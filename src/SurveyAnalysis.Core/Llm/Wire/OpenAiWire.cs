using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SurveyAnalysis.Llm.Wire;

// The exact OpenAI / OpenAI-compatible JSON shapes for chat-completions and embeddings. Kept internal
// and separate from the public DTOs so the wire format never leaks to consumers. Null request fields
// are omitted on serialize (see OpenAiJsonContext); responses tolerate missing fields.

internal sealed class ChatMessageWire
{
    [JsonPropertyName("role")] public string Role { get; set; } = "";
    [JsonPropertyName("content")] public string? Content { get; set; }
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

internal sealed class ChatChoiceWire
{
    [JsonPropertyName("message")] public ChatMessageWire? Message { get; set; }
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
internal sealed partial class OpenAiJsonContext : JsonSerializerContext
{
}
