using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ReachForge.Web.Tests;

public class MediaEndpointTests
{
    private static MultipartFormDataContent PngForm()
    {
        using var image = new Image<Rgba32>(320, 240, new Rgba32(30, 140, 200));
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        var file = new ByteArrayContent(ms.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return new MultipartFormDataContent { { file, "file", "sample.png" } };
    }

    [Fact]
    public async Task Upload_requires_custom_header_and_delivers_through_signed_url()
    {
        await using var app = new WebFixture();
        var client = app.Browser();
        await WebFixture.LoginAsync(client, "owner@example.com");

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/v1/media", PngForm())).StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/media") { Content = PngForm() };
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        var created = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/media");
        var url = list.EnumerateArray().Single(a => a.GetProperty("id").GetGuid() == id).GetProperty("url").GetString()!;

        // 配信 URL は認証なし（SNS からの取得）で読める
        var anonymous = app.Browser();
        var image = await anonymous.GetAsync(url);
        Assert.True(image.StatusCode == HttpStatusCode.OK, $"{url} -> {(int)image.StatusCode} {await image.Content.ReadAsStringAsync()}");
        Assert.StartsWith("image/", image.Content.Headers.ContentType?.MediaType);

        // 改ざんしたトークンは拒否する
        var tampered = url[..^2] + "xx";
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(tampered)).StatusCode);
    }
}
