using FlowOps.Domain;
using FlowOps.Domain.Sla;
using FlowOps.Domain.Tickets;
using Xunit;

namespace FlowOps.Domain.Tests.Sla;

public class SlaPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly SlaConfiguration[] Configurations =
    [
        new SlaConfiguration(1, null, Priority.Critical, 240, 80),
        new SlaConfiguration(2, null, Priority.High, 480, 80),
        new SlaConfiguration(3, null, Priority.Medium, 1440, 80),
        new SlaConfiguration(4, null, Priority.Low, 4320, 80),
        new SlaConfiguration(5, WorkType.Problem, Priority.High, 600, 80),
    ];

    [Fact] // SLA-RULE-05
    public void CalculateDueDate_AddsTargetMinutes() =>
        Assert.Equal(Now.AddMinutes(240), SlaPolicy.CalculateDueDate(Now, 240));

    [Fact] // SLA-RULE-01 — exact (WorkType, Priority) match wins over the default row
    public void ResolveTargetMinutes_ExactMatch_TakesPrecedenceOverDefault() =>
        Assert.Equal(600, SlaPolicy.ResolveTargetMinutes(Configurations, WorkType.Problem, Priority.High));

    [Fact] // SLA-RULE-01 — falls back to the (null, Priority) default row
    public void ResolveTargetMinutes_NoExactMatch_FallsBackToDefault() =>
        Assert.Equal(480, SlaPolicy.ResolveTargetMinutes(Configurations, WorkType.Incident, Priority.High));

    [Fact] // SLA-RULE-01 — neither present is a domain rule violation, not a silent default
    public void ResolveTargetMinutes_NoMatchAtAll_Throws()
    {
        var sparse = new[] { new SlaConfiguration(1, WorkType.Incident, Priority.Critical, 240, 80) };
        var ex = Assert.Throws<DomainRuleException>(() => SlaPolicy.ResolveTargetMinutes(sparse, WorkType.Task, Priority.Low));
        Assert.Equal("SLA-RULE-01", ex.RuleCode);
    }

    // ---- SLA-RULE-01 / SLA-RULE-12: one resolution mechanism, returning the whole row ----

    [Fact]
    public void ResolveConfiguration_ExactMatch_TakesPrecedenceOverDefault()
    {
        var resolved = SlaPolicy.ResolveConfiguration(Configurations, WorkType.Problem, Priority.High);

        Assert.Equal(5, resolved.Id);
        Assert.Equal(600, resolved.TargetMinutes);
    }

    [Fact]
    public void ResolveConfiguration_NoExactMatch_FallsBackToDefault()
    {
        var resolved = SlaPolicy.ResolveConfiguration(Configurations, WorkType.Incident, Priority.High);

        Assert.Equal(2, resolved.Id);
        Assert.Null(resolved.WorkType);
    }

    [Fact]
    public void ResolveConfiguration_NoMatchAtAll_Throws()
    {
        var sparse = new[] { new SlaConfiguration(1, WorkType.Incident, Priority.Critical, 240, 80) };

        var ex = Assert.Throws<DomainRuleException>(() => SlaPolicy.ResolveConfiguration(sparse, WorkType.Task, Priority.Low));
        Assert.Equal("SLA-RULE-01", ex.RuleCode);
    }

    /// <summary>
    /// The reason ResolveConfiguration exists: the risk threshold must come from the row the
    /// resolution order actually selected, not from whichever row happens to be first. Uses
    /// distinct thresholds so a wrong match would produce a different number.
    /// </summary>
    [Fact]
    public void ResolveConfiguration_ReturnsTheMatchedRowsRiskThreshold()
    {
        SlaConfiguration[] mixedThresholds =
        [
            new SlaConfiguration(1, null, Priority.High, 480, 50),
            new SlaConfiguration(2, WorkType.Problem, Priority.High, 600, 90),
        ];

        Assert.Equal(90, SlaPolicy.ResolveConfiguration(mixedThresholds, WorkType.Problem, Priority.High).RiskThresholdPercent);
        Assert.Equal(50, SlaPolicy.ResolveConfiguration(mixedThresholds, WorkType.Incident, Priority.High).RiskThresholdPercent);
    }

    [Theory] // ResolveTargetMinutes must be the same resolution, not a parallel one.
    [InlineData(WorkType.Problem, Priority.High)]   // exact match
    [InlineData(WorkType.Incident, Priority.High)]  // default-row fallback
    [InlineData(WorkType.Task, Priority.Low)]       // default-row fallback, different priority
    public void ResolveTargetMinutes_DelegatesToResolveConfiguration(WorkType workType, Priority priority) =>
        Assert.Equal(
            SlaPolicy.ResolveConfiguration(Configurations, workType, priority).TargetMinutes,
            SlaPolicy.ResolveTargetMinutes(Configurations, workType, priority));

    // ---- SLA-RULE-10: GetStatus, every branch and every boundary ----

    [Fact]
    public void GetStatus_Resolved_WithSlaMetTrue_IsMet() =>
        Assert.Equal(SlaStatus.Met, SlaPolicy.GetStatus(Status.Resolved, true, Now, Now.AddDays(-1), Now.AddHours(1), 0, 1440, 80));

    [Fact]
    public void GetStatus_Resolved_WithSlaMetFalse_IsBreached() =>
        Assert.Equal(SlaStatus.Breached, SlaPolicy.GetStatus(Status.Resolved, false, Now, Now.AddDays(-1), Now.AddHours(1), 0, 1440, 80));

    [Fact]
    public void GetStatus_Closed_WithSlaMetFalse_IsBreached() =>
        Assert.Equal(SlaStatus.Breached, SlaPolicy.GetStatus(Status.Closed, false, Now, Now.AddDays(-1), Now.AddHours(1), 0, 1440, 80));

    [Fact]
    public void GetStatus_Pending_IsPaused() =>
        Assert.Equal(SlaStatus.Paused, SlaPolicy.GetStatus(Status.Pending, null, Now, Now.AddHours(-1), Now.AddHours(1), 0, 1440, 80));

    [Fact] // inclusive boundary: now == SlaDueAt is Breached
    public void GetStatus_ExactlyAtDueDate_IsBreached() =>
        Assert.Equal(SlaStatus.Breached, SlaPolicy.GetStatus(Status.InProgress, null, Now, Now.AddHours(-1), Now, 0, 60, 80));

    [Fact]
    public void GetStatus_OneMinuteAfterDueDate_IsBreached() =>
        Assert.Equal(SlaStatus.Breached, SlaPolicy.GetStatus(Status.InProgress, null, Now.AddMinutes(1), Now.AddHours(-1), Now, 0, 60, 80));

    [Fact] // 80% of 100 minutes = 80 minutes elapsed exactly at threshold
    public void GetStatus_ExactlyAtRiskThreshold_IsAtRisk()
    {
        var startedAt = Now.AddMinutes(-80);
        var dueAt = Now.AddMinutes(20);
        Assert.Equal(SlaStatus.AtRisk, SlaPolicy.GetStatus(Status.InProgress, null, Now, startedAt, dueAt, 0, 100, 80));
    }

    [Fact] // one minute before the threshold: still Within
    public void GetStatus_JustBelowRiskThreshold_IsWithin()
    {
        var startedAt = Now.AddMinutes(-79);
        var dueAt = Now.AddMinutes(21);
        Assert.Equal(SlaStatus.Within, SlaPolicy.GetStatus(Status.InProgress, null, Now, startedAt, dueAt, 0, 100, 80));
    }

    [Fact] // pause time does not count as elapsed toward the risk threshold
    public void GetStatus_PausedMinutesExcludedFromElapsedCalculation()
    {
        // 100 min target, 30 min already paused (excluded), 50 min elapsed wall-clock since start.
        // Effective elapsed = 50 - 30 = 20 => 20% => comfortably Within even though wall-clock says 50%.
        var startedAt = Now.AddMinutes(-50);
        var dueAt = startedAt.AddMinutes(100 + 30);
        Assert.Equal(SlaStatus.Within, SlaPolicy.GetStatus(Status.InProgress, null, Now, startedAt, dueAt, 30, 100, 80));
    }
}
