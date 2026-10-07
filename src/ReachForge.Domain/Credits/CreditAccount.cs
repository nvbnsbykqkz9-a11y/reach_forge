using ReachForge.Domain.Common;
using ReachForge.Domain.Enums;

namespace ReachForge.Domain.Credits;

/// <summary>
/// テナントのクレジット残高（F-13）。生成前に推定消費を予約（ホールド）し、完了時に実消費で確定、
/// 失敗・ブロック時は解放する。残量 20% で通知、0% で生成停止（予約投稿の配信は継続）。
/// </summary>
public sealed class CreditAccount : Entity
{
    public const double LowThreshold = 0.20;

    public int MonthlyGrant { get; set; }
    public int Balance { get; private set; }
    public int Held { get; private set; }

    /// <summary>今月の消費量（機能別内訳は AiGeneration から集計）。</summary>
    public int ConsumedThisPeriod { get; private set; }
    public DateOnly PeriodStart { get; set; }

    public int Available => Math.Max(0, Balance - Held);

    public double RemainingRatio => MonthlyGrant == 0 ? 0 : (double)Balance / MonthlyGrant;

    public bool IsLow => RemainingRatio <= LowThreshold;

    public bool IsExhausted => Balance <= 0;

    public static CreditAccount Open(Guid tenantId, int monthlyGrant, DateOnly periodStart)
    {
        var account = new CreditAccount { TenantId = tenantId, MonthlyGrant = monthlyGrant, PeriodStart = periodStart };
        account.Balance = monthlyGrant;
        return account;
    }

    public void Hold(int amount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        if (amount > Available)
        {
            throw new DomainException(ErrorCodes.AiInsufficientCredits,
                $"クレジットが不足しています（必要：{amount}／残り：{Available}）。");
        }
        Held += amount;
    }

    /// <summary>ホールドを確定する。実消費が見積りを超えた場合も残高の範囲で確定する（マイナスにはしない）。</summary>
    public int Commit(int held, int actual)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(actual);
        Held = Math.Max(0, Held - held);
        var charged = Math.Min(actual, Balance);
        Balance -= charged;
        ConsumedThisPeriod += charged;
        return charged;
    }

    public void Release(int held) => Held = Math.Max(0, Held - held);

    /// <summary>
    /// 月次付与・リセット（繰越なしが既定）。実行中のジョブのホールドは、そのジョブが確定・解放するため残す。
    /// </summary>
    public void ResetPeriod(DateOnly periodStart, bool carryOver = false)
    {
        Balance = carryOver ? Balance + MonthlyGrant : MonthlyGrant;
        ConsumedThisPeriod = 0;
        PeriodStart = periodStart;
    }

    /// <summary>このペースだと上限に達する日（RF-UX-001 SCR-14）。消費がなければ null。</summary>
    public DateOnly? ProjectedExhaustionDate(DateOnly today)
    {
        var elapsed = today.DayNumber - PeriodStart.DayNumber + 1;
        if (ConsumedThisPeriod <= 0 || elapsed <= 0) return null;
        var perDay = (double)ConsumedThisPeriod / elapsed;
        return today.AddDays((int)Math.Ceiling(Balance / perDay));
    }
}

/// <summary>クレジット換算表（F-13 初期案）。運用管理画面から変更可能にする前提の既定値。</summary>
public static class CreditTable
{
    public static int Cost(CreditOperation op, int quantity = 1) => op switch
    {
        CreditOperation.CopyGeneration => 3,
        CreditOperation.CopyPartialRegeneration => 1,
        CreditOperation.VariantConversion => 1 * quantity,
        CreditOperation.ImageStandard => 5 * quantity,
        CreditOperation.ImageEditAi => 5 * quantity,
        CreditOperation.ShortVideo => 300 * quantity,
        CreditOperation.Narration30s => 2 * quantity,
        CreditOperation.ReplySuggestion => 1,
        CreditOperation.Classification => 0,
        CreditOperation.TemplateVideo => 20 * quantity,
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };
}
