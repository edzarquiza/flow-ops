using FlowOps.Domain.Attention;
using FlowOps.Domain.Tickets;
using Xunit;
using static FlowOps.Domain.Tests.Tickets.TicketTestFactory;

namespace FlowOps.Domain.Tests.Attention;

public class AttentionPolicyTests
{
    private static readonly AttentionOptions Options = new();
    private const int RiskThreshold = 80;

    [Fact] // ATTN-RULE-01 — pure function: same inputs produce the same outputs
    public void Evaluate_IsPure_SameInputsSameOutputs()
    {
        var ticket = CreateInProgressTicket();
        var first = AttentionPolicy.Evaluate(ticket, Now, Options, RiskThreshold);
        var second = AttentionPolicy.Evaluate(ticket, Now, Options, RiskThreshold);

        Assert.Equal(first, second);
    }

    [Theory] // ATTN-RULE-02 — terminal tickets never need attention (confirmed resolution, docs/domain-model.md §7)
    [InlineData(Status.Resolved)]
    [InlineData(Status.Closed)]
    public void Evaluate_TerminalTicket_ReturnsNoSignals(Status status)
    {
        var ticket = CreateInProgressTicket();
        if (status == Status.Resolved || status == Status.Closed)
        {
            ticket.Resolve(Resolution.Fixed, "Replaced the toner cartridge.", AssigneeId, Now);
            if (status == Status.Closed)
            {
                ticket.Close(new TicketActor(ManagerId, UserRole.Manager), Now);
            }
        }

        var signals = AttentionPolicy.Evaluate(ticket, Now.AddDays(60), Options, RiskThreshold);
        Assert.Empty(signals);
    }

    [Fact] // SlaBreached
    public void Evaluate_PastSlaDueAt_FiresSlaBreached()
    {
        var ticket = CreateInProgressTicket(slaTargetMinutes: 60);
        var signals = AttentionPolicy.Evaluate(ticket, ticket.SlaDueAt.AddMinutes(1), Options, RiskThreshold);
        Assert.Contains(signals, s => s.Code == AttentionSignalCode.SlaBreached && s.Severity == AttentionSeverity.Critical);
    }

    [Fact] // SlaAtRisk — exactly at the 80% threshold
    public void Evaluate_AtRiskThreshold_FiresSlaAtRisk()
    {
        var ticket = CreateInProgressTicket(slaTargetMinutes: 100);
        var signals = AttentionPolicy.Evaluate(ticket, Now.AddMinutes(80), Options, RiskThreshold);
        Assert.Contains(signals, s => s.Code == AttentionSignalCode.SlaAtRisk);
    }

    [Fact] // near-miss: one minute before the risk threshold, no SlaAtRisk
    public void Evaluate_JustBelowRiskThreshold_DoesNotFireSlaAtRisk()
    {
        var ticket = CreateInProgressTicket(slaTargetMinutes: 100);
        var signals = AttentionPolicy.Evaluate(ticket, Now.AddMinutes(79), Options, RiskThreshold);
        Assert.DoesNotContain(signals, s => s.Code == AttentionSignalCode.SlaAtRisk);
    }

    [Fact] // Overdue
    public void Evaluate_PastDueDate_FiresOverdue()
    {
        var ticket = CreateInProgressTicket(slaTargetMinutes: 100_000);
        ticket.ChangeDueDate(Now.AddDays(1), ManagerId, Now);

        var signals = AttentionPolicy.Evaluate(ticket, Now.AddDays(2), Options, RiskThreshold);
        Assert.Contains(signals, s => s.Code == AttentionSignalCode.Overdue);
    }

    [Fact] // near-miss: due date still in the future
    public void Evaluate_BeforeDueDate_DoesNotFireOverdue()
    {
        var ticket = CreateInProgressTicket(slaTargetMinutes: 100_000);
        ticket.ChangeDueDate(Now.AddDays(2), ManagerId, Now);

        var signals = AttentionPolicy.Evaluate(ticket, Now.AddDays(1), Options, RiskThreshold);
        Assert.DoesNotContain(signals, s => s.Code == AttentionSignalCode.Overdue);
    }

