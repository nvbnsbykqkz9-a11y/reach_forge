using ReachForge.Domain.Enums;
using ReachForge.Domain.Platforms;

namespace ReachForge.Application.Ai;

/// <summary>画像生成の指定（F-04 入力）。</summary>
public sealed record ImageGenerationSpec
{
    public required string Prompt { get; init; }
    public ImageStyle Style { get; init; } = ImageStyle.Photo;
    public required AspectRatio Aspect { get; init; }
    public required (int Width, int Height) Size { get; init; }
    public int Count { get; init; } = 1;
    public IReadOnlyList<string> BrandColors { get; init; } = [];
    public string BrandName { get; init; } = "";

    /// <summary>編集（アウトペインティングなど）の元画像。</summary>
    public byte[]? SourceImage { get; init; }
    public string? SourceMime { get; init; }

    public const int MaxCount = 4;
    public const int MaxPromptLength = 1000;
}

public sealed record GeneratedImage(byte[] Bytes, string Mime, AiModelInfo Model);

/// <summary>画像の生成・編集（モデルルータ経由、障害時は代替プロバイダへ）。</summary>
public interface IImageGenerationService
{
    Task<IReadOnlyList<GeneratedImage>> GenerateAsync(ImageGenerationSpec spec, Guid? generationId, CancellationToken ct);
}

/// <summary>画像の ALT テキスト（代替テキスト）を画像理解モデルで作る（F-04-7、WCAG）。</summary>
public interface IAltTextGenerator
{
    Task<string> DescribeAsync(byte[] image, string mime, string? hint, CancellationToken ct);
}
