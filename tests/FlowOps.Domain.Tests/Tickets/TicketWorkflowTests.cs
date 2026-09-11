using FlowOps.Domain;
using FlowOps.Domain.Tickets;
using Xunit;
using static FlowOps.Domain.Tests.Tickets.TicketTestFactory;

namespace FlowOps.Domain.Tests.Tickets;

public class TicketWorkflowTests
{
    // ---- TICKET-WF-01 ----

    [Fact]
    public void Assign_FromOpen_Succeeds()
    {
        var ticket = CreateOpenTicket();
        ticket.Assign(AssigneeId, assigneeIsActiveTeamMember: true, ManagerId, Now);

        Assert.Equal(Status.Assigned, ticket.Status);
        Assert.Equal(AssigneeId, ticket.AssigneeId);
        Assert.Equal(TicketEventType.Assigned, ticket.Events.Last().EventType);
    }

    [Fact] // TICKET-INV-03
    public void Assign_ToInactiveTeamMember_Throws()
    {
        var ticket = CreateOpenTicket();
        var ex = Assert.Throws<DomainRuleException>(() => ticket.Assign(AssigneeId, assigneeIsActiveTeamMember: false, ManagerId, Now));
        Assert.Equal("TICKET-INV-03", ex.RuleCode);
    }

    [Fact] // TICKET-WF-10
    public void Assign_WhenAlreadyAssigned_Throws()
    {
        var ticket = CreateAssignedTicket();
        var ex = Assert.Throws<DomainRuleException>(() => ticket.Assign(OtherAgentId, true, ManagerId, Now));
        Assert.Equal("TICKET-WF-01", ex.RuleCode);
    }

    // ---- TICKET-WF-02 ----

    [Fact]
    public void Unassign_FromAssigned_ClearsAssigneeAndReturnsToOpen()
    {
        var ticket = CreateAssignedTicket();
        ticket.Unassign(ManagerId, Now);

        Assert.Equal(Status.Open, ticket.Status);
        Assert.Null(ticket.AssigneeId);
    }

    // ---- TICKET-WF-03 ----

    [Fact]
    public void StartWork_ByAssignee_Succeeds()
    {
        var ticket = CreateAssignedTicket();
        ticket.StartWork(new TicketActor(AssigneeId, UserRole.Agent), Now);
        Assert.Equal(Status.InProgress, ticket.Status);
    }

    [Fact]
    public void StartWork_ByNonAssigneeAgent_Throws()
    {
        var ticket = CreateAssignedTicket();
        var ex = Assert.Throws<DomainRuleException>(() => ticket.StartWork(new TicketActor(OtherAgentId, UserRole.Agent), Now));
        Assert.Equal("TICKET-WF-03", ex.RuleCode);
    }

