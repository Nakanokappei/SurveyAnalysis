namespace SurveyAnalysis.Llm.Cache;

// Persistent cache for embedding vectors and chat responses, keyed by HashKey-derived keys. Lets the
// client be tested with an in-memory fake and lets caching be turned off (NullLlmCache).
public interface ILlmCache
{
    bool TryGetEmbedding(string cacheKey, out float[]? vector);
    void PutEmbedding(string cacheKey, string provider, string model, float[] vector);

    bool TryGetChat(string cacheKey, out ChatResult? result);
    void PutChat(string cacheKey, string provider, string model, ChatResult result);
}

// Caching disabled: every lookup misses, every store is a no-op.
public sealed class NullLlmCache : ILlmCache
{
    public bool TryGetEmbedding(string cacheKey, out float[]? vector) { vector = null; return false; }
    public void PutEmbedding(string cacheKey, string provider, string model, float[] vector) { }
    public bool TryGetChat(string cacheKey, out ChatResult? result) { result = null; return false; }
    public void PutChat(string cacheKey, string provider, string model, ChatResult result) { }
}
