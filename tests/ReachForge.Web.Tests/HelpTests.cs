using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using ReachForge.Infrastructure.Help;

namespace ReachForge.Web.Tests;

/// <summary>操作説明書（PDF）と About の情報。</summary>
public class HelpTests
{
    [Fact]
    public async Task Manual_pdf_is_served_inline_without_login()
    {
        await using var app = new WebFixture();
        var response = await app.Browser().GetAsync("/help/manual.pdf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("inline", response.Content.Headers.ContentDisposition?.DispositionType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.True(bytes.Length > 20_000, $"{bytes.Length} bytes"); // 表紙のアイコンと10章の本文

        if (Environment.GetEnvironmentVariable("RF_MANUAL_OUT") is { Length: > 0 } path) await File.WriteAllBytesAsync(path, bytes);
    }

    [Fact]
    public void App_info_and_legal_texts_are_complete()
    {
        Assert.Equal("ver 1.00.00", AppInfo.DisplayVersion);
        Assert.StartsWith("https://", AppInfo.DeveloperUrl);
        Assert.Contains("@", AppInfo.ContactEmail);
        Assert.True(AppInfo.Icon().Length > 1000);
        Assert.True(LegalTexts.Eula.Count >= 10);
        Assert.All(LegalTexts.ThirdParty, c => Assert.StartsWith("https://", c.Url));
        Assert.Contains(LegalTexts.ThirdParty, c => c.Name.StartsWith("SkiaSharp", StringComparison.Ordinal));
        Assert.Contains(LegalTexts.ThirdParty, c => c.Name.StartsWith("QuestPDF", StringComparison.Ordinal));
        Assert.DoesNotContain(LegalTexts.ThirdParty, c => c.Name.Contains("ImageSharp", StringComparison.Ordinal));
    }
}