    [Theory]
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.Admin)]
    public void StartWork_ByManagerOrAdminOverride_Succeeds(UserRole role)
    {
        var ticket = CreateAssignedTicket();
        ticket.StartWork(new TicketActor(OtherAgentId, role), Now);
        Assert.Equal(Status.InProgress, ticket.Status);
    }

    // ---- TICKET-WF-04 / TICKET-INV-05 ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void PutOnHold_WithoutReason_Throws(string reason)
    {
        var ticket = CreateAssignedTicket();
        var ex = Assert.Throws<DomainRuleException>(() => ticket.PutOnHold(reason, ManagerId, Now));
        Assert.Equal("TICKET-INV-05", ex.RuleCode);
    }

    [Fact]
    public void PutOnHold_FromAssigned_Succeeds_AndDoesNotYetTouchPausedMinutes()
    {
        var ticket = CreateAssignedTicket();
        ticket.PutOnHold("Waiting on requester", ManagerId, Now);

        Assert.Equal(Status.Pending, ticket.Status);
        Assert.Equal("Waiting on requester", ticket.PendingReason);
        Assert.Equal(Now, ticket.PendingSince);
        Assert.Equal(0, ticket.SlaPausedMinutes); // SLA-RULE-07: paused minutes accrue on Resume/Resolve, not here
    }

    // ---- TICKET-WF-05 / TICKET-WF-06 / SLA-RULE-07 ----

    [Fact]
    public void Resume_FromAssignedBeforeHold_ReturnsToAssigned()
    {
        var ticket = CreateAssignedTicket();
        ticket.PutOnHold("Waiting on requester", ManagerId, Now);
        ticket.Resume(ManagerId, Now.AddMinutes(30));

        Assert.Equal(Status.Assigned, ticket.Status);
    }

    [Fact]
    public void Resume_FromInProgressBeforeHold_ReturnsToInProgress()
    {
        var ticket = CreateInProgressTicket();
        ticket.PutOnHold("Waiting on vendor", AssigneeId, Now);
        ticket.Resume(AssigneeId, Now.AddMinutes(45));

        Assert.Equal(Status.InProgress, ticket.Status);
    }

    [Fact]
    public void Resume_AccruesPausedMinutes_AndPushesOutSlaDueAt()
    {
        var ticket = CreateInProgressTicket(slaTargetMinutes: 1440);
        var originalDueAt = ticket.SlaDueAt;

        ticket.PutOnHold("Waiting on vendor", AssigneeId, Now);
        ticket.Resume(AssigneeId, Now.AddMinutes(30));

        Assert.Equal(30, ticket.SlaPausedMinutes);
        Assert.Equal(originalDueAt.AddMinutes(30), ticket.SlaDueAt);
        Assert.Null(ticket.PendingSince);
    }

    // ---- TICKET-WF-07 / TICKET-INV-06 / SLA-RULE-08 ----

    [Theory]
    [InlineData("123456789", false)]  // 9 chars — below the boundary
    [InlineData("1234567890", true)]  // exactly 10 — boundary is valid
    public void Resolve_ResolutionNotesLengthBoundary_EnforcedExactly(string notes, bool expectSuccess)
    {
        var ticket = CreateInProgressTicket();

        if (expectSuccess)
        {
            ticket.Resolve(Resolution.Fixed, notes, AssigneeId, Now);
            Assert.Equal(Status.Resolved, ticket.Status);
        }
        else
        {
            var ex = Assert.Throws<DomainRuleException>(() => ticket.Resolve(Resolution.Fixed, notes, AssigneeId, Now));
            Assert.Equal("TICKET-INV-06", ex.RuleCode);
        }
    }

    [Fact] // SLA-RULE-08 — resolved exactly at SlaDueAt is inclusive: Met
    public void Resolve_ExactlyAtSlaDueAt_IsMet()
    {
        var ticket = CreateInProgressTicket(slaTargetMinutes: 60);
        ticket.Resolve(Resolution.Fixed, "Replaced the toner cartridge.", AssigneeId, ticket.SlaDueAt);

        Assert.True(ticket.SlaMet);
        Assert.Equal(ticket.SlaDueAt, ticket.ResolvedAt);
    }

    [Fact] // SLA-RULE-08 — one tick after SlaDueAt is Breached
    public void Resolve_OneSecondAfterSlaDueAt_IsNotMet()
    {
        var ticket = CreateInProgressTicket(slaTargetMinutes: 60);
        ticket.Resolve(Resolution.Fixed, "Replaced the toner cartridge.", AssigneeId, ticket.SlaDueAt.AddSeconds(1));

        Assert.False(ticket.SlaMet);
    }

    [Fact] // TICKET-INV-06 / TICKET-INV-07
    public void Resolve_SetsResolvedAtButNotClosedAt()
    {
        var ticket = CreateInProgressTicket();
        ticket.Resolve(Resolution.Fixed, "Replaced the toner cartridge.", AssigneeId, Now);

        Assert.NotNull(ticket.ResolvedAt);
        Assert.Null(ticket.ClosedAt);
    }

    [Fact] // SLA-RULE-07 — resolving directly from Pending also accrues the pause
    public void Resolve_FromPending_AccruesPendingPauseFirst()
    {
        var ticket = CreateInProgressTicket(slaTargetMinutes: 1440);
        var originalDueAt = ticket.SlaDueAt;
        ticket.PutOnHold("Waiting on requester", AssigneeId, Now);

        ticket.Resolve(Resolution.Fixed, "Replaced the toner cartridge.", AssigneeId, Now.AddMinutes(20));

        Assert.Equal(20, ticket.SlaPausedMinutes);
        Assert.Equal(originalDueAt.AddMinutes(20), ticket.SlaDueAt);
    }

    // ---- TICKET-WF-08 ----

    [Fact]
    public void Close_ByManager_Succeeds()
    {
        var ticket = CreateInProgressTicket();
        ticket.Resolve(Resolution.Fixed, "Replaced the toner cartridge.", AssigneeId, Now);
        ticket.Close(new TicketActor(ManagerId, UserRole.Manager), Now);

        Assert.Equal(Status.Closed, ticket.Status);
        Assert.NotNull(ticket.ClosedAt);
    }

    [Fact]
    public void Close_ByRequesterConfirming_Succeeds()
    {
        var ticket = CreateInProgressTicket();
        ticket.Resolve(Resolution.Fixed, "Replaced the toner cartridge.", AssigneeId, Now);
        ticket.Close(new TicketActor(RequesterId, UserRole.Viewer), Now);

        Assert.Equal(Status.Closed, ticket.Status);
    }

    [Fact]
    public void Close_ByUnrelatedAgent_Throws()
    {
        var ticket = CreateInProgressTicket();
        ticket.Resolve(Resolution.Fixed, "Replaced the toner cartridge.", AssigneeId, Now);

        var ex = Assert.Throws<DomainRuleException>(() => ticket.Close(new TicketActor(OtherAgentId, UserRole.Agent), Now));
        Assert.Equal("TICKET-WF-08", ex.RuleCode);
    }

    // ---- TICKET-WF-09 / SLA-RULE-09 ----

    [Fact]
    public void Reopen_FromClosed_StartsNewSlaCycle()
    {
        var ticket = CreateInProgressTicket(priority: Priority.High, slaTargetMinutes: 480);
        ticket.Resolve(Resolution.Fixed, "Replaced the toner cartridge.", AssigneeId, Now);
        ticket.Close(new TicketActor(ManagerId, UserRole.Manager), Now);

        var reopenTime = Now.AddDays(2);
        ticket.Reopen("Issue recurred", RequesterId, reopenTime, DefaultSlaConfigurations());

        Assert.Equal(1, ticket.ReopenCount);
        Assert.Equal(reopenTime, ticket.SlaStartedAt);
        Assert.Equal(0, ticket.SlaPausedMinutes);
        Assert.Null(ticket.SlaMet);
        Assert.Null(ticket.ResolvedAt);
        Assert.Null(ticket.ClosedAt);
        Assert.Equal(reopenTime.AddMinutes(480), ticket.SlaDueAt);
        Assert.Equal(Status.Assigned, ticket.Status); // assignee still on record
    }

    [Fact]
    public void Reopen_PreservesPriorEventsInHistory()
    {
        var ticket = CreateInProgressTicket();
        ticket.Resolve(Resolution.Fixed, "Replaced the toner cartridge.", AssigneeId, Now);
        var eventCountBeforeReopen = ticket.Events.Count;

        ticket.Reopen("Issue recurred", RequesterId, Now.AddDays(1), DefaultSlaConfigurations());

        Assert.True(ticket.Events.Count > eventCountBeforeReopen);
        Assert.Contains(ticket.Events, e => e.EventType == TicketEventType.Resolved);
    }

    [Fact]
    public void Reopen_WithoutReason_Throws()
    {
        var ticket = CreateInProgressTicket();
        ticket.Resolve(Resolution.Fixed, "Replaced the toner cartridge.", AssigneeId, Now);

        var ex = Assert.Throws<DomainRuleException>(() => ticket.Reopen(" ", RequesterId, Now, DefaultSlaConfigurations()));
        Assert.Equal("TICKET-WF-09", ex.RuleCode);
    }

    // ---- TICKET-WF-10 — the exhaustive "everything else is rejected" complement ----

    [Theory]
    [InlineData(Status.Open)]
    [InlineData(Status.InProgress)]
    [InlineData(Status.Pending)]
    [InlineData(Status.Resolved)]
    [InlineData(Status.Closed)]
    public void StartWork_FromAnyNonAssignedStatus_Throws(Status status)
    {
        var ticket = BuildTicketInStatus(status);
        Assert.Throws<DomainRuleException>(() => ticket.StartWork(new TicketActor(AssigneeId, UserRole.Agent), Now.AddDays(10)));
    }

    [Theory] // TICKET-WF-09 is only legal from Resolved/Closed — every other status must reject it
    [InlineData(Status.Open)]
    [InlineData(Status.Assigned)]
    [InlineData(Status.InProgress)]
    [InlineData(Status.Pending)]
    public void Reopen_FromNonTerminalStatus_Throws(Status status)
    {
        var ticket = BuildTicketInStatus(status);
        Assert.Throws<DomainRuleException>(() => ticket.Reopen("reason", RequesterId, Now.AddDays(10), DefaultSlaConfigurations()));
    }

    [Fact] // TICKET-WF-09 — Resolved is a legal Reopen source, not just Closed
    public void Reopen_FromResolved_Succeeds()
    {
        var ticket = BuildResolvedForReopenTest();
        ticket.Reopen("Recurred before closing", RequesterId, Now.AddDays(1), DefaultSlaConfigurations());
        Assert.True(ticket.Status is Status.Open or Status.Assigned);
    }

    private static Ticket BuildResolvedForReopenTest() => BuildResolved();

    // ---- TICKET-WF-11 ----

    [Theory]
    [InlineData(Status.Assigned)]
    [InlineData(Status.InProgress)]
    [InlineData(Status.Pending)]
    public void Reassign_FromLegalStatuses_IncrementsChangeCount(Status status)
    {
        var ticket = BuildTicketInStatus(status);
        ticket.Reassign(OtherAgentId, newAssigneeIsActiveTeamMember: true, ManagerId, Now.AddDays(10));

        Assert.Equal(1, ticket.AssignmentChangeCount);
        Assert.Equal(OtherAgentId, ticket.AssigneeId);
    }

    [Fact]
    public void Reassign_FromInProgressToDifferentPerson_RevertsToAssigned()
    {
        var ticket = CreateInProgressTicket();
        ticket.Reassign(OtherAgentId, true, ManagerId, Now);
        Assert.Equal(Status.Assigned, ticket.Status);
    }

    [Fact]
    public void Reassign_FromInProgressToSamePerson_StatusUnchanged()
    {
        var ticket = CreateInProgressTicket();
        ticket.Reassign(AssigneeId, true, ManagerId, Now);
        Assert.Equal(Status.InProgress, ticket.Status);
    }

    [Fact]
    public void Reassign_FromOpen_Throws()
    {
        var ticket = CreateOpenTicket();
        Assert.Throws<DomainRuleException>(() => ticket.Reassign(AssigneeId, true, ManagerId, Now));
    }

    [Fact] // TICKET-INV-03 applies equally to Reassign
    public void Reassign_ToInactiveTeamMember_Throws()
    {
        var ticket = CreateAssignedTicket();
        var ex = Assert.Throws<DomainRuleException>(() => ticket.Reassign(OtherAgentId, false, ManagerId, Now));
        Assert.Equal("TICKET-INV-03", ex.RuleCode);
    }

    // ---- TICKET-INV-08 ----

    [Theory]
    [InlineData(Status.Resolved)]
    [InlineData(Status.Closed)]
    public void ChangePriority_OnTerminalTicket_Throws(Status status)
    {
        var ticket = BuildTicketInStatus(status);
        var ex = Assert.Throws<DomainRuleException>(() => ticket.ChangePriority(Priority.High, DefaultSlaConfigurations(), ManagerId, Now));
        Assert.Equal("TICKET-INV-08", ex.RuleCode);
    }

    [Theory]
    [InlineData(Status.Resolved)]
    [InlineData(Status.Closed)]
    public void ChangeCategory_OnTerminalTicket_Throws(Status status)
    {
        var ticket = BuildTicketInStatus(status);
        var ex = Assert.Throws<DomainRuleException>(() => ticket.ChangeCategory(99, TeamId, ManagerId, Now));
        Assert.Equal("TICKET-INV-08", ex.RuleCode);
    }

    [Theory] // Project-owner decision: resolved/closed tickets are completed historical work —
             // a team change afterward would corrupt historical ownership/reporting.
    [InlineData(Status.Resolved)]
    [InlineData(Status.Closed)]
    public void ChangeTeam_OnTerminalTicket_Throws(Status status)
    {
        var ticket = BuildTicketInStatus(status);
        var ex = Assert.Throws<DomainRuleException>(() => ticket.ChangeTeam(TeamId + 1, ManagerId, Now));
        Assert.Equal("TICKET-INV-08", ex.RuleCode);
    }

    [Theory] // ChangeTeam remains legal everywhere the domain contract does not block it
    [InlineData(Status.Open)]
    [InlineData(Status.Assigned)]
    [InlineData(Status.InProgress)]
    [InlineData(Status.Pending)]
    public void ChangeTeam_OnNonTerminalTicket_Succeeds(Status status)
    {
        var ticket = BuildTicketInStatus(status);
        var hadAssignee = ticket.AssigneeId.HasValue;

        ticket.ChangeTeam(TeamId + 1, ManagerId, Now.AddDays(10));

        Assert.Equal(TeamId + 1, ticket.TeamId);
        if (hadAssignee)
        {
            Assert.Null(ticket.AssigneeId); // TICKET-INV-03: old assignee not necessarily valid on the new team
        }
        Assert.Contains(ticket.Events, e => e.EventType == TicketEventType.TeamChanged);
    }

    [Fact] // AUDIT-RULE-03 / TICKET-INV-09 resolution: exactly one event, and it is PriorityChanged, not SlaRecalculated
    public void ChangePriority_AppendsExactlyOnePriorityChangedEvent_NotSlaRecalculated()
    {
        var ticket = CreateInProgressTicket(priority: Priority.Medium, slaTargetMinutes: 1440);
        var eventsBefore = ticket.Events.Count;

        ticket.ChangePriority(Priority.Critical, DefaultSlaConfigurations(), ManagerId, Now);

        Assert.Equal(eventsBefore + 1, ticket.Events.Count);
        Assert.Equal(TicketEventType.PriorityChanged, ticket.Events.Last().EventType);
        Assert.DoesNotContain(ticket.Events, e => e.EventType == TicketEventType.SlaRecalculated);
    }

    [Fact] // SLA-RULE-06 — the specific formula: SlaStartedAt is NOT reset
    public void ChangePriority_RecomputesSlaDueAt_WithoutResettingSlaStartedAt()
    {
        var ticket = CreateInProgressTicket(priority: Priority.Medium, slaTargetMinutes: 1440);
        ticket.PutOnHold("Waiting on part", AssigneeId, Now);
        ticket.Resume(AssigneeId, Now.AddMinutes(60)); // accrues 60 paused minutes
        var startedAt = ticket.SlaStartedAt;

        ticket.ChangePriority(Priority.Critical, DefaultSlaConfigurations(), ManagerId, Now.AddMinutes(60));

        Assert.Equal(startedAt, ticket.SlaStartedAt); // unchanged — this is the specific mistake the skill warns about
        Assert.Equal(240, ticket.SlaTargetMinutes);
        Assert.Equal(startedAt.AddMinutes(240 + 60), ticket.SlaDueAt);
    }

    [Fact] // TICKET-INV-02 applies to category changes too
    public void ChangeCategory_ToCategoryOfDifferentTeam_Throws()
    {
        var ticket = CreateOpenTicket();
        var ex = Assert.Throws<DomainRuleException>(() => ticket.ChangeCategory(99, newCategoryTeamId: TeamId + 1, ManagerId, Now));
        Assert.Equal("TICKET-INV-02", ex.RuleCode);
    }

    // ---- TICKET-INV-09 — every state-changing method appends exactly one event ----

    [Fact]
    public void EveryTransition_AppendsExactlyOneEvent()
    {
        var ticket = CreateOpenTicket();
        AssertAppendsExactlyOneEvent(ticket, t => t.Assign(AssigneeId, true, ManagerId, Now));
        AssertAppendsExactlyOneEvent(ticket, t => t.StartWork(new TicketActor(AssigneeId, UserRole.Agent), Now));
        AssertAppendsExactlyOneEvent(ticket, t => t.PutOnHold("Waiting on requester", AssigneeId, Now));
        AssertAppendsExactlyOneEvent(ticket, t => t.Resume(AssigneeId, Now.AddMinutes(10)));
        AssertAppendsExactlyOneEvent(ticket, t => t.Resolve(Resolution.Fixed, "Replaced the toner cartridge.", AssigneeId, Now.AddMinutes(20)));
        AssertAppendsExactlyOneEvent(ticket, t => t.Close(new TicketActor(ManagerId, UserRole.Manager), Now.AddMinutes(30)));
        AssertAppendsExactlyOneEvent(ticket, t => t.Reopen("Recurred", RequesterId, Now.AddDays(1), DefaultSlaConfigurations()));
    }

    private static void AssertAppendsExactlyOneEvent(Ticket ticket, Action<Ticket> action)
    {
        var before = ticket.Events.Count;
        action(ticket);
        Assert.Equal(before + 1, ticket.Events.Count);
    }

    // ---- TICKET-WF-10 — everything not in the legal transition table is rejected ----

    /// <summary>The operations the state machine gates by status, for the rejection matrix below.</summary>
    public enum WorkflowOperation
    {
        Assign,
        Unassign,
        PutOnHold,
        Resume,
        Resolve,
        Close,
        Reassign,
    }

    /// <summary>
    /// Every <c>(status, operation)</c> pair absent from CLAUDE.md §5's legal transition table.
    /// Each case supplies otherwise-valid arguments — an active team member, a non-blank reason,
    /// long-enough notes, a privileged actor — so the only thing that can reject the call is the
    /// status guard itself, which is what TICKET-WF-10 is about.
    /// </summary>
    /// <remarks>
    /// <c>StartWork</c> and <c>Reopen</c> are absent from this matrix on purpose: their full
    /// rejection sets are already covered exhaustively by
    /// <see cref="StartWork_FromAnyNonAssignedStatus_Throws"/> and
    /// <see cref="Reopen_FromNonTerminalStatus_Throws"/>.
    /// </remarks>
    [Theory]
    // Assign is legal only from Open.
    [InlineData(Status.Assigned, WorkflowOperation.Assign)]
    [InlineData(Status.InProgress, WorkflowOperation.Assign)]
    [InlineData(Status.Pending, WorkflowOperation.Assign)]
    [InlineData(Status.Resolved, WorkflowOperation.Assign)]
    [InlineData(Status.Closed, WorkflowOperation.Assign)]
    // Unassign is legal only from Assigned.
    [InlineData(Status.Open, WorkflowOperation.Unassign)]
    [InlineData(Status.InProgress, WorkflowOperation.Unassign)]
    [InlineData(Status.Pending, WorkflowOperation.Unassign)]
    [InlineData(Status.Resolved, WorkflowOperation.Unassign)]
    [InlineData(Status.Closed, WorkflowOperation.Unassign)]
    // PutOnHold is legal only from Assigned or InProgress.
    [InlineData(Status.Open, WorkflowOperation.PutOnHold)]
    [InlineData(Status.Pending, WorkflowOperation.PutOnHold)]
    [InlineData(Status.Resolved, WorkflowOperation.PutOnHold)]
    [InlineData(Status.Closed, WorkflowOperation.PutOnHold)]
    // Resume is legal only from Pending.
    [InlineData(Status.Open, WorkflowOperation.Resume)]
    [InlineData(Status.Assigned, WorkflowOperation.Resume)]
    [InlineData(Status.InProgress, WorkflowOperation.Resume)]
    [InlineData(Status.Resolved, WorkflowOperation.Resume)]
    [InlineData(Status.Closed, WorkflowOperation.Resume)]
    // Resolve is legal only from InProgress or Pending.
    [InlineData(Status.Open, WorkflowOperation.Resolve)]
    [InlineData(Status.Assigned, WorkflowOperation.Resolve)]
    [InlineData(Status.Resolved, WorkflowOperation.Resolve)]
    [InlineData(Status.Closed, WorkflowOperation.Resolve)]
    // Close is legal only from Resolved.
    [InlineData(Status.Open, WorkflowOperation.Close)]
    [InlineData(Status.Assigned, WorkflowOperation.Close)]
    [InlineData(Status.InProgress, WorkflowOperation.Close)]
    [InlineData(Status.Pending, WorkflowOperation.Close)]
    [InlineData(Status.Closed, WorkflowOperation.Close)]
    // Reassign is legal only from Assigned, InProgress or Pending.
    [InlineData(Status.Open, WorkflowOperation.Reassign)]
    [InlineData(Status.Resolved, WorkflowOperation.Reassign)]
    [InlineData(Status.Closed, WorkflowOperation.Reassign)]
    public void IllegalTransition_IsRejected_AndAppendsNoEvent(Status status, WorkflowOperation operation)
    {
        var ticket = BuildTicketInStatus(status);
        var statusBefore = ticket.Status;
        var eventsBefore = ticket.Events.Count;

        Assert.Throws<DomainRuleException>(() => Invoke(ticket, operation));

        // A rejected transition changes nothing at all — no status move, no audit row.
        Assert.Equal(statusBefore, ticket.Status);
        Assert.Equal(eventsBefore, ticket.Events.Count);
    }

    private static void Invoke(Ticket ticket, WorkflowOperation operation)
    {
        switch (operation)
        {
            case WorkflowOperation.Assign:
                ticket.Assign(AssigneeId, assigneeIsActiveTeamMember: true, ManagerId, Now);
                break;
            case WorkflowOperation.Unassign:
                ticket.Unassign(ManagerId, Now);
                break;
            case WorkflowOperation.PutOnHold:
                ticket.PutOnHold("Waiting on requester", ManagerId, Now);
                break;
            case WorkflowOperation.Resume:
                ticket.Resume(ManagerId, Now);
                break;
            case WorkflowOperation.Resolve:
                ticket.Resolve(Resolution.Fixed, "Replaced the toner cartridge.", ManagerId, Now);
                break;
            case WorkflowOperation.Close:
                ticket.Close(new TicketActor(ManagerId, UserRole.Manager), Now);
                break;
            case WorkflowOperation.Reassign:
                ticket.Reassign(OtherAgentId, newAssigneeIsActiveTeamMember: true, ManagerId, Now);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    // ---- AUDIT-RULE-02 — event metadata is populated exactly where the method defines it ----

    [Fact]
    public void Assign_EventRecordsAssigneeChange()
    {
        var ticket = CreateOpenTicket();
        ticket.Assign(AssigneeId, assigneeIsActiveTeamMember: true, ManagerId, Now);

        var appended = ticket.Events.Last();
        Assert.Equal(TicketEventType.Assigned, appended.EventType);
        Assert.Equal("AssigneeId", appended.Field);
        Assert.Null(appended.OldValue);
        Assert.Equal(AssigneeId.ToString(), appended.NewValue);
        Assert.Equal(ManagerId, appended.ActorUserId);
        Assert.Equal(Now, appended.OccurredAt);
    }

    [Fact]
    public void Unassign_EventRecordsThePreviousAssignee()
    {
        var ticket = CreateAssignedTicket();
        ticket.Unassign(ManagerId, Now);

        var appended = ticket.Events.Last();
        Assert.Equal(TicketEventType.Unassigned, appended.EventType);
        Assert.Equal("AssigneeId", appended.Field);
        Assert.Equal(AssigneeId.ToString(), appended.OldValue);
        Assert.Null(appended.NewValue);
    }

    [Fact]
    public void Reassign_EventRecordsBothOldAndNewAssignee()
    {
        var ticket = CreateAssignedTicket();
        ticket.Reassign(OtherAgentId, newAssigneeIsActiveTeamMember: true, ManagerId, Now);

        var appended = ticket.Events.Last();
        Assert.Equal(TicketEventType.Reassigned, appended.EventType);
        Assert.Equal("AssigneeId", appended.Field);
        Assert.Equal(AssigneeId.ToString(), appended.OldValue);
        Assert.Equal(OtherAgentId.ToString(), appended.NewValue);
    }

    [Fact]
    public void StartWork_EventRecordsTheStatusChange()
    {
        var ticket = CreateAssignedTicket();
        ticket.StartWork(new TicketActor(AssigneeId, UserRole.Agent), Now);

        var appended = ticket.Events.Last();
        Assert.Equal(TicketEventType.StatusChanged, appended.EventType);
        Assert.Equal("Status", appended.Field);
        Assert.Equal(nameof(Status.Assigned), appended.OldValue);
        Assert.Equal(nameof(Status.InProgress), appended.NewValue);
    }

    [Theory] // Reason-carrying transitions record it as Note, with no field-change triple.
    [InlineData(true)]
    [InlineData(false)]
    public void ReasonCarryingTransitions_RecordTheReasonAsNote(bool onHold)
    {
        const string reason = "Waiting on the requester to confirm";
        var ticket = onHold ? CreateInProgressTicket() : BuildClosed();

        if (onHold)
        {
            ticket.PutOnHold(reason, AssigneeId, Now);
        }
        else
        {
            ticket.Reopen(reason, RequesterId, Now, DefaultSlaConfigurations());
        }

        var appended = ticket.Events.Last();
        Assert.Equal(onHold ? TicketEventType.PutOnHold : TicketEventType.Reopened, appended.EventType);
        Assert.Equal(reason, appended.Note);
        Assert.Null(appended.Field);
        Assert.Null(appended.OldValue);
        Assert.Null(appended.NewValue);
    }

    [Fact] // AUDIT-RULE-02: actor and timestamp are never optional, on any event.
    public void EveryEvent_CarriesAnActorAndTimestamp()
    {
        var ticket = BuildClosed();

        Assert.All(ticket.Events, e =>
        {
            Assert.NotEqual(Guid.Empty, e.ActorUserId);
            Assert.NotEqual(default, e.OccurredAt);
        });
    }

    private static Ticket BuildTicketInStatus(Status status)
    {
        return status switch
        {
            Status.Open => CreateOpenTicket(),
            Status.Assigned => CreateAssignedTicket(),
            Status.InProgress => CreateInProgressTicket(),
            Status.Pending => BuildPending(),
            Status.Resolved => BuildResolved(),
            Status.Closed => BuildClosed(),
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };
    }

    private static Ticket BuildPending()
    {
        var ticket = CreateInProgressTicket();
        ticket.PutOnHold("Waiting on requester", AssigneeId, Now);
        return ticket;
    }

    private static Ticket BuildResolved()
    {
        var ticket = CreateInProgressTicket();
        ticket.Resolve(Resolution.Fixed, "Replaced the toner cartridge.", AssigneeId, Now);
        return ticket;
    }

    private static Ticket BuildClosed()
    {
        var ticket = BuildResolved();
        ticket.Close(new TicketActor(ManagerId, UserRole.Manager), Now);
        return ticket;
    }
}
