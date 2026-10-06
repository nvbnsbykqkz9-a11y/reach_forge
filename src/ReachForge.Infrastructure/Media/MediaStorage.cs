using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;
using ReachForge.Application.Abstractions;

namespace ReachForge.Infrastructure.Media;

/// <summary>ローカルディスクへの保存（Local / Dev 環境）。パスは保存ルート外に出られないよう検証する。</summary>
public sealed class LocalMediaStorage(IOptions<MediaOptions> options) : IMediaStorage
{
    private string Root => Path.GetFullPath(options.Value.LocalPath);

    public async Task SaveAsync(string path, byte[] content, string contentType, CancellationToken ct)
    {
        var full = Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, content, ct);
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct) =>
        Task.FromResult<Stream>(new FileStream(Resolve(path), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true));

    public Task DeleteAsync(string path, CancellationToken ct)
    {
        var full = Resolve(path);
        if (File.Exists(full)) File.Delete(full);
        return Task.CompletedTask;
    }

    private string Resolve(string path)
    {
        var full = Path.GetFullPath(Path.Combine(Root, path));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Invalid media path.");
        }
        return full;
    }
}

/// <summary>Azure Blob Storage への保存（本番）。Managed Identity で接続し、配信は SAS（短時間・読取専用）。</summary>
public sealed class BlobMediaStorage(BlobContainerClient container) : IMediaStorage
{
    public async Task SaveAsync(string path, byte[] content, string contentType, CancellationToken ct) =>
        await container.GetBlobClient(path).UploadAsync(new BinaryData(content),
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } }, ct);

    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct) =>
        await container.GetBlobClient(path).OpenReadAsync(cancellationToken: ct);

    public async Task DeleteAsync(string path, CancellationToken ct) =>
        await container.GetBlobClient(path).DeleteIfExistsAsync(cancellationToken: ct);
}
