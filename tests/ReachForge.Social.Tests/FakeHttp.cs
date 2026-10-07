using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using ReachForge.Social;

namespace ReachForge.Social.Tests;

public sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Body, Dictionary<string, string> Headers);

/// <summary>要求を記録し、登録した順に応答を返すテスト用 HTTP ハンドラ。</summary>
public sealed class FakeHttp : HttpMessageHandler, IHttpClientFactory
{
    private readonly Queue<(string Contains, HttpStatusCode Status, string Body, Uri? Location)> _responses = new();
    public List<RecordedRequest> Requests { get; } = [];

    public FakeHttp Respond(string urlContains, string json, HttpStatusCode status = HttpStatusCode.OK, Uri? location = null)
    {
        _responses.Enqueue((urlContains, status, json, location));
        return this;
    }

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false)
    {
        BaseAddress = new Uri(name switch
        {
            "sns-x" => "https://api.x.com/",
            "sns-meta" => "https://graph.facebook.com/",
            "sns-threads" => "https://graph.threads.net/",
            "sns-line" => "https://api.line.me/",
            "sns-tiktok" => "https://open.tiktokapis.com/",
            "sns-youtube" => "https://www.googleapis.com/",
            _ => "https://example.invalid/",
        }),
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
        if (request.Content is not null)
        {
            foreach (var h in request.Content.Headers) headers[h.Key] = string.Join(",", h.Value);
        }
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, body, headers));

        if (_responses.Count == 0) throw new InvalidOperationException($"Unexpected request {request.Method} {request.RequestUri}");
        var (contains, status, json, location) = _responses.Dequeue();
        Assert.Contains(contains, request.RequestUri!.ToString());
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        response.Headers.Location = location;
        return response;
    }

    public static IOptions<SocialOptions> Options(Action<SocialOptions>? configure = null)
    {
        var o = new SocialOptions
        {
            X = { ClientId = "x-client", ClientSecret = "x-secret" },
            Meta = { AppId = "meta-app", AppSecret = "meta-secret", GraphVersion = "v24.0" },
            Threads = { AppId = "th-app", AppSecret = "th-secret" },
            TikTok = { ClientKey = "tt-key", ClientSecret = "tt-secret", Audited = true },
            YouTube = { ClientId = "yt-client", ClientSecret = "yt-secret" },
        };
        configure?.Invoke(o);
        return Microsoft.Extensions.Options.Options.Create(o);
    }
}
