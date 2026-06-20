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

        // String, numeric (kept as text) and free-text fields are read; an unknown key is ignored.
        Assert.Equal("2026/05/20", values["記入日"]);
        Assert.Equal("4", values["満足度"]);
        Assert.Equal("対応が丁寧でした", values["ご意見"]);
        Assert.False(values.ContainsKey("不明な列"));

        // The personal-information field (氏名 = Name) is never sent to OCR, so even though the (fake) model
        // returned a value for it, it is excluded from the result and from the prompt the model received.
        Assert.False(values.ContainsKey("氏名"));
        var userMessage = llm.LastRequest!.Messages.Single(m => m.Role == "user");
        Assert.DoesNotContain("氏名", userMessage.Content);

        // The user message carried exactly one base64 data URL for the image bytes.
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
}
