using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using SurveyAnalysis.Data;
using SurveyAnalysis.Models;
using Xunit;

namespace SurveyAnalysis.Tests;

// Backup / optimize / restore and the grandfather-father-son retention pruning. Each test runs in its
// own temp directory so the `backups` subfolder (derived from the database's folder) stays isolated.
public class DatabaseMaintenanceTests
{
    private static readonly DateTime Now = new(2026, 6, 16, 12, 0, 0);

    private static string BackupName(DateTime ts) => $"surveyanalysis-{ts:yyyyMMdd-HHmmss}.db";

    [Fact]
    public void Backup_creates_a_copy_and_Restore_replaces_the_live_database()
    {
        using var temp = new TempDir();
        var projects = new ProjectRepository(temp.Db);

        projects.Insert(new Project { Name = "before" });
        var backup = temp.Maintenance.Backup(BackupRetention.Standard, Now);
        Assert.NotNull(backup);
        Assert.True(File.Exists(backup!));

        // The live database moves on, then we restore the earlier snapshot.
        projects.Insert(new Project { Name = "after" });
        Assert.Equal(2, projects.ListSummaries().Count);

        temp.Maintenance.Restore(backup!, Now.AddHours(1));

        var summaries = projects.ListSummaries();
        Assert.Single(summaries);
        Assert.Equal("before", summaries[0].Name);
    }

    [Fact]
    public void Optimize_runs_without_error()
    {
        using var temp = new TempDir();
        new ProjectRepository(temp.Db).Insert(new Project { Name = "p" });
        temp.Maintenance.Optimize();   // VACUUM; throws on failure
    }

    [Fact]
    public void Off_retention_does_not_create_a_backup()
    {
        using var temp = new TempDir();
        Assert.Null(temp.Maintenance.Backup(BackupRetention.Off, Now));
        Assert.Empty(temp.Maintenance.ListBackups());
    }

    [Fact]
    public void Prune_keeps_the_standard_ten_generations()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Maintenance.BackupFolder);

        // Seed backups at a spread of ages (days ago), plus an earlier one today.
        int[] ages = { 0, 1, 2, 3, 4, 5, 6, 7, 10, 14, 21, 28, 40 };
        foreach (var age in ages)
            Seed(temp, Now.AddDays(-age));
        Seed(temp, Now.AddHours(-3));   // a second backup earlier today

        temp.Maintenance.PruneBackups(BackupRetention.Standard, Now);

        var remaining = Directory.GetFiles(temp.Maintenance.BackupFolder).Select(Path.GetFileName).ToHashSet();

        // Daily tier: ages 0-6 kept; today keeps the newest of its two files.
        Assert.Contains(BackupName(Now), remaining);
        Assert.DoesNotContain(BackupName(Now.AddHours(-3)), remaining);
        foreach (var age in new[] { 1, 2, 3, 4, 5, 6 })
            Assert.Contains(BackupName(Now.AddDays(-age)), remaining);

        // Weekly tier: 1/2/3 weeks ago kept; the older same-bucket (day 10) and anything past 3 weeks gone.
        Assert.Contains(BackupName(Now.AddDays(-7)), remaining);
        Assert.Contains(BackupName(Now.AddDays(-14)), remaining);
        Assert.Contains(BackupName(Now.AddDays(-21)), remaining);
        Assert.DoesNotContain(BackupName(Now.AddDays(-10)), remaining);
        Assert.DoesNotContain(BackupName(Now.AddDays(-28)), remaining);
        Assert.DoesNotContain(BackupName(Now.AddDays(-40)), remaining);

        Assert.Equal(10, remaining.Count);
    }

    private static void Seed(TempDir temp, DateTime timestamp) =>
        File.WriteAllText(Path.Combine(temp.Maintenance.BackupFolder, BackupName(timestamp)), "x");

    // A throwaway database in its own temp directory, with maintenance wired to it. Disposing releases
    // pooled file handles and removes the whole directory (database + backups).
    private sealed class TempDir : IDisposable
    {
        private readonly string _root;
        public AppDatabase Db { get; }
        public DatabaseMaintenance Maintenance { get; }

        public TempDir()
        {
            _root = Path.Combine(Path.GetTempPath(), "satest_maint_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            var dbPath = Path.Combine(_root, "surveyanalysis.db");
            Db = new AppDatabase(dbPath);
            Db.EnsureSchema();
            Maintenance = new DatabaseMaintenance(dbPath);
        }

        public void Dispose()
        {
            try
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // Best effort: a leftover temp directory is harmless.
            }
        }
    }
}
