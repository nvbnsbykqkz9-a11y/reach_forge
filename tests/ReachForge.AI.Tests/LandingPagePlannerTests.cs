using Microsoft.Extensions.AI;
using NSubstitute;
using ReachForge.AI.Providers;
using ReachForge.AI.Routing;
using ReachForge.AI.Services;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;
using ReachForge.Application.Services;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;

namespace ReachForge.AI.Tests;

/// <summary>LP から作る集客動画の企画（訴求の整理・絵コンテ）と、その出力の確認。</summary>
public class LandingPagePlannerTests
{
    private static readonly BrandContext Brand = new(new BrandProfile { BrandName = "ほっこりカフェ", NgWords = ["激安"], MustPhrases = [] }, []);

    private static WebPage Page(int images = 2) => new(new Uri("https://example.com/lp"), "秋限定さつまいもラテ | ほっこりカフェ", "期間限定",
        "さつまいもラテ 680円（税込）。１，２８０円のセットもあります。", [],
        Enumerable.Range(0, images).Select(i => new WebImage(new Uri($"https://example.com/{i}.jpg"), $"画像{i}")).ToList());

    private static LandingPageVideoPlanner Planner(IChatClient? client = null)
    {
        var router = Substitute.For<IModelRouter>();
        router.Resolve(Arg.Any<AiTaskType>()).Returns(client ?? new StubChatClient());
        return new LandingPageVideoPlanner(router, PromptTests.Catalog());
    }

    private static LandingPageVideoPlan Plan(string narration, string postText = "投稿文") => new("t", "ラテ", "誰か", [], "", "来店",
        [new LandingPageScene("hook", "見出し", narration, 3, null), new LandingPageScene("cta", "締め", "来てね", 3, null)], postText, [], "");

    [Fact]
    public async Task Plan_has_a_hook_and_a_call_to_action_and_valid_image_numbers()
    {
        var plan = await Planner().PlanAsync(Brand, Page(), 5, 20, CancellationToken.None);
        Assert.Equal(5, plan.Scenes.Count);
        Assert.Equal("hook", plan.Scenes[0].Role);
        Assert.Equal("cta", plan.Scenes[^1].Role);
        Assert.All(plan.Scenes, s => Assert.True(s.ImageIndex is null or >= 0 and < 2));
        Assert.False(string.IsNullOrWhiteSpace(plan.PostText));

        var noImages = await Planner().PlanAsync(Brand, Page(images: 0), 4, 15, CancellationToken.None);
        Assert.All(noImages.Scenes, s => Assert.Null(s.ImageIndex));
    }

    [Fact]
    public void Prices_are_read_in_any_width_and_format()
    {
        Assert.Equal(["1280", "1980", "680"], LandingPageVideoPlanner.Prices("680円、１，２８０円、¥1,980").Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("いまなら680円で飲めます", true)]
    [InlineData("セットは¥1,280", true)]
    [InlineData("いまなら480円！", false)] // LP にない価格はつくらない
    [InlineData("激安のラテ", false)]      // ブランドの NG ワード
    public void Output_is_checked_against_the_page_and_brand(string narration, bool ok)
    {
        var check = () => LandingPageVideoPlanner.Check(Plan(narration), Page(), Brand);
        if (ok) check();
        else Assert.Throws<AiSafetyBlockedException>(check);
    }

    [Fact]
    public void Price_in_post_text_is_also_checked()
    {
        Assert.Throws<AiSafetyBlockedException>(() => LandingPageVideoPlanner.Check(Plan("どうぞ", "今だけ2,000円"), Page(), Brand));
    }
}
