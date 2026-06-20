using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SurveyAnalysis.Llm.Wire;

// The exact OpenAI / OpenAI-compatible JSON shapes for chat-completions and embeddings. Kept internal
// and separate from the public DTOs so the wire format never leaks to consumers. Null request fields
// are omitted on serialize (see OpenAiJsonContext); responses tolerate missing fields.

internal sealed class ChatMessageWire
{
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    // Either a plain string (text-only message) or a List<ChatContentPartWire> (multimodal: text +
    // images). Typed as object so the same message shape carries both; the runtime type is resolved
    // against this context, which registers string and the parts list below.
    [JsonPropertyName("content")] public object? Content { get; set; }
}

// One part of a multimodal message: a text part ({"type":"text","text":...}) or an image part
// ({"type":"image_url","image_url":{"url":"data:..."}}). The unused sibling is null and dropped on
// write (WhenWritingNull), so each part serializes to exactly the OpenAI vision shape.
internal sealed class ChatContentPartWire
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("image_url")] public ChatImageUrlWire? ImageUrl { get; set; }
}

internal sealed class ChatImageUrlWire
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
}

internal sealed class ResponseFormatWire
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
}

internal sealed class ChatRequestWire
{
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("messages")] public List<ChatMessageWire> Messages { get; set; } = new();
    [JsonPropertyName("temperature")] public double? Temperature { get; set; }
    [JsonPropertyName("max_tokens")] public int? MaxTokens { get; set; }
    [JsonPropertyName("response_format")] public ResponseFormatWire? ResponseFormat { get; set; }
    [JsonPropertyName("seed")] public int? Seed { get; set; }
    [JsonPropertyName("stream")] public bool Stream { get; set; }
}

// The assistant's reply message: content is always a plain string (text), so it stays typed as string
// rather than the request side's polymorphic object. refusal carries the model's reason when it declines
// (content is then null) — surfaced in the error so an empty reply is explainable rather than opaque.
internal sealed class ChatResponseMessageWire
{
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("refusal")] public string? Refusal { get; set; }
}

internal sealed class ChatChoiceWire
{
    [JsonPropertyName("message")] public ChatResponseMessageWire? Message { get; set; }
    // Why generation stopped (stop / length / content_filter); reported when the content is empty.
    [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
}

internal sealed class ChatUsageWire
{
    [JsonPropertyName("prompt_tokens")] public int? PromptTokens { get; set; }
    [JsonPropertyName("completion_tokens")] public int? CompletionTokens { get; set; }
}

internal sealed class ChatResponseWire
{
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("choices")] public List<ChatChoiceWire>? Choices { get; set; }
    [JsonPropertyName("usage")] public ChatUsageWire? Usage { get; set; }
}

internal sealed class EmbeddingRequestWire
{
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("input")] public List<string> Input { get; set; } = new();
}

internal sealed class EmbeddingDatumWire
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("embedding")] public float[]? Embedding { get; set; }
}

internal sealed class EmbeddingResponseWire
{
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("data")] public List<EmbeddingDatumWire>? Data { get; set; }
}

// Source-generated (reflection-free) serialization. Null fields are dropped on write so the request
// body omits temperature/max_tokens/etc. when unset.
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ChatRequestWire))]
[JsonSerializable(typeof(ChatResponseWire))]
[JsonSerializable(typeof(EmbeddingRequestWire))]
[JsonSerializable(typeof(EmbeddingResponseWire))]
// Registered so the object-typed message content resolves to these runtime types (a plain string is
// a built-in converter; a multimodal message serializes through the parts list).
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(List<ChatContentPartWire>))]
internal sealed partial class OpenAiJsonContext : JsonSerializerContext
{
}
