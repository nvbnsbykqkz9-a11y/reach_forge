using ReachForge.Domain.Common;
using ReachForge.Domain.Entities;
using ReachForge.Domain.Enums;
using ReachForge.Domain.Guardrails;

namespace ReachForge.Domain.Tests;

public class PostVariantTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 3, 0, 0, TimeSpan.Zero);

    private static PostVariant NewVariant()
    {
        var master = new MasterPost { Title = "秋限定ラテ", CoreMessage = "本文" };
        var channel = new Channel
        {
            Platform = SocialPlatform.X, ExternalAccountId = "x", DisplayName = "@x", CredentialSecretRef = "dev://",
        };
        return PostVariant.Create(master, channel, "今年の秋は、ほっくり甘い一杯から。", ["秋限定"]);
    }

    private static GuardrailReport ErrorReport() =>
        new([new GuardrailFinding(GuardrailLevel.Error, "E", "エラー")]);

    [Fact]
    public void Happy_path_with_approval_reaches_published()
    {
        var v = NewVariant();
        v.Submit(Now.AddHours(2));
        Assert.Equal(VariantStatus.InReview, v.Status);
        Assert.Equal(Now.AddHours(2), v.RequestedPublishAt);

        v.Approve();
        v.Schedule(Now.AddHours(2), Now, requiresApproval: true);
        Assert.Equal(VariantStatus.Scheduled, v.Status);
        Assert.False(v.IsDue(Now));
        Assert.True(v.IsDue(Now.AddHours(2)));

        v.MarkPublishing();
        v.MarkPublished("123", "https://x.com/123", Now.AddHours(2));
        Assert.Equal(VariantStatus.Published, v.Status);
        Assert.Equal("123", v.ExternalPostId);
    }

    [Fact]
    public void Cannot_schedule_unapproved_variant_when_approval_is_required()
    {
        var v = NewVariant();
        var ex = Assert.Throws<DomainException>(() => v.Schedule(Now.AddHours(1), Now, requiresApproval: true));
        Assert.Equal(ErrorCodes.PubNotApproved, ex.ErrorCode);
    }

    [Fact]
    public void Draft_can_be_scheduled_directly_when_approval_is_not_required()
    {
        var v = NewVariant();
        v.Schedule(Now.AddHours(1), Now, requiresApproval: false);
        Assert.Equal(VariantStatus.Scheduled, v.Status);
    }

    [Fact]
    public void Scheduling_in_the_past_is_rejected()
    {
        var v = NewVariant();
        var ex = Assert.Throws<DomainException>(() => v.Schedule(Now.AddMinutes(-1), Now, requiresApproval: false));
        Assert.Equal(ErrorCodes.PubScheduleInPast, ex.ErrorCode);
    }

    [Fact]
    public void Editing_after_approval_requires_reapproval()
    {
        var v = NewVariant();
        v.Submit();
        v.Approve();
        v.Schedule(Now.AddHours(1), Now, requiresApproval: true);

        var reapproval = v.Edit("書き換えた本文", ["秋限定"], null, requiresApproval: true);

        Assert.True(reapproval);
        Assert.Equal(VariantStatus.Draft, v.Status);
        Assert.Null(v.ScheduledAt);
    }

    [Fact]
    public void Editing_without_changes_keeps_approval()
    {
        var v = NewVariant();
        v.Submit();
        v.Approve();

        var reapproval = v.Edit(v.Body, v.Hashtags.ToList(), v.Title, requiresApproval: true);

        Assert.False(reapproval);
        Assert.Equal(VariantStatus.Approved, v.Status);
    }

    [Fact]
    public void Guardrail_errors_block_submission()
    {
        var v = NewVariant();
        v.ApplyGuardrail(ErrorReport());
        var ex = Assert.Throws<DomainException>(() => v.Submit());
        Assert.Equal(ErrorCodes.AprBlockedByGuardrail, ex.ErrorCode);
    }

    [Fact]
    public void Guardrail_errors_block_approval_except_owner_exception_with_reason()
    {
        var v = NewVariant();
        v.Submit();
        v.ApplyGuardrail(ErrorReport());

        Assert.Throws<DomainException>(() => v.Approve());
        Assert.Throws<DomainException>(() => v.Approve(asOwnerException: true, exceptionReason: " "));

        v.Approve(asOwnerException: true, exceptionReason: "法務確認済み");
        Assert.Equal(VariantStatus.Approved, v.Status);
    }

    [Fact]
    public void Reject_requires_reason_and_returns_to_draft()
    {
        var v = NewVariant();
        v.Submit();
        Assert.Throws<DomainException>(() => v.Reject(""));

        v.Reject("価格表記を確認してください");
        Assert.Equal(VariantStatus.Draft, v.Status);
        Assert.Equal("価格表記を確認してください", v.RejectReason);
    }

    [Fact]
    public void Transient_failures_back_off_exponentially_then_fail()
    {
        var v = NewVariant();
        v.Schedule(Now.AddMinutes(1), Now, requiresApproval: false);
        var t = Now.AddMinutes(1);
        var expectedDelays = new[] { 1, 2, 4, 8, 16 };
        foreach (var minutes in expectedDelays)
        {
            v.MarkPublishing();
            v.ScheduleRetry(t, "E-PUB-503", "busy");
            Assert.Equal(VariantStatus.Scheduled, v.Status);
            Assert.Equal(t.AddMinutes(minutes), v.NextAttemptAt);
            t = v.NextAttemptAt!.Value;
        }

        v.MarkPublishing();
        v.ScheduleRetry(t, "E-PUB-503", "busy");
        Assert.Equal(VariantStatus.Failed, v.Status);

        v.RetryNow(t);
        Assert.Equal(VariantStatus.Scheduled, v.Status);
        Assert.Equal(0, v.RetryCount);
    }

    [Fact]
    public void Published_variant_cannot_be_canceled_or_edited()
    {
        var v = NewVariant();
        v.Schedule(Now.AddMinutes(1), Now, requiresApproval: false);
        v.MarkPublishing();
        v.MarkPublished("1", null, Now);

        Assert.Throws<DomainException>(v.Cancel);
        Assert.Throws<DomainException>(() => v.Edit("x", [], null, requiresApproval: false));
    }

    [Fact]
    public void Hold_and_unschedule_restore_expected_states()
    {
        var v = NewVariant();
        v.Submit();
        v.Approve();
        v.Schedule(Now.AddHours(1), Now, requiresApproval: true);
        v.Hold("緊急停止");
        Assert.Equal(VariantStatus.OnHold, v.Status);
        Assert.False(v.IsDue(Now.AddHours(2)));

        v.Unschedule(requiresApproval: true);
        Assert.Equal(VariantStatus.Approved, v.Status);
    }
}
