using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SurveyAnalysis.Llm;

// The single seam every feature consumer (sentiment / topics / OCR / report / the future C#
// clustering) depends on — never the concrete HTTP client. Resolve the shared instance from
// AppServices.Llm. Chat carries its model per request (so each use can pick its own model);
// embeddings use the configured embedding model.
public interface IChatClient
{
    Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct = default);
}

public interface IEmbeddingClient
{
    // One vector per input, order-preserving. Internally batched, parallelised, and cached.
    Task<IReadOnlyList<EmbeddingVector>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default);
}

public interface ILlmClient : IChatClient, IEmbeddingClient
{
}
