using MudBlazor;
using ReachForge.Domain.Common;

namespace ReachForge.Web.Hosting;

public static class UiErrors
{
    /// <summary>
    /// 業務エラーは「原因＋解決策」の文言をそのまま表示する。想定外の例外は内容を出さず問い合わせ番号を示す（RF-UX-001 8.1）。
    /// </summary>
    public static void Show(this ISnackbar snackbar, Exception ex, ILogger? log = null)
    {
        if (ex is DomainException d)
        {
            snackbar.Add(d.Message, d.ErrorCode.StartsWith("W-") ? Severity.Warning : Severity.Error);
            return;
        }
        var traceId = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N")[..12];
        log?.LogError(ex, "Unhandled UI error {TraceId}", traceId);
        snackbar.Add($"システムエラーが発生しました。（問い合わせ番号：{traceId}）", Severity.Error);
    }
}
