using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReachForge.Application.Abstractions;
using ReachForge.Application.Ai;
using ReachForge.Application.Services;
using ReachForge.Domain.Common;
using ReachForge.Infrastructure.Web;

namespace ReachForge.Application.Tests;

public class BrandDiagnosisTests
{
    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("169.254.169.254", false)] // クラウドのメタデータ
    [InlineData("100.64.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("93.184.216.34", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void Only_public_addresses_are_allowed(string ip, bool expected) =>
        Assert.Equal(expected, SafeWebPageFetcher.IsPublic(IPAddress.Parse(ip)));

    [Fact]
    public void Rejects_non_http_and_non_default_ports()
    {
        Assert.Throws<DomainException>(() => SafeWebPageFetcher.EnsureAllowedUri(new Uri("file:///etc/passwd")));
        Assert.Throws<DomainException>(() => SafeWebPageFetcher.EnsureAllowedUri(new Uri("http://example.com:8080/")));
        Assert.Throws<DomainException>(() => SafeWebPageFetcher.EnsureAllowedUri(new Uri("https://user:pw@example.com/")));
        SafeWebPageFetcher.EnsureAllowedUri(new Uri("https://example.com/about"));
    }

    [Fact]
    public void Respects_robots_disallow_for_all_agents()
    {
        const string robots = "User-agent: Googlebot\nDisallow: /\n\nUser-agent: *\nDisallow: /private\nAllow: /private/menu\n";
        Assert.True(SafeWebPageFetcher.RobotsAllows(robots, "/about"));
        Assert.False(SafeWebPageFetcher.RobotsAllows(robots, "/private/staff"));
        Assert.True(SafeWebPageFetcher.RobotsAllows(robots, "/private/menu"));
        Assert.False(SafeWebPageFetcher.RobotsAllows("User-agent: *\nDisallow: /", "/"));
    }

    [Fact]
    public void Parses_text_title_and_brand_colors()
    {
        var page = SafeWebPageFetcher.Parse(new Uri("https://example.com"), """
            <html><head><title>ほっこりカフェ | 渋谷</title><meta name="description" content="渋谷の小さなカフェ">
            <meta name="theme-color" content="#b45309"><style>body{color:#333333;background:#ffffff}.btn{background:#B45309}</style>
            <script>var secret = "x";</script></head>
            <body><h1>ようこそ</h1><p>Q. 営業時間は？</p><p>A. 8時から20時です。</p></body></html>
            """);
        Assert.Equal("ほっこりカフェ | 渋谷", page.Title);
        Assert.Equal("渋谷の小さなカフェ", page.Description);
        Assert.Contains("営業時間", page.Text);
        Assert.DoesNotContain("secret", page.Text);
        Assert.Equal("#B45309", page.Colors[0]);
        Assert.DoesNotContain("#FFFFFF", page.Colors); // 白・黒・灰色は除く
    }

    private sealed class FakeFetcher(WebPage? page) : IWebPageFetcher
    {
        public Task<WebPage> FetchAsync(string url, CancellationToken ct) => page is null
            ? throw new DomainException(ErrorCodes.BrdUrlUnavailable, "取得できません")
            : Task.FromResult(page);
    }

    [Fact]
    public async Task Diagnosis_drafts_profile_and_falls_back_to_text_when_url_fails()
    {
        var page = new WebPage(new Uri("https://example.com"), "ほっこりカフェ | 渋谷", "", "毎日のコーヒーを！\nQ. 駐車場はありますか？\nA. ありません。近くのコインパーキングへ。", ["#B45309"]);
        await using (var f = await AppFixture.CreateAsync(configure: s => s.AddScoped<IWebPageFetcher>(_ => new FakeFetcher(page))))
        {
            await using var scope = f.Scope();
            var before = (await f.Get<ICreditService>(scope).GetAccountAsync(CancellationToken.None)).Balance;
            var result = await f.Get<BrandDiagnosisService>(scope).DiagnoseAsync("https://example.com", null, CancellationToken.None);
            Assert.Equal("ほっこりカフェ", result.Draft.BrandName);
            Assert.Equal("カフェ", result.Draft.Industry);
            Assert.Equal(["#B45309"], result.Draft.Colors);
            Assert.Contains(result.Draft.Faqs, x => x.Question.Contains("駐車場"));
            Assert.Null(result.Warning);
            Assert.Equal(before - 3, (await f.Get<ICreditService>(scope).GetAccountAsync(CancellationToken.None)).Balance);
            Assert.True(await f.Get<IAppDbContext>(scope).AuditLogs.AnyAsync(a => a.Action == "brand.diagnosed"));
        }

        await using (var f = await AppFixture.CreateAsync(configure: s => s.AddScoped<IWebPageFetcher>(_ => new FakeFetcher(null))))
        {
            await using var scope = f.Scope();
            var diagnosis = f.Get<BrandDiagnosisService>(scope);
            await Assert.ThrowsAsync<DomainException>(() => diagnosis.DiagnoseAsync("https://blocked.example", null, CancellationToken.None));
            var result = await diagnosis.DiagnoseAsync("https://blocked.example", "渋谷のカフェです。", CancellationToken.None);
            Assert.NotNull(result.Warning); // URL は取れなかったが紹介文で診断した
        }
    }
}