    [Fact] // UnassignedUrgent
    public void Evaluate_CriticalUnassignedOver15Minutes_FiresUnassignedUrgent()
    {
        var ticket = CreateOpenTicket(priority: Priority.Critical);
        var signals = AttentionPolicy.Evaluate(ticket, Now.AddMinutes(16), Options, RiskThreshold);
        Assert.Contains(signals, s => s.Code == AttentionSignalCode.UnassignedUrgent && s.Severity == AttentionSeverity.Critical);
    }

    [Fact] // near-miss: 14 minutes unassigned
    public void Evaluate_CriticalUnassignedUnder15Minutes_DoesNotFireUnassignedUrgent()
    {
        var ticket = CreateOpenTicket(priority: Priority.Critical);
        var signals = AttentionPolicy.Evaluate(ticket, Now.AddMinutes(14), Options, RiskThreshold);
        Assert.DoesNotContain(signals, s => s.Code == AttentionSignalCode.UnassignedUrgent);
    }

    [Fact] // UnassignedUrgent does not fire for Medium/Low priority
    public void Evaluate_MediumUnassigned_DoesNotFireUnassignedUrgent()
    {
        var ticket = CreateOpenTicket(priority: Priority.Medium);
        var signals = AttentionPolicy.Evaluate(ticket, Now.AddMinutes(30), Options, RiskThreshold);
        Assert.DoesNotContain(signals, s => s.Code == AttentionSignalCode.UnassignedUrgent);
    }

    [Fact] // Aging — Medium threshold is 10 days
    public void Evaluate_OpenLongerThanAgingThreshold_FiresAging()
    {
        var ticket = CreateOpenTicket(priority: Priority.Medium);
        var signals = AttentionPolicy.Evaluate(ticket, Now.AddDays(11), Options, RiskThreshold);
        Assert.Contains(signals, s => s.Code == AttentionSignalCode.Aging);
    }

    [Fact] // near-miss: 9 days, below the 10-day Medium threshold
    public void Evaluate_UnderAgingThreshold_DoesNotFireAging()
    {
        var ticket = CreateOpenTicket(priority: Priority.Medium);
        var signals = AttentionPolicy.Evaluate(ticket, Now.AddDays(9), Options, RiskThreshold);
        Assert.DoesNotContain(signals, s => s.Code == AttentionSignalCode.Aging);
    }

    [Fact] // Stalled — Pending branch, 3-day threshold
    public void Evaluate_PendingOverThreeDays_FiresStalled()
    {
        var ticket = CreateInProgressTicket(slaTargetMinutes: 1_000_000);
        ticket.PutOnHold("Waiting on requester", AssigneeId, Now);

        var signals = AttentionPolicy.Evaluate(ticket, Now.AddDays(4), Options, RiskThreshold);
        Assert.Contains(signals, s => s.Code == AttentionSignalCode.Stalled);
    }

    [Fact] // near-miss: pending for 2 days, below the 3-day threshold
    public void Evaluate_PendingUnderThreeDays_DoesNotFireStalled()
    {
        var ticket = CreateInProgressTicket(slaTargetMinutes: 1_000_000);
        ticket.PutOnHold("Waiting on requester", AssigneeId, Now);

        var signals = AttentionPolicy.Evaluate(ticket, Now.AddDays(2), Options, RiskThreshold);
        Assert.DoesNotContain(signals, s => s.Code == AttentionSignalCode.Stalled);
    }

    [Fact] // Stalled — InProgress branch, 5-day no-activity threshold
    public void Evaluate_InProgressNoActivityOverFiveDays_FiresStalled()
    {
        var ticket = CreateInProgressTicket(slaTargetMinutes: 1_000_000);
        var signals = AttentionPolicy.Evaluate(ticket, Now.AddDays(6), Options, RiskThreshold);
        Assert.Contains(signals, s => s.Code == AttentionSignalCode.Stalled);
    }

    [Fact] // near-miss: 4 days of inactivity, below the 5-day threshold
    public void Evaluate_InProgressNoActivityUnderFiveDays_DoesNotFireStalled()
    {
        var ticket = CreateInProgressTicket(slaTargetMinutes: 1_000_000);
        var signals = AttentionPolicy.Evaluate(ticket, Now.AddDays(4), Options, RiskThreshold);
        Assert.DoesNotContain(signals, s => s.Code == AttentionSignalCode.Stalled);
    }

