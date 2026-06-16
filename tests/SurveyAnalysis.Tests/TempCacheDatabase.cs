using System;
using System.IO;
using SurveyAnalysis.Llm.Cache;

namespace SurveyAnalysis.Tests;

// A throwaway LLM cache database on a unique temp path, schema created. Disposing deletes the file
// (and the WAL/SHM sidecars) so tests stay isolated. Mirrors TempDatabase.
internal sealed class TempCacheDatabase : IDisposable
{
    public LlmCacheDatabase Db { get; }
    public string Path { get; }

    public TempCacheDatabase()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"satest_llmcache_{Guid.NewGuid():N}.db");
        Db = new LlmCacheDatabase(Path);
        Db.EnsureSchema();
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(Path + suffix); } catch { /* best effort */ }
        }
    }
}
