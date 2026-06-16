using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace SurveyAnalysis.Data;

// How many backup generations to keep. A backup is taken when a project is closed (and on exit while a
// project is open); the older ones are pruned to a grandfather-father-son scheme: one per recent day,
// then one per older week. The default (Standard) is the 10-file policy: the last 7 days plus the 1/2/3
// weeks-ago snapshots.
public enum BackupRetention
{
    Off,        // do not back up
    Few,        // 3 daily + 1 weekly
    Standard,   // 7 daily + 3 weekly = 10
    Many,       // 14 daily + 8 weekly
}

// Resolves a retention enum to its (daily, weekly) tier sizes and parses the stored setting string.
public static class BackupRetentionPolicy
{
    public static (int DailyDays, int WeeklyWeeks) Tiers(BackupRetention retention) => retention switch
    {
        BackupRetention.Off => (0, 0),
        BackupRetention.Few => (3, 1),
        BackupRetention.Standard => (7, 3),
        BackupRetention.Many => (14, 8),
        _ => (7, 3),
    };

    // Parses the persisted setting; anything unrecognised falls back to the default (Standard).
    public static BackupRetention Parse(string? value) =>
        Enum.TryParse<BackupRetention>(value, out var retention) ? retention : BackupRetention.Standard;
}

// Backup / optimize / restore for the single application database file. UI-independent: the WinForms
// host shows the busy cursor and confirmation dialogs around these calls. Backups live in a `backups`
// subfolder next to the database, named with a sortable timestamp.
public sealed class DatabaseMaintenance
{
    private readonly string _databasePath;

    public DatabaseMaintenance(string databasePath) => _databasePath = databasePath;

    public string DatabasePath => _databasePath;
    public string BackupFolder => Path.Combine(Path.GetDirectoryName(_databasePath)!, "backups");

    private const string Prefix = "surveyanalysis-";
    private const string Extension = ".db";
    private const string TimestampFormat = "yyyyMMdd-HHmmss";
    private const int TimestampLength = 15;   // yyyyMMdd(8) + '-'(1) + HHmmss(6)

    // Copies the database into the backups folder under a timestamped name, then prunes older backups to
    // the retention policy. Returns the new backup's path, or null when retention is Off (no-op). `now`
    // is injected so the filename and pruning agree and tests are deterministic.
    public string? Backup(BackupRetention retention, DateTime now)
    {
        if (retention == BackupRetention.Off)
            return null;

        Directory.CreateDirectory(BackupFolder);
        var destination = Path.Combine(BackupFolder, $"{Prefix}{now.ToString(TimestampFormat)}{Extension}");
        if (!File.Exists(destination))   // a second backup within the same second is redundant
            CopyDatabase(_databasePath, destination);

        PruneBackups(retention, now);
        return destination;
    }

    // Rebuilds the database file to reclaim free pages and defragment it (the "最適化" action). Safe to run
    // with idle pooled connections present — VACUUM only needs that no other connection holds a lock.
    public void Optimize()
    {
        using var connection = Open(_databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "VACUUM;";
        command.ExecuteNonQuery();
    }

    // The backup files, newest first. The timestamped name sorts chronologically, so a descending name
    // sort is newest-first.
    public IReadOnlyList<string> ListBackups()
    {
        if (!Directory.Exists(BackupFolder))
            return Array.Empty<string>();
        return Directory.GetFiles(BackupFolder, Prefix + "*" + Extension)
            .OrderByDescending(path => path, StringComparer.Ordinal)
            .ToList();
    }

    // Replaces the live database with the chosen backup. The current database is first copied aside (a
    // pre-restore safety copy), then pooled handles are released so the file can be overwritten. The
    // caller restarts the app afterwards so every connection reopens against the restored file.
    public void Restore(string backupPath, DateTime now)
    {
        Directory.CreateDirectory(BackupFolder);
        var safety = Path.Combine(BackupFolder, $"{Prefix}{now.ToString(TimestampFormat)}-prerestore{Extension}");

        SqliteConnection.ClearAllPools();   // release file handles held by the pool
        if (File.Exists(_databasePath) && !File.Exists(safety))
            File.Copy(_databasePath, safety);
        File.Copy(backupPath, _databasePath, overwrite: true);
    }

    // Deletes backups outside the retention window: keeps the newest backup of each of the last
    // `DailyDays` calendar days, plus the newest in each subsequent weekly window. Public for tests.
    public void PruneBackups(BackupRetention retention, DateTime now)
    {
        var (dailyDays, weeklyWeeks) = BackupRetentionPolicy.Tiers(retention);

        var keep = new HashSet<string>();
        var slotsTaken = new HashSet<string>();
        foreach (var path in ListBackups())   // newest first → the first file seen in a slot is its newest
        {
            if (!TryParseTimestamp(path, out var timestamp))
            {
                keep.Add(path);   // unrecognised name: keep rather than risk deleting something foreign
                continue;
            }
            var slot = SlotFor(timestamp, now, dailyDays, weeklyWeeks);
            if (slot is null)
                continue;                       // outside every tier → prune
            if (slotsTaken.Add(slot))
                keep.Add(path);                 // newest in this slot
        }

        foreach (var path in ListBackups())
            if (!keep.Contains(path))
                TryDelete(path);
    }

    // Maps a backup's date to its retention slot, or null if it falls outside all tiers. Daily slots
    // cover ages 0..DailyDays-1 (one per calendar day); weekly slots follow in 7-day windows.
    private static string? SlotFor(DateTime timestamp, DateTime now, int dailyDays, int weeklyWeeks)
    {
        var age = (now.Date - timestamp.Date).Days;
        if (age < 0)
            age = 0;                            // a future-stamped file counts as today
        if (age < dailyDays)
            return "D" + age;
        var week = (age - dailyDays) / 7 + 1;
        return week <= weeklyWeeks ? "W" + week : null;
    }

    private static bool TryParseTimestamp(string path, out DateTime timestamp)
    {
        timestamp = default;
        var name = Path.GetFileNameWithoutExtension(path);
        if (!name.StartsWith(Prefix, StringComparison.Ordinal) || name.Length < Prefix.Length + TimestampLength)
            return false;
        var stamp = name.Substring(Prefix.Length, TimestampLength);   // ignores any "-prerestore" suffix
        return DateTime.TryParseExact(stamp, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { /* best effort: a backup we cannot delete is harmless */ }
    }

    private static void CopyDatabase(string sourcePath, string destinationPath)
    {
        using var source = Open(sourcePath);
        using var destination = Open(destinationPath);
        source.BackupDatabase(destination);   // online backup API: consistent regardless of journal state
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        return connection;
    }
}
