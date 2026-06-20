using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SurveyAnalysis.Llm;
using SurveyAnalysis.Llm.Consumers;
using SurveyAnalysis.Models;
using Xunit;

namespace SurveyAnalysis.Tests;

// The OCR extractor turns a (fake) vision reply into a 項目名→値 map and then into a response, attaching
// the image to the chat request so the client sends a multimodal call.
public class OcrExtractorTests
{
    private static IReadOnlyList<DataField> Fields() => new[]
    {
        new DataField { Name = "記入日", FieldType = FieldType.Date },
        new DataField { Name = "氏名", FieldType = FieldType.Name },
        new DataField { Name = "満足度", FieldType = FieldType.Number },
        new DataField { Name = "ご意見", FieldType = FieldType.FreeText },
    };

    [Fact]
    public async Task Extracts_known_fields_and_attaches_the_image()
    {
        var llm = new CaptureChat("""
            {"記入日":"2026/05/20","氏名":"山田太郎","満足度":4,"ご意見":"対応が丁寧でした","不明な列":"x"}
            """);

        var values = await new OcrExtractor(llm, "gpt-4o").ExtractAsync(
            new byte[] { 1, 2, 3 }, "image/png", Fields(), "工事アンケート");

        // When the model complies, every field is read — string, numeric (kept as text), free-text, and the
        // personal-information field (氏名) too; an unknown key is ignored.
        Assert.Equal("2026/05/20", values["記入日"]);
        Assert.Equal("山田太郎", values["氏名"]);
        Assert.Equal("4", values["満足度"]);
        Assert.Equal("対応が丁寧でした", values["ご意見"]);
        Assert.False(values.ContainsKey("不明な列"));

        // The user message carried exactly one base64 data URL for the image bytes.
        var userMessage = llm.LastRequest!.Messages.Single(m => m.Role == "user");
        Assert.NotNull(userMessage.ImageDataUrls);
        var dataUrl = Assert.Single(userMessage.ImageDataUrls!);
        Assert.Equal("data:image/png;base64," + System.Convert.ToBase64String(new byte[] { 1, 2, 3 }), dataUrl);
    }

    [Fact]
    public async Task Blank_values_are_omitted_and_build_response_maps_by_name()
    {
        var llm = new CaptureChat("""{"記入日":"2026/05/20","氏名":"","満足度":"","ご意見":"良い"}""");

        var values = await new OcrExtractor(llm, "gpt-4o").ExtractAsync(
            new byte[] { 9 }, "image/jpeg", Fields(), "");

        Assert.False(values.ContainsKey("氏名"));   // blank → omitted
        Assert.False(values.ContainsKey("満足度"));

        var response = OcrExtractor.BuildResponse(values, Fields());
        Assert.Equal(new[] { "記入日", "ご意見" }, response.Answers.Select(a => a.FieldName).ToArray());
        Assert.Equal("良い", response.Answers.Single(a => a.FieldName == "ご意見").Value);
    }

    [Fact]
    public async Task Unparseable_reply_yields_no_values()
    {
        var values = await new OcrExtractor(new CaptureChat("not json"), "gpt-4o").ExtractAsync(
            new byte[] { 1 }, "image/png", Fields(), "");
        Assert.Empty(values);
    }

    [Fact]
    public async Task Choice_options_are_listed_inline_for_choice_fields_only()
    {
        var llm = new CaptureChat("{}");
        var fields = new[]
        {
            new DataField { Name = "加入サービス", FieldType = FieldType.Choice },
            new DataField { Name = "ご意見", FieldType = FieldType.FreeText },
        };
        var options = new Dictionary<string, IReadOnlyList<string>>
        {
            ["加入サービス"] = new[] { "テレビ", "インターネット", "固定電話" },
        };

        await new OcrExtractor(llm, "gpt-4o").ExtractAsync(new byte[] { 1 }, "image/png", fields, "", options);

        var prompt = llm.LastRequest!.Messages.Single(m => m.Role == "user").Content;
        Assert.Contains("テレビ / インターネット / 固定電話", prompt);   // options inlined for the choice field
        Assert.DoesNotContain("ご意見（選択肢", prompt);                 // free-text field gets no option list
    }

    [Fact]
    public async Task Refusal_drops_the_PII_fields_and_reads_the_rest()
    {
        // The first call (all fields) is refused; the retry (non-PII only) succeeds — so the PII field is
        // dropped, the rest is read, and the retry's prompt no longer mentions the PII field.
        var llm = new RefuseThenAnswer("""{"記入日":"2026/05/20","満足度":4,"ご意見":"丁寧でした"}""");

        var values = await new OcrExtractor(llm, "gpt-4o").ExtractAsync(
            new byte[] { 1 }, "image/png", Fields(), "");

        Assert.False(values.ContainsKey("氏名"));   // PII dropped after the refusal
        Assert.Equal("2026/05/20", values["記入日"]);
        Assert.Equal("丁寧でした", values["ご意見"]);

        Assert.Equal(2, llm.Calls);
        Assert.DoesNotContain("氏名", llm.LastRequest!.Messages.Single(m => m.Role == "user").Content);
    }

    // A chat client that records the last request and replies with a fixed body.
    private sealed class CaptureChat : IChatClient
    {
        private readonly string _reply;
        public ChatRequest? LastRequest { get; private set; }
        public CaptureChat(string reply) => _reply = reply;

        public Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new ChatResult(_reply, request.Model, null, null, false));
        }
    }

    // A chat client that refuses (empty response) on the first call, then answers — to exercise the OCR's
    // fall-back-without-PII path.
    private sealed class RefuseThenAnswer : IChatClient
    {
        private readonly string _reply;
        public int Calls { get; private set; }
        public ChatRequest? LastRequest { get; private set; }
        public RefuseThenAnswer(string reply) => _reply = reply;

        public Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct = default)
        {
            Calls++;
            LastRequest = request;
            if (Calls == 1)
                throw new LlmEmptyResponseException("refused", "モデルが応答を拒否しました");
            return Task.FromResult(new ChatResult(_reply, request.Model, null, null, false));
        }
    }
}
