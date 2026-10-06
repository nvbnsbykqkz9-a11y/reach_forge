namespace ReachForge.Domain.Common;

/// <summary>業務ルール違反。<see cref="ErrorCode"/> は RF-DES-001 15章のコード体系に従う。</summary>
public class DomainException(string errorCode, string message) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
}
