using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SurveyAnalysis.Llm;

// Derives stable cache keys (lowercase-hex SHA-256) for embedding and chat requests. Provider
// identity (normalized endpoint + model) is always part of the hashed material, so switching
// endpoint or model can never return a stale cached value. Every field is length-prefixed
// ("<len>:<value>") before hashing, which makes the concatenation unambiguous regardless of what the
// content contains — two distinct inputs can never produce the same byte string.
public static class HashKey
{
    // Per-input embedding key (so a batch yields one key per input and partial cache hits work).
    public static string ForEmbedding(string normalizedEndpoint, string model, string input)
    {
        var sb = new StringBuilder("emb");
        Append(sb, normalizedEndpoint);
        Append(sb, model);
        Append(sb, input);
        return Hash(sb.ToString());
    }

    // Chat key over every field that changes the response: provider, model, the full message list,
    // and the sampling parameters. A null parameter serializes as "-" so present-but-default differs
    // from absent consistently. The message count is encoded so message boundaries are unambiguous.
    public static string ForChat(
        string normalizedEndpoint,
        string model,
        IReadOnlyList<ChatMessage> messages,
        double? temperature,
        int? maxTokens,
        string? responseFormat,
        int? seed)
    {
        var sb = new StringBuilder("chat");
        Append(sb, normalizedEndpoint);
        Append(sb, model);
        Append(sb, messages.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var message in messages)
        {
            Append(sb, message.Role);
            Append(sb, message.Content);
        }
        Append(sb, Num(temperature));
        Append(sb, Num(maxTokens));
        Append(sb, responseFormat ?? "-");
        Append(sb, Num(seed));
        return Hash(sb.ToString());
    }

    // Length-prefixed field append: "<len>:<value>". The explicit length removes any dependence on a
    // separator that could appear inside the value.
    private static void Append(StringBuilder sb, string field)
        => sb.Append(field.Length).Append(':').Append(field);

    private static string Num(double? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "-";
    private static string Num(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "-";

    private static string Hash(string material)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
}
