using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using ReachForge.Application.Security;
using ReachForge.Domain.Common;

namespace ReachForge.Web.Api;

/// <summary>
/// 例外を RFC 9457 Problem Details（type, title, status, detail, errorCode, traceId）に変換する（RF-DES-001 13.1）。
/// 想定外の例外は詳細を返さず E-SYS-500 と問い合わせ番号（traceId）だけを返す。
/// </summary>
public sealed class ProblemDetailsHandler(IProblemDetailsService problems, ILogger<ProblemDetailsHandler> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        var traceId = Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier;
        var (status, code, detail) = exception switch
        {
            ForbiddenException e => (StatusCodes.Status403Forbidden, e.ErrorCode, e.Message),
            NotFoundException e => (StatusCodes.Status404NotFound, e.ErrorCode, e.Message),
            DomainException e when e.ErrorCode == ErrorCodes.AiUnavailable => (StatusCodes.Status503ServiceUnavailable, e.ErrorCode, e.Message),
            DomainException e when e.ErrorCode == ErrorCodes.AiInsufficientCredits => (StatusCodes.Status402PaymentRequired, e.ErrorCode, e.Message),
            DomainException e => (StatusCodes.Status400BadRequest, e.ErrorCode, e.Message),
            BadHttpRequestException e => (StatusCodes.Status400BadRequest, ErrorCodes.Validation, e.Message),
            _ => (StatusCodes.Status500InternalServerError, ErrorCodes.SysUnexpected,
                $"システムエラーが発生しました。（問い合わせ番号：{traceId}）"),
        };
        if (status >= 500) log.LogError(exception, "Unhandled exception {TraceId}", traceId);

        http.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Type = $"https://reachforge.example/errors/{code}",
                Title = code,
                Status = status,
                Detail = detail,
                Extensions = { ["errorCode"] = code, ["traceId"] = traceId },
            },
        });
    }
}