    [Fact] // Churn
    public void Evaluate_ThreeReassignments_FiresChurn()
    {
        var ticket = CreateAssignedTicket();
        ticket.Reassign(OtherAgentId, true, ManagerId, Now);
        ticket.Reassign(AssigneeId, true, ManagerId, Now);
        ticket.Reassign(OtherAgentId, true, ManagerId, Now);

        var signals = AttentionPolicy.Evaluate(ticket, Now, Options, RiskThreshold);
        Assert.Contains(signals, s => s.Code == AttentionSignalCode.Churn);
    }

    [Fact] // near-miss: only two reassignments
    public void Evaluate_TwoReassignments_DoesNotFireChurn()
    {
        var ticket = CreateAssignedTicket();
        ticket.Reassign(OtherAgentId, true, ManagerId, Now);
        ticket.Reassign(AssigneeId, true, ManagerId, Now);

        var signals = AttentionPolicy.Evaluate(ticket, Now, Options, RiskThreshold);
        Assert.DoesNotContain(signals, s => s.Code == AttentionSignalCode.Churn);
    }

    [Fact] // Reopened
    public void Evaluate_ReopenedTicket_FiresReopened()
    {
        var ticket = CreateInProgressTicket();
        ticket.Resolve(Resolution.Fixed, "Replaced the toner cartridge.", AssigneeId, Now);
        ticket.Reopen("Recurred", RequesterId, Now.AddDays(1), DefaultSlaConfigurations());

        var signals = AttentionPolicy.Evaluate(ticket, Now.AddDays(1), Options, RiskThreshold);
        Assert.Contains(signals, s => s.Code == AttentionSignalCode.Reopened);
    }

    // ---- ATTN-RULE-03 — thresholds come from the injected options, not from constants ----

    /// <summary>
    /// Same ticket, same instant, two different option sets: the signal must follow the injected
    /// threshold. If any aging threshold were hardcoded inside the policy, one of these would fail.
    /// </summary>
    [Fact]
    public void Evaluate_AgingThreshold_FollowsInjectedOptions()
    {
        var ticket = CreateOpenTicket(slaTargetMinutes: 1_000_000);
        var fiveDaysOld = Now.AddDays(5);

        // Default Medium threshold is 10 days: five days is not yet aging.
        Assert.DoesNotContain(
            AttentionPolicy.Evaluate(ticket, fiveDaysOld, Options, RiskThreshold),
            s => s.Code == AttentionSignalCode.Aging);

        var impatient = new AttentionOptions
        {
            AgingThresholdDays = new Dictionary<Priority, int>
            {
                [Priority.Critical] = 1,
                [Priority.High] = 1,
                [Priority.Medium] = 2,
                [Priority.Low] = 2,
            },
        };

        Assert.Contains(
            AttentionPolicy.Evaluate(ticket, fiveDaysOld, impatient, RiskThreshold),
            s => s.Code == AttentionSignalCode.Aging);
    }

    [Fact] // The same, for the churn threshold.
    public void Evaluate_ChurnThreshold_FollowsInjectedOptions()
    {
        var ticket = CreateAssignedTicket(slaTargetMinutes: 1_000_000);
        ticket.Reassign(OtherAgentId, newAssigneeIsActiveTeamMember: true, ManagerId, Now);

        // One reassignment is below the default threshold of three.
        Assert.DoesNotContain(
            AttentionPolicy.Evaluate(ticket, Now, Options, RiskThreshold),
            s => s.Code == AttentionSignalCode.Churn);

        var sensitive = new AttentionOptions { ChurnAssignmentChangeThreshold = 1 };

        Assert.Contains(
            AttentionPolicy.Evaluate(ticket, Now, sensitive, RiskThreshold),
            s => s.Code == AttentionSignalCode.Churn);
    }

    // ---- ATTN-RULE-02 — signals accumulate; only terminal status suppresses ----

