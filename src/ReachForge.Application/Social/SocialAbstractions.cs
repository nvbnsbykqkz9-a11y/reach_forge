namespace ReachForge.Application.Social;

/// <summary>メディアの短時間・読取専用 URL（SAS）。Instagram などは公開 URL から画像を取得する。</summary>
public interface IMediaUrlSigner
{
    Task<string> CreateReadUrlAsync(Guid mediaAssetId, TimeSpan lifetime, CancellationToken ct);
}
