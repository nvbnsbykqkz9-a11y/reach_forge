#pragma warning disable MEAI001 // 画像生成の抽象（評価版）を使う
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ReachForge.AI.Prompts;
using ReachForge.AI.Providers;
using ReachForge.AI.Routing;
using ReachForge.AI.Services;
using ReachForge.Application.Ai;
using ReachForge.Application.Services;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;

namespace ReachForge.AI.Tests;

/// <summary>LP の広告づくりの AI：Google の画像生成（Nano Banana Pro）、世界観とビジュアル案、出来の確認、モデルの選び方。</summary>
public class LpCreativeAiTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<(string Url, string? Body, string? Key)> Requests = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!.ToString(), request.Content is null ? null : await request.Content.ReadAsStringAsync(ct),
                request.Headers.TryGetValues("x-goog-api-key", out var v) ? v.First() : null));
            return respond(request);
        }
    }

    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G'];

    [Fact]
    public async Task Google_image_generator_sends_the_reference_and_the_nearest_aspect_ratio()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$$"""{"candidates":[{"content":{"parts":[{"text":"ok"},{"inlineData":{"mimeType":"image/png","data":"{{{Convert.ToBase64String(Png)}}}"}}]}}]}""",
                Encoding.UTF8, "application/json"),
        });
        var generator = new GoogleImageGenerator(new HttpClient(handler), new AiProviderOptions { Type = AiProviderType.Google, ApiKey = "g" }, "");
        var request = new ImageGenerationRequest("product photo") { OriginalImages = [new DataContent(new byte[] { 1, 2 }, "image/png")] };
        var response = await generator.GenerateAsync(request, new ImageGenerationOptions { ImageSize = new System.Drawing.Size(1024, 1536) });

        var image = Assert.Single(response.Contents.OfType<DataContent>());
        Assert.Equal(Png, image.Data.ToArray());
        Assert.Contains($"/models/{GoogleImageGenerator.DefaultModel}:generateContent", handler.Requests[0].Url);
        Assert.Equal("g", handler.Requests[0].Key);
        var body = JsonNode.Parse(handler.Requests[0].Body!)!;
        Assert.Equal("2:3", body["generationConfig"]!["imageConfig"]!["aspectRatio"]!.GetValue<string>());
        Assert.Equal("2K", body["generationConfig"]!["imageConfig"]!["imageSize"]!.GetValue<string>());
        Assert.Equal("AQI=", body["contents"]![0]!["parts"]![1]!["inline_data"]!["data"]!.GetValue<string>());
    }

    [Fact]
    public async Task Google_image_generator_reports_a_blocked_prompt()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"promptFeedback":{"blockReason":"SAFETY"}}""", Encoding.UTF8, "application/json"),
        });
        var generator = new GoogleImageGenerator(new HttpClient(handler), new AiProviderOptions { Type = AiProviderType.Google, ApiKey = "g" }, "veo-3.1-fast-generate-preview");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => generator.GenerateAsync(new ImageGenerationRequest("x")));
        Assert.Contains("SAFETY", ex.Message);
        Assert.Contains(GoogleImageGenerator.DefaultModel, handler.Requests[0].Url); // 動画のモデルは画像に使わない
    }

    [Theory]
    [InlineData(1080, 1350, "4:5")]
    [InlineData(1080, 1920, "9:16")]
    [InlineData(1200, 628, "16:9")]
    [InlineData(1536, 1024, "3:2")]
    [InlineData(1080, 1080, "1:1")]
    public void Nearest_supported_aspect_ratio(int w, int h, string expected) =>
        Assert.Equal(expected, GoogleImageGenerator.NearestRatio((double)w / h));

    [Fact]
    public void Image_edit_uses_the_image_model_when_not_set()
    {
        var google = new AiProviderOptions { Type = AiProviderType.Google, TaskModels = { ["Image"] = "gemini-3-pro-image", ["VideoGeneration"] = "veo" } };
        Assert.Equal("gemini-3-pro-image", google.ModelFor(AiTaskType.ImageEdit));
        Assert.True(google.SupportsImageGeneration);
        Assert.True(new AiProviderOptions { Type = AiProviderType.Kling }.SupportsVideoGeneration);
        Assert.False(new AiProviderOptions { Type = AiProviderType.Kling }.SupportsImageGeneration);
    }

    private static readonly BrandContext Brand = new(new BrandProfile { BrandName = "SecureView", BrandColors = ["#0B1B3A"] }, []);

    private static WebPage Page() => new(new Uri("https://example.com/lp"), "SecureView | 検知から初動まで1画面で", "",
        "アラートが多すぎる。AI が検知・分析し、対応が必要なものだけを知らせます。", ["#1F4FD8", "#ffffff"]);

    private static IModelRouter Router(IChatClient client)
    {
        var router = Substitute.For<IModelRouter>();
        router.Resolve(Arg.Any<AiTaskType>()).Returns(client);
        return router;
    }

    [Fact]
    public async Task Planner_returns_a_direction_from_the_page_and_keeps_the_screen_kind()
    {
        var planner = new LpVisualPlanner(Router(new StubChatClient()), PromptTests.Catalog());
        var plan = await planner.PlanAsync(Brand, Page(),
            [new LpSourceBrief("ダッシュボードの画面", LpSourceKind.Screen), new LpSourceBrief("運用チームの写真", LpSourceKind.Photo)], CancellationToken.None);

        Assert.Equal(2, plan.Concepts.Count);
        Assert.Equal(LpSourceKind.Screen, plan.Concepts[0].Kind);
        Assert.Equal(LpSourceKind.Photo, plan.Concepts[1].Kind);
        Assert.All(plan.Concepts, c => Assert.False(string.IsNullOrWhiteSpace(c.BackdropPrompt)));
        Assert.Equal("#1F4FD8", plan.Direction.Palette[0]); // LP の色
        Assert.False(string.IsNullOrWhiteSpace(plan.Direction.Setting));
    }

    [Fact]
    public void Direction_keeps_only_valid_colors_and_known_motifs()
    {
        var direction = LpVisualPlanner.Direction(new LpDirectionDraft("緊張感", ["#ff0000", "red", "#12345", "#0B1B3A"], "SMOKE", ""), Brand, Page());
        Assert.Equal(["#FF0000", "#0B1B3A", "#1F4FD8", "#FFFFFF"], direction.Palette);
        Assert.Equal(BackdropMotif.Smoke, direction.Motif);
        Assert.False(string.IsNullOrWhiteSpace(direction.Setting));

        Assert.Equal(BackdropMotif.Aurora, LpVisualPlanner.Direction(new LpDirectionDraft(null, null, "neon", null), Brand, Page()).Motif);
    }

    private sealed class FixedClient(string json) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    [Theory]
    [InlineData("""{"score":5,"approved":true,"problems":"","fix":""}""", true, 5)]
    [InlineData("""{"score":4,"approved":false,"problems":"商品のラベルが変わっている","fix":"Keep the label exactly as in the reference."}""", false, 4)]
    [InlineData("""{"score":3,"approved":true,"problems":"","fix":""}""", false, 3)] // 点数が低ければ合格にしない
    [InlineData("not json", true, 0)]                                                   // 確かめられなければ止めない
    public async Task Reviewer_turns_the_answer_into_a_verdict(string answer, bool approved, int score)
    {
        var reviewer = new LpVisualReviewer(Router(new FixedClient(answer)), PromptTests.Catalog(), NullLogger<LpVisualReviewer>.Instance);
        var review = await reviewer.ReviewAsync(Png, "image/png", Png, "image/png", new LpVisualConcept(0, "こだわり", "", "", ""), CancellationToken.None);

        Assert.Equal((approved, score), (review.Approved, review.Score));
        if (score == 4) Assert.Equal("Keep the label exactly as in the reference.", review.Feedback);
    }

    [Fact]
    public async Task Video_prompt_writer_keeps_known_scenes_and_only_quotes_found_in_the_lp()
    {
        const string answer = """
            {"scenes":[
              {"slot":"solution","title":"解決","sourceText":"AI が検知・分析し、対応が必要なものだけを知らせます。","prompt":"Calm blue light fills the room."},
              {"slot":"hook","title":"冒頭","sourceText":"「アラートが多すぎる。」","prompt":"Red warning lights flash on blurred monitors."},
              {"slot":"hook","title":"重複","sourceText":"","prompt":"duplicate"},
              {"slot":"backdrop","title":"背景","sourceText":"LP にない言葉","prompt":"An empty dark office."},
              {"slot":"ending","title":"不明","sourceText":"","prompt":"unknown slot"},
              {"slot":"image","title":"画像","sourceText":"","prompt":"  "}
            ]}
            """;
        var writer = new LpVideoPromptWriter(Router(new FixedClient(answer)), PromptTests.Catalog());
        var prompts = await writer.WriteAsync(Brand, Page(), CancellationToken.None);

        Assert.Equal(["hook", "solution", "backdrop"], prompts.Select(p => p.Slot)); // 決まった順・重複と不明な場面・空の指示は除く
        Assert.Equal("アラートが多すぎる。", prompts[0].SourceText);                  // 括弧を外し、LP にある文言だけを残す
        Assert.Equal("", prompts[2].SourceText);                                      // LP にない言葉は「LP の文言」として見せない
        Assert.Equal("Red warning lights flash on blurred monitors.", prompts[0].Prompt);
    }

    [Fact]
    public async Task Video_prompt_writer_stub_returns_all_scenes()
    {
        var writer = new LpVideoPromptWriter(Router(new StubChatClient()), PromptTests.Catalog());
        var prompts = await writer.WriteAsync(Brand, Page(), CancellationToken.None);
        Assert.Equal(LpVideoPromptWriter.Slots, prompts.Select(p => p.Slot));
    }
}