    /// <summary>
    /// One ticket deliberately arranged to trip several independent signals at once. No signal
    /// suppresses another: the only suppression in the policy is the terminal-status early return.
    /// </summary>
    [Fact]
    public void Evaluate_TicketTrippingSeveralConditions_ReturnsAllOfThem()
    {
        // Critical, unassigned, long overdue on both its due date and its SLA, and old enough to age.
        var ticket = Ticket.Create(
            title: "Payroll system is offline",
            description: "The payroll system has been unreachable since this morning.",
            workType: WorkType.Incident,
            priority: Priority.Critical,
            requesterId: RequesterId,
            teamId: TeamId,
            categoryId: CategoryId,
            categoryTeamId: TeamId,
            projectId: null,
            slaTargetMinutes: 240,
            now: Now);

        ticket.ChangeDueDate(Now.AddHours(1), ManagerId, Now);

        var signals = AttentionPolicy.Evaluate(ticket, Now.AddDays(3), Options, RiskThreshold);
        var codes = signals.Select(s => s.Code).ToList();

        Assert.Contains(AttentionSignalCode.SlaBreached, codes);
        Assert.Contains(AttentionSignalCode.Overdue, codes);
        Assert.Contains(AttentionSignalCode.UnassignedUrgent, codes);
        Assert.Contains(AttentionSignalCode.Aging, codes);

        // SlaBreached and SlaAtRisk are mutually exclusive by construction — one derived status.
        Assert.DoesNotContain(AttentionSignalCode.SlaAtRisk, codes);
        Assert.Equal(codes.Count, codes.Distinct().Count());
    }

    // ---- ATTN-RULE-04 ranking ----

    [Fact]
    public void Rank_OrdersBySeverityThenSlaDueAtThenPriorityThenCreatedAtThenId()
    {
        var critical = MakeResultWithSeverity(id: 2, severity: AttentionSeverity.Critical, slaDueAt: Now.AddHours(5), priority: Priority.High, createdAt: Now);
        var highSoonerDue = MakeResultWithSeverity(id: 3, severity: AttentionSeverity.High, slaDueAt: Now.AddHours(1), priority: Priority.Medium, createdAt: Now);
        var highLaterDue = MakeResultWithSeverity(id: 4, severity: AttentionSeverity.High, slaDueAt: Now.AddHours(2), priority: Priority.Medium, createdAt: Now);
        var mediumHigherPriority = MakeResultWithSeverity(id: 5, severity: AttentionSeverity.Medium, slaDueAt: Now.AddHours(10), priority: Priority.Critical, createdAt: Now);
        var mediumLowerPriority = MakeResultWithSeverity(id: 6, severity: AttentionSeverity.Medium, slaDueAt: Now.AddHours(10), priority: Priority.Low, createdAt: Now);

        var ranked = AttentionPolicy.Rank([mediumLowerPriority, highLaterDue, critical, mediumHigherPriority, highSoonerDue]);

        Assert.Equal([critical, highSoonerDue, highLaterDue, mediumHigherPriority, mediumLowerPriority], ranked);
    }

    [Fact] // final tiebreak: identical severity/SlaDueAt/priority/CreatedAt — lower Id wins
    public void Rank_FinalTiebreak_IsById()
    {
        var shared = Now.AddHours(3);
        var lowerId = MakeResultWithSeverity(id: 10, AttentionSeverity.High, shared, Priority.High, Now);
        var higherId = MakeResultWithSeverity(id: 11, AttentionSeverity.High, shared, Priority.High, Now);

        var ranked = AttentionPolicy.Rank([higherId, lowerId]);

        Assert.Equal([lowerId, higherId], ranked);
    }

    [Fact]
    public void Rank_DropsTicketsWithNoSignals()
    {
        var withSignal = MakeResultWithSeverity(id: 1, AttentionSeverity.Critical, Now.AddHours(1), Priority.High, Now);
        var withoutSignal = new TicketAttentionResult(CreateOpenTicket(), []);

        var ranked = AttentionPolicy.Rank([withSignal, withoutSignal]);

        Assert.Single(ranked);
        Assert.Same(withSignal.Ticket, ranked[0].Ticket);
    }

    private static TicketAttentionResult MakeResultWithSeverity(int id, AttentionSeverity severity, DateTimeOffset slaDueAt, Priority priority, DateTimeOffset createdAt)
    {
        // Ticket.SlaDueAt has no direct setter (rightly — it's always derived from
        // SlaStartedAt + SlaTargetMinutes). To land on a specific SlaDueAt for the ranking test,
        // back-compute the target minutes from the desired gap instead of inventing a new seam.
        var targetMinutes = (int)Math.Round((slaDueAt - createdAt).TotalMinutes);
        var ticket = CreateOpenTicket(priority: priority, slaTargetMinutes: targetMinutes, now: createdAt);
        ticket.SetId(id);
        var signal = new AttentionSignal(AttentionSignalCode.Aging, severity, "test signal", createdAt);
        return new TicketAttentionResult(ticket, [signal]);
    }
}
