using ReachForge.Application.Services;
using ReachForge.Domain.Entities;

namespace ReachForge.Application.Ai;

/// <summary>A/B テストの B 案（F-11-1：指定した1つの要素だけを変える）。</summary>
public interface IAbVariantGenerator
{
    Task<string> GenerateAsync(string body, AbVariable variable, BrandContext brand, CancellationToken ct);
}
