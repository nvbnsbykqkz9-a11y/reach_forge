using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ReachForge.Application.Abstractions;

namespace ReachForge.AI.Providers;

public interface IImageGeneratorFactory
{
    IImageGenerator Get(string providerName, AiProviderOptions options, string modelId);
}

public sealed class ImageGeneratorFactory(IServiceProvider services) : IImageGeneratorFactory
{
    private readonly ConcurrentDictionary<(string, string), IImageGenerator> _generators = new();

    public IImageGenerator Get(string providerName, AiProviderOptions options, string modelId) =>
        _generators.GetOrAdd((providerName, modelId), _ => options.Type switch
        {
            AiProviderType.Stub => new StubImageGenerator(services.GetRequiredService<IImageProcessor>()),
            AiProviderType.OpenAI => new OpenAI.Images.ImageClient(modelId, options.ApiKey).AsIImageGenerator(),
            _ => throw new NotSupportedException($"{options.Type} does not support image generation."),
        });
}

/// <summary>
/// ローカル用の決定的な画像スタブ（ブランド色のグラデーションと図形）。API キーなしで画像の流れを確認するためのもので、本番のルートには含めない。
/// 編集（アウトペインティング）では元画像をそのまま返す（透明部分は後段の JPEG 化で背景色になる）。
/// </summary>
public sealed class StubImageGenerator(IImageProcessor processor) : IImageGenerator
{
    public const string ColorsKey = "rf.colors";

    public async Task<ImageGenerationResponse> GenerateAsync(ImageGenerationRequest request, ImageGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (request.OriginalImages?.OfType<DataContent>().FirstOrDefault() is { } original)
        {
            return new ImageGenerationResponse([new DataContent(original.Data, original.MediaType)]);
        }

        var size = options?.ImageSize ?? new System.Drawing.Size(1024, 1024);
        var colors = options?.AdditionalProperties?.TryGetValue(ColorsKey, out var c) == true && c is IReadOnlyList<string> list ? list : [];
        var contents = new List<AIContent>();
        for (var i = 0; i < Math.Max(1, options?.Count ?? 1); i++)
        {
            var seed = HashCode.Combine(request.Prompt, i);
            var image = await processor.RenderPlaceholderAsync(size.Width, size.Height, seed, colors, cancellationToken);
            contents.Add(new DataContent(image.Bytes, image.Mime));
        }
        return new ImageGenerationResponse(contents);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
