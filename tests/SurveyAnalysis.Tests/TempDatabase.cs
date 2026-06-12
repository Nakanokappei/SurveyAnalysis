using System;
using System.IO;
using SurveyAnalysis.Data;

namespace SurveyAnalysis.Tests;

// A throwaway SQLite database on a unique temp-file path, with the schema created. Disposing
// deletes the file so tests stay isolated from one another and from the real app database.
internal sealed class TempDatabase : IDisposable
{
    public AppDatabase Db { get; }
    private readonly string _path;

    public TempDatabase()
    {
        _path = Path.Combine(Path.GetTempPath(), $"satest_{Guid.NewGuid():N}.db");
        Db = new AppDatabase(_path);
        Db.EnsureSchema();
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
        catch
        {
            // Best effort: a leftover temp file is harmless.
        }
    }
}
