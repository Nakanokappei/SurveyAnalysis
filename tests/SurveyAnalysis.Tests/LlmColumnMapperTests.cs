using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SurveyAnalysis.Llm;
using SurveyAnalysis.Llm.Consumers;
using Xunit;

namespace SurveyAnalysis.Tests;

// The LLM column mapper turns the chat reply into column→field suggestions, keeping only entries that name
// a real CSV column and a real candidate field.
public class LlmColumnMapperTests
{
    private static readonly (string Column, IReadOnlyList<string> Samples)[] Columns =
    {
        ("連絡先", new[] { "090-0000-0000" }),
        ("雑記", new[] { "..." }),
        ("日付", new[] { "2026/05/20" }),
    };
    private static readonly (string Field, string TypeLabel)[] Fields =
    {
        ("電話番号", "電話番号"),
        ("記入日", "日付"),
    };

    [Fact]
    public async Task MapAsync_keeps_only_valid_column_and_field_names()
    {
        var chat = new FakeChat("{\"連絡先\":\"電話番号\",\"雑記\":\"存在しない項目\",\"日付\":\"記入日\"}");

        var result = await new LlmColumnMapper(chat, "m").MapAsync(Columns, Fields);

        Assert.Equal("電話番号", result["連絡先"]);
        Assert.Equal("記入日", result["日付"]);
        Assert.False(result.ContainsKey("雑記"));   // mapped to an unknown field → dropped
    }

    [Fact]
    public async Task MapAsync_returns_empty_on_unparseable_reply()
    {
        var result = await new LlmColumnMapper(new FakeChat("not json"), "m").MapAsync(Columns, Fields);
        Assert.Empty(result);
    }

    private sealed class FakeChat : IChatClient
    {
        private readonly string _reply;
        public FakeChat(string reply) => _reply = reply;
        public Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct = default) =>
            Task.FromResult(new ChatResult(_reply, request.Model, null, null, false));
    }
}
