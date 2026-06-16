using System;

namespace SurveyAnalysis.Llm.Cache;

// SQLite-backed cache over LlmCacheDatabase. Opens a short-lived connection per operation (matching
// the repository style elsewhere). Writes use INSERT OR IGNORE: an identical key implies an identical
// value, so concurrent double-writes are harmless (first writer wins).
public sealed class SqliteLlmCache : ILlmCache
{
    private readonly LlmCacheDatabase _db;

    public SqliteLlmCache(LlmCacheDatabase db) => _db = db;

    public bool TryGetEmbedding(string cacheKey, out float[]? vector)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT dim, vector FROM embedding_cache WHERE cache_key = $k;";
        command.Parameters.AddWithValue("$k", cacheKey);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            vector = null;
            return false;
        }

        var dim = reader.GetInt32(0);
        var blob = reader.GetFieldValue<byte[]>(1);
        // Guard against a corrupt/mismatched row rather than returning a wrong-length vector.
        if (blob.Length != dim * sizeof(float))
        {
            vector = null;
            return false;
        }

        vector = new float[dim];
        Buffer.BlockCopy(blob, 0, vector, 0, blob.Length);
        return true;
    }

    public void PutEmbedding(string cacheKey, string provider, string model, float[] vector)
    {
        var blob = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, blob, 0, blob.Length);

        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO embedding_cache (cache_key, provider, model, dim, vector, created_utc)
            VALUES ($k, $p, $m, $d, $v, $now);
            """;
        command.Parameters.AddWithValue("$k", cacheKey);
        command.Parameters.AddWithValue("$p", provider);
        command.Parameters.AddWithValue("$m", model);
        command.Parameters.AddWithValue("$d", vector.Length);
        command.Parameters.AddWithValue("$v", blob);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        command.ExecuteNonQuery();
    }

    public bool TryGetChat(string cacheKey, out ChatResult? result)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT model, content, prompt_tokens, completion_tokens FROM chat_cache WHERE cache_key = $k;";
        command.Parameters.AddWithValue("$k", cacheKey);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            result = null;
            return false;
        }

        var model = reader.GetString(0);
        var content = reader.GetString(1);
        int? promptTokens = reader.IsDBNull(2) ? null : reader.GetInt32(2);
        int? completionTokens = reader.IsDBNull(3) ? null : reader.GetInt32(3);
        result = new ChatResult(content, model, promptTokens, completionTokens, FromCache: true);
        return true;
    }

    public void PutChat(string cacheKey, string provider, string model, ChatResult result)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO chat_cache (cache_key, provider, model, content, prompt_tokens, completion_tokens, created_utc)
            VALUES ($k, $p, $m, $c, $pt, $ct, $now);
            """;
        command.Parameters.AddWithValue("$k", cacheKey);
        command.Parameters.AddWithValue("$p", provider);
        command.Parameters.AddWithValue("$m", model);
        command.Parameters.AddWithValue("$c", result.Content);
        command.Parameters.AddWithValue("$pt", (object?)result.PromptTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$ct", (object?)result.CompletionTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        command.ExecuteNonQuery();
    }
}
