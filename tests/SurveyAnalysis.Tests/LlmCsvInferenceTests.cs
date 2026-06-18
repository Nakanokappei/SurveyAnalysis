using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SurveyAnalysis.Llm;
using SurveyAnalysis.Llm.Consumers;
using SurveyAnalysis.Models;
using Xunit;

namespace SurveyAnalysis.Tests;

// LLM-based CSV inference: a project description + per-column types in one call. Maps the model's type
// labels back to FieldType, tolerates a flat (no "types" wrapper) reply, skips unknown labels / columns,
// and yields no overrides + empty description on bad JSON (so the caller keeps its heuristic guess).
public class LlmCsvInferenceTests
{
    private static readonly string[] Headers = { "氏名", "記入日", "満足度", "ご意見" };
    private static readonly IReadOnlyList<IReadOnlyList<string>> Rows = new[]
    {
        new[] { "山田太郎", "2026/05/20", "とても満足", "丁寧でした。" },
    };

    [Fact]
    public async Task Returns_description_and_maps_types()
    {
        var chat = new FakeChat("""
            {"description":"工事後の顧客満足アンケート。","types":{"氏名":"氏名","記入日":"日付","満足度":"選択肢","ご意見":"テキスト（改行あり）"}}
            """);
        var result = await new LlmCsvInference(chat, "m").InferAsync(Headers, Rows, "");

        Assert.Equal("工事後の顧客満足アンケート。", result.Description);
        Assert.Equal(FieldType.Name, result.Types["氏名"]);
        Assert.Equal(FieldType.Date, result.Types["記入日"]);
        Assert.Equal(FieldType.Choice, result.Types["満足度"]);
        Assert.Equal(FieldType.FreeText, result.Types["ご意見"]);
        Assert.Equal("json_object", chat.LastRequest!.ResponseFormat);
        Assert.Equal(2, chat.LastRequest!.Messages.Count);
    }

    [Fact]
    public async Task Tolerates_a_flat_types_object_without_wrapper()
    {
        var chat = new FakeChat("""{"氏名":"氏名","記入日":"日付"}""");
        var result = await new LlmCsvInference(chat, "m").InferAsync(Headers, Rows, "");

        Assert.Equal("", result.Description);
        Assert.Equal(FieldType.Name, result.Types["氏名"]);
        Assert.Equal(FieldType.Date, result.Types["記入日"]);
    }

    [Fact]
    public async Task Skips_unknown_labels_and_unknown_columns()
    {
        var chat = new FakeChat("""{"types":{"氏名":"なんとか型","記入日":"日付","存在しない列":"数値"}}""");
        var result = await new LlmCsvInference(chat, "m").InferAsync(Headers, Rows, "");

        Assert.False(result.Types.ContainsKey("氏名"));          // unknown label → skipped
        Assert.False(result.Types.ContainsKey("存在しない列"));   // not a header → skipped
        Assert.Equal(FieldType.Date, result.Types["記入日"]);
    }

    [Fact]
    public async Task Bad_json_yields_no_overrides_and_no_description()
    {
        var chat = new FakeChat("これはJSONではありません");
        var result = await new LlmCsvInference(chat, "m").InferAsync(Headers, Rows, "");
        Assert.Empty(result.Types);
        Assert.Equal("", result.Description);
    }

    private sealed class FakeChat : IChatClient
    {
        private readonly string _content;
        public ChatRequest? LastRequest;

        public FakeChat(string content) => _content = content;

        public Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new ChatResult(_content, request.Model, null, null, false));
        }
    }
}
