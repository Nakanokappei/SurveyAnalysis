using System.Collections.Generic;
using System.Linq;
using SurveyAnalysis.Llm;
using Xunit;

namespace SurveyAnalysis.Tests;

// Cache-key derivation: identical requests share a key; anything that changes the response (provider,
// model, inputs, params, message boundaries) changes the key.
public class HashKeyTests
{
    private const string Ep = "https://api.openai.com/v1";

    private static IReadOnlyList<ChatMessage> Msg(params (string Role, string Content)[] m)
        => m.Select(x => new ChatMessage(x.Role, x.Content)).ToList();

    [Fact]
    public void Embedding_key_is_stable_and_provider_sensitive()
    {
        var k = HashKey.ForEmbedding(Ep, "text-embedding-3-small", "hello");
        Assert.Equal(k, HashKey.ForEmbedding(Ep, "text-embedding-3-small", "hello"));            // stable
        Assert.NotEqual(k, HashKey.ForEmbedding(Ep, "text-embedding-3-small", "world"));         // input
        Assert.NotEqual(k, HashKey.ForEmbedding("http://localhost:1234/v1", "text-embedding-3-small", "hello")); // endpoint
        Assert.NotEqual(k, HashKey.ForEmbedding(Ep, "text-embedding-3-large", "hello"));         // model
    }

    [Fact]
    public void Chat_key_is_stable_and_parameter_sensitive()
    {
        var msgs = Msg(("system", "S"), ("user", "U"));
        var k = HashKey.ForChat(Ep, "gpt-4o", msgs, 0.2, 400, "json_object", 7);
        Assert.Equal(k, HashKey.ForChat(Ep, "gpt-4o", Msg(("system", "S"), ("user", "U")), 0.2, 400, "json_object", 7));

        Assert.NotEqual(k, HashKey.ForChat(Ep, "gpt-4o-mini", msgs, 0.2, 400, "json_object", 7)); // model
        Assert.NotEqual(k, HashKey.ForChat(Ep, "gpt-4o", msgs, 0.3, 400, "json_object", 7));       // temperature
        Assert.NotEqual(k, HashKey.ForChat(Ep, "gpt-4o", msgs, 0.2, 401, "json_object", 7));       // maxTokens
        Assert.NotEqual(k, HashKey.ForChat(Ep, "gpt-4o", msgs, 0.2, 400, null, 7));                // responseFormat
        Assert.NotEqual(k, HashKey.ForChat(Ep, "gpt-4o", msgs, 0.2, 400, "json_object", 8));       // seed
        Assert.NotEqual(k, HashKey.ForChat(Ep, "gpt-4o", Msg(("user", "U"), ("system", "S")), 0.2, 400, "json_object", 7)); // order
        Assert.NotEqual(k, HashKey.ForChat(Ep, "gpt-4o", Msg(("system", "S"), ("assistant", "U")), 0.2, 400, "json_object", 7)); // role
    }

    [Fact]
    public void Null_parameter_differs_from_default_value()
    {
        var msgs = Msg(("user", "U"));
        Assert.NotEqual(
            HashKey.ForChat(Ep, "gpt-4o", msgs, null, null, null, null),
            HashKey.ForChat(Ep, "gpt-4o", msgs, 0.0, 0, "-", 0));
    }

    [Fact]
    public void Length_prefix_prevents_message_boundary_collisions()
    {
        // Without length-prefixing, ["a","bc"] and ["ab","c"] could concatenate identically.
        Assert.NotEqual(
            HashKey.ForChat(Ep, "gpt-4o", Msg(("user", "a"), ("user", "bc")), null, null, null, null),
            HashKey.ForChat(Ep, "gpt-4o", Msg(("user", "ab"), ("user", "c")), null, null, null, null));
    }
}
