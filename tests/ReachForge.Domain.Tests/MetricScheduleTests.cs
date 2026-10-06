using ReachForge.Domain.Analytics;

namespace ReachForge.Domain.Tests;

public class MetricScheduleTests
{
    private static readonly DateTimeOffset Published = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Not_due_before_first_checkpoint()
    {
        Assert.False(MetricSchedule.IsDue(Published, null, Published.AddMinutes(59)));
        Assert.True(MetricSchedule.IsDue(Published, null, Published.AddHours(1)));
    }

    [Fact]
    public void Due_once_per_checkpoint_even_if_late()
    {
        // 1時間後に取得済み → 6時間に達するまでは不要
        Assert.False(MetricSchedule.IsDue(Published, Published.AddHours(1), Published.AddHours(5)));
        Assert.True(MetricSchedule.IsDue(Published, Published.AddHours(1), Published.AddHours(6)));
        // 取得が遅れて 24h と 72h を両方過ぎていても1回でよい
        Assert.True(MetricSchedule.IsDue(Published, Published.AddHours(6), Published.AddHours(80)));
        Assert.False(MetricSchedule.IsDue(Published, Published.AddHours(80), Published.AddHours(90)));
    }

    [Fact]
    public void Stops_after_thirty_days()
    {
        Assert.True(MetricSchedule.IsDue(Published, Published.AddDays(7), Published.AddDays(30)));
        Assert.False(MetricSchedule.IsDue(Published, Published.AddDays(7), Published.AddDays(32)));
    }
}
