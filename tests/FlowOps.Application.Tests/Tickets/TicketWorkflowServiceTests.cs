using FlowOps.Application.Tests.Persistence;
using FlowOps.Application.Tickets;
using FlowOps.Domain;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FlowOps.Application.Tests.Tickets;

/// <summary>
/// Phase 6 workflow orchestration against real PostgreSQL: every transition persists its status
/// change and its audit event in one transaction, rejections leave both untouched, and the
/// authorization decision happens before anything is mutated.
/// </summary>
[Collection("Postgres")]
public sealed class TicketWorkflowServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 5, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;

    public TicketWorkflowServiceTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact] // TICKET-WF-01: legal transition persists state and exactly one matching event.
    public async Task AssignAsync_PersistsStatusAndAuditEventTogether()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        await world.Service.AssignAsync(world.TicketId, world.AgentId, world.Agent);

        await using var verify = _fixture.CreateContext();
        var ticket = await verify.Tickets.AsNoTracking().SingleAsync(t => t.Id == world.TicketId);
        var events = await EventsAsync(verify, world.TicketId);

        Assert.Equal(Status.Assigned, ticket.Status);
        Assert.Equal(world.AgentId, ticket.AssigneeId);
        Assert.Equal([TicketEventType.Created, TicketEventType.Assigned], events.Select(e => e.EventType));

        var assigned = events.Last();
        Assert.Equal("AssigneeId", assigned.Field);
        Assert.Equal(world.AgentId.ToString(), assigned.NewValue);
        Assert.Equal(world.AgentId, assigned.ActorUserId);
    }

    [Fact] // TICKET-WF-10: an illegal transition changes nothing and writes no audit row.
    public async Task IllegalTransition_IsRejected_AndPersistsNothing()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        // The ticket is Open; StartWork is legal only from Assigned. Attempted as Admin, who the
        // policy always permits, so the rejection can only come from the status guard —
        // authorization is checked before the domain rule, so a less privileged actor would be
        // refused by the policy first and never reach it.
        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            world.Service.StartWorkAsync(world.TicketId, world.Admin));

        Assert.Equal("TICKET-WF-03", ex.RuleCode);

        await using var verify = _fixture.CreateContext();
        var ticket = await verify.Tickets.AsNoTracking().SingleAsync(t => t.Id == world.TicketId);

        Assert.Equal(Status.Open, ticket.Status);
        Assert.Equal([TicketEventType.Created], (await EventsAsync(verify, world.TicketId)).Select(e => e.EventType));
    }

    [Fact] // Authorization is decided before any mutation happens.
    public async Task UnauthorizedActor_IsRejectedBeforeMutating()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        var outsider = TicketTestData.User(await TicketTestData.AddUserAsync(context), UserRole.Agent, world.OtherTeamId);

        await Assert.ThrowsAsync<TicketAccessDeniedException>(() =>
            world.Service.AssignAsync(world.TicketId, outsider.UserId, outsider));

        await using var verify = _fixture.CreateContext();
        var ticket = await verify.Tickets.AsNoTracking().SingleAsync(t => t.Id == world.TicketId);

        Assert.Equal(Status.Open, ticket.Status);
        Assert.Null(ticket.AssigneeId);
        Assert.Single(await EventsAsync(verify, world.TicketId));
    }

    [Fact] // A ticket the caller cannot see is refused exactly like one that does not exist.
    public async Task UnknownTicket_AndForbiddenTicket_AreIndistinguishable()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        var outsider = TicketTestData.User(await TicketTestData.AddUserAsync(context), UserRole.Agent, world.OtherTeamId);

        var forbidden = await Assert.ThrowsAsync<TicketAccessDeniedException>(() =>
            world.Service.StartWorkAsync(world.TicketId, outsider));
        var missing = await Assert.ThrowsAsync<TicketAccessDeniedException>(() =>
            world.Service.StartWorkAsync(int.MaxValue, outsider));

        Assert.Equal(missing.Message, forbidden.Message);
    }

    [Fact] // TICKET-INV-03: the assignee must be a member of the ticket's team.
    public async Task AssignAsync_ToNonMemberOfTheTeam_IsRejected()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        var stranger = await TicketTestData.AddUserAsync(context);

        // An Admin may assign anyone, so the policy permits the call and only the domain rule
        // can reject it — which is what this test is about.
        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            world.Service.AssignAsync(world.TicketId, stranger, world.Admin));

        Assert.Equal("TICKET-INV-03", ex.RuleCode);
    }

    [Fact] // TICKET-INV-03's other half: membership alone is not enough, the account must be active.
    public async Task AssignAsync_ToInactiveTeamMember_IsRejected()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        var inactiveId = await TicketTestData.AddUserAsync(context);
        await TicketTestData.AddTeamMembershipAsync(context, world.TeamId, inactiveId);
        var inactive = await context.Users.SingleAsync(u => u.Id == inactiveId);
        inactive.IsActive = false;
        await context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            world.Service.AssignAsync(world.TicketId, inactiveId, world.Admin));

        Assert.Equal("TICKET-INV-03", ex.RuleCode);
    }

    [Fact] // TICKET-WF-04/05: pending state persists, and Resume restores the prior status.
    public async Task PutOnHoldThenResume_PersistsPendingStateAndReturnsToPriorStatus()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        await world.Service.AssignAsync(world.TicketId, world.AgentId, world.Agent);
        await world.Service.StartWorkAsync(world.TicketId, world.Agent);
        await world.Service.PutOnHoldAsync(world.TicketId, "Waiting on the requester", world.Agent);

        await using (var mid = _fixture.CreateContext())
        {
            var onHold = await mid.Tickets.AsNoTracking().SingleAsync(t => t.Id == world.TicketId);
            Assert.Equal(Status.Pending, onHold.Status);
            Assert.Equal("Waiting on the requester", onHold.PendingReason);
            Assert.NotNull(onHold.PendingSince);
            Assert.Equal(0, onHold.SlaPausedMinutes); // accrues on Resume, not on PutOnHold
        }

        world.Clock.Advance(TimeSpan.FromMinutes(30));
        await world.Service.ResumeAsync(world.TicketId, world.Agent);

        await using var verify = _fixture.CreateContext();
        var resumed = await verify.Tickets.AsNoTracking().SingleAsync(t => t.Id == world.TicketId);

        Assert.Equal(Status.InProgress, resumed.Status);
        Assert.Null(resumed.PendingReason);
        Assert.Equal(30, resumed.SlaPausedMinutes); // existing aggregate arithmetic, persisted
    }

    [Fact] // TICKET-WF-08: the requester may close; an Agent who is only the assignee may not.
    public async Task CloseAsync_RequesterMayClose_AssigneeOnlyAgentMayNot()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        await world.Service.AssignAsync(world.TicketId, world.AgentId, world.Agent);
        await world.Service.StartWorkAsync(world.TicketId, world.Agent);
        await world.Service.ResolveAsync(world.TicketId, Resolution.Fixed, "Replaced the toner cartridge.", world.Agent);

        // The agent is the assignee but not the requester: CanClose refuses.
        await Assert.ThrowsAsync<TicketAccessDeniedException>(() =>
            world.Service.CloseAsync(world.TicketId, world.Agent));

        // The requester closes their own ticket.
        await world.Service.CloseAsync(world.TicketId, world.Requester);

        await using var verify = _fixture.CreateContext();
        var ticket = await verify.Tickets.AsNoTracking().SingleAsync(t => t.Id == world.TicketId);

        Assert.Equal(Status.Closed, ticket.Status);
        Assert.NotNull(ticket.ClosedAt);
    }

    [Fact] // TICKET-WF-09: Application supplies the SLA configuration; the aggregate resolves the cycle.
    public async Task ReopenAsync_StartsANewSlaCycleFromPersistedConfiguration()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        await world.Service.AssignAsync(world.TicketId, world.AgentId, world.Agent);
        await world.Service.StartWorkAsync(world.TicketId, world.Agent);
        await world.Service.ResolveAsync(world.TicketId, Resolution.Fixed, "Replaced the toner cartridge.", world.Agent);

        world.Clock.Advance(TimeSpan.FromHours(2));
        var reopenedAt = world.Clock.GetUtcNow();
        await world.Service.ReopenAsync(world.TicketId, "The fault recurred overnight", world.Requester);

        await using var verify = _fixture.CreateContext();
        var ticket = await verify.Tickets.AsNoTracking().SingleAsync(t => t.Id == world.TicketId);

        Assert.Equal(1, ticket.ReopenCount);
        Assert.Null(ticket.SlaMet);
        Assert.Null(ticket.ResolvedAt);
        Assert.Equal(reopenedAt, ticket.SlaStartedAt);
        // Medium priority resolves to the seeded 1440-minute default row (SLA-RULE-02).
        Assert.Equal(1440, ticket.SlaTargetMinutes);
        Assert.Equal(reopenedAt.AddMinutes(1440), ticket.SlaDueAt);
    }

    [Fact] // Aggregate state and audit trail stay consistent across a full lifecycle.
    public async Task FullLifecycle_LeavesStateAndAuditTrailConsistent()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        await world.Service.AssignAsync(world.TicketId, world.AgentId, world.Agent);
        await world.Service.StartWorkAsync(world.TicketId, world.Agent);
        await world.Service.PutOnHoldAsync(world.TicketId, "Waiting on parts", world.Agent);
        world.Clock.Advance(TimeSpan.FromMinutes(10));
        await world.Service.ResumeAsync(world.TicketId, world.Agent);
        await world.Service.ResolveAsync(world.TicketId, Resolution.Fixed, "Replaced the toner cartridge.", world.Agent);
        await world.Service.CloseAsync(world.TicketId, world.Manager);

        await using var verify = _fixture.CreateContext();
        var ticket = await verify.Tickets.AsNoTracking().SingleAsync(t => t.Id == world.TicketId);
        var events = await EventsAsync(verify, world.TicketId);

        Assert.Equal(Status.Closed, ticket.Status);
        Assert.Equal(
            [
                TicketEventType.Created,
                TicketEventType.Assigned,
                TicketEventType.StatusChanged,
                TicketEventType.PutOnHold,
                TicketEventType.Resumed,
                TicketEventType.Resolved,
                TicketEventType.Closed,
            ],
            events.Select(e => e.EventType));

        // One event per state-changing call, and the ticket's UpdatedAt tracks the last of them.
        Assert.Equal(ticket.UpdatedAt, events.Last().OccurredAt);
    }

    [Fact] // ADR-0011: a concurrent edit surfaces as a conflict, never a silent lost update.
    public async Task ConcurrentTransitions_SurfaceAConcurrencyConflict()
    {
        await using var setup = _fixture.CreateContext();
        var world = await SeedAsync(setup);

        // Two callers each load the ticket while it is still Open.
        await using var firstContext = _fixture.CreateContext();
        await using var secondContext = _fixture.CreateContext();
        var firstService = new TicketService(firstContext, world.Clock, TestEmail.Sender, TestEmail.Options);
        var secondService = new TicketService(secondContext, world.Clock, TestEmail.Sender, TestEmail.Options);

        await secondContext.Tickets.SingleAsync(t => t.Id == world.TicketId); // primes the stale xmin

        await firstService.AssignAsync(world.TicketId, world.AgentId, world.Agent);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            secondService.AssignAsync(world.TicketId, world.AgentId, world.Agent));
    }

    [Fact] // History is guarded by the same visibility scope as the ticket itself.
    public async Task GetHistoryAsync_IsViewScoped()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        await world.Service.AssignAsync(world.TicketId, world.AgentId, world.Agent);

        var query = new TicketQueryService(context, TimeProvider.System);
        var outsider = TicketTestData.User(await TicketTestData.AddUserAsync(context), UserRole.Agent, world.OtherTeamId);

        var visible = await query.GetHistoryAsync(world.TicketId, world.Agent);
        var hidden = await query.GetHistoryAsync(world.TicketId, outsider);

        Assert.Equal(2, visible.Count); // Created + Assigned, newest first
        Assert.Equal(TicketEventType.Assigned, visible[0].EventType);
        Assert.Equal("Phase 5 Test User", visible[0].ActorDisplayName);
        Assert.Empty(hidden);
    }

    private static Task<List<TicketEvent>> EventsAsync(FlowOpsDbContext context, int ticketId) =>
        context.TicketEvents
            .AsNoTracking()
            .Where(e => e.TicketId == ticketId)
            .OrderBy(e => e.OccurredAt)
            .ThenBy(e => e.Id)
            .ToListAsync();

    private sealed record World(
        int TicketId,
        int TeamId,
        int OtherTeamId,
        Guid AgentId,
        CurrentUser Agent,
        CurrentUser Requester,
        CurrentUser Manager,
        CurrentUser Admin,
        TicketService Service,
        TicketTestData.FixedTimeProvider Clock);

    /// <summary>
    /// One team with an agent and a separate requester, plus a second team used to prove
    /// cross-team refusal. The ticket starts Open, created by the requester.
    /// </summary>
    private static async Task<World> SeedAsync(FlowOpsDbContext context)
    {
        var teamId = await TicketTestData.AddTeamAsync(context);
        var otherTeamId = await TicketTestData.AddTeamAsync(context);
        var categoryId = await TicketTestData.AddCategoryAsync(context, teamId);

        var agentId = await TicketTestData.AddUserAsync(context);
        var requesterId = await TicketTestData.AddUserAsync(context);
        var managerId = await TicketTestData.AddUserAsync(context);
        var adminId = await TicketTestData.AddUserAsync(context);

        await TicketTestData.AddTeamMembershipAsync(context, teamId, agentId);
        await TicketTestData.AddTeamMembershipAsync(context, teamId, requesterId);
        await TicketTestData.AddTeamMembershipAsync(context, teamId, managerId, isTeamManager: true);

        var clock = new TicketTestData.FixedTimeProvider(Start);
        var service = new TicketService(context, clock, TestEmail.Sender, TestEmail.Options);
        var requester = TicketTestData.User(requesterId, UserRole.Agent, teamId);

        var (ticketId, _) = await service.CreateAsync(
            new CreateTicketRequest(
                Title: "Printer on 3rd floor is jammed",
                Description: "The printer near the east stairwell is jammed and needs a technician.",
                WorkType: WorkType.Incident,
                Priority: Priority.Medium,
                TeamId: teamId,
                CategoryId: categoryId,
                ProjectId: null),
            requester);

        return new World(
            ticketId,
            teamId,
            otherTeamId,
            agentId,
            TicketTestData.User(agentId, UserRole.Agent, teamId),
            requester,
            TicketTestData.Manager(managerId, teamId),
            TicketTestData.User(adminId, UserRole.Admin),
            service,
            clock);
    }
}
