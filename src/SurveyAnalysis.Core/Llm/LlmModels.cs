using System.Collections.Generic;

namespace SurveyAnalysis.Llm;

// Provider-neutral DTOs that cross the ILlmClient boundary. The OpenAI wire shapes live in Wire/
// and are never exposed here, so a future provider swap does not ripple into consumers.

// One chat message. Role is "system" | "user" | "assistant". ImageDataUrls is null/empty for an
// ordinary text message (the wire then sends content as a plain string, backward-compatible); when it
// carries one or more "data:<mime>;base64,..." URLs the wire sends content as a multimodal parts array
// (the Content text first, then each image) for a vision request (OCR).
public sealed record ChatMessage(string Role, string Content, IReadOnlyList<string>? ImageDataUrls = null);

// A chat completion request. The model travels with the request so a single shared client serves
// every use (OCR / topic / sentiment / report). ResponseFormat "json_object" asks for JSON mode;
// null omits it. Temperature / MaxTokens / Seed are omitted from the wire when null.
public sealed record ChatRequest(
    string Model,
    IReadOnlyList<ChatMessage> Messages,
    double? Temperature = null,
    int? MaxTokens = null,
    string? ResponseFormat = null,
    int? Seed = null);

// The assistant's reply plus token usage (when the server reports it). FromCache is true when the
// result was served from the local cache without an HTTP call.
public sealed record ChatResult(
    string Content,
    string Model,
    int? PromptTokens,
    int? CompletionTokens,
    bool FromCache);

// One embedding vector (float32, ready for the future C# clustering). FromCache marks a cache hit.
public sealed record EmbeddingVector(float[] Values, bool FromCache);
