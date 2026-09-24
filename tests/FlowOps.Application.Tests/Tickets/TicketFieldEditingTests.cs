using FlowOps.Application.Tests.Persistence;
using FlowOps.Application.Tickets;
using FlowOps.Domain;
using FlowOps.Domain.Catalog;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FlowOps.Application.Tests.Tickets;

/// <summary>
/// Phase 30C (ADR-0033) against real PostgreSQL: editing Priority/Category/Team/Due date on an
/// existing ticket — each field's own domain invariant, its audit event, authorization (the same
/// <see cref="TicketAccessPolicy.CanTransition"/> footprint "Change priority" already had), and
/// that a caller-supplied category/team id is never trusted without re-validation.
/// </summary>
[Collection("Postgres")]
public sealed class TicketFieldEditingTests
{
    private static readonly DateTimeOffset Start = new(2026, 5, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;

    public TicketFieldEditingTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ChangePriorityAsync_PersistsNewPriority_RecalculatesSla_WithOneAuditEvent()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        var oldDueAt = (await context.Tickets.AsNoTracking().SingleAsync(t => t.Id == world.TicketId)).SlaDueAt;

        await world.Service.ChangePriorityAsync(world.TicketId, Priority.Critical, world.Manager);

        var ticket = await FreshAsync(world.TicketId);
        Assert.Equal(Priority.Critical, ticket.Priority);
        Assert.NotEqual(oldDueAt, ticket.SlaDueAt);
        var events = await EventsAsync(world.TicketId);
        Assert.Equal(TicketEventType.PriorityChanged, events.Last().EventType);
        Assert.Equal("Priority", events.Last().Field);
        Assert.Equal("Medium", events.Last().OldValue);
        Assert.Equal("Critical", events.Last().NewValue);
    }

    [Theory]
    [InlineData(UserRole.Viewer)]
    public async Task ChangePriorityAsync_UnauthorizedActor_Throws(UserRole role)
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        var outsider = TicketTestData.User(await TicketTestData.AddUserAsync(context), role, world.TeamId);

        await Assert.ThrowsAsync<TicketAccessDeniedException>(
            () => world.Service.ChangePriorityAsync(world.TicketId, Priority.Critical, outsider));

        Assert.Equal(Priority.Medium, (await FreshAsync(world.TicketId)).Priority);
    }

    [Fact]
    public async Task ChangePriorityAsync_OnAResolvedTicket_Throws()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        await world.Service.AssignAsync(world.TicketId, world.AgentId, world.Manager);
        await world.Service.StartWorkAsync(world.TicketId, world.Agent);
        await world.Service.ResolveAsync(world.TicketId, Resolution.Fixed, "Reseated the cable.", world.Agent);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => world.Service.ChangePriorityAsync(world.TicketId, Priority.Critical, world.Manager));

        Assert.Equal("TICKET-INV-08", ex.RuleCode);
    }

    [Fact]
    public async Task ChangeCategoryAsync_PersistsNewCategory_WithOneAuditEvent()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        var secondCategoryId = await TicketTestData.AddCategoryAsync(context, world.TeamId);

        await world.Service.ChangeCategoryAsync(world.TicketId, secondCategoryId, world.Manager);

        var ticket = await FreshAsync(world.TicketId);
        Assert.Equal(secondCategoryId, ticket.CategoryId);
        var events = await EventsAsync(world.TicketId);
        Assert.Equal(TicketEventType.CategoryChanged, events.Last().EventType);
    }

    [Fact]
    public async Task ChangeCategoryAsync_CategoryBelongingToAnotherTeam_Throws()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        var otherTeamCategoryId = await TicketTestData.AddCategoryAsync(context, world.OtherTeamId);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => world.Service.ChangeCategoryAsync(world.TicketId, otherTeamCategoryId, world.Manager));

        Assert.Equal("TICKET-INV-02", ex.RuleCode);
        Assert.Equal(world.CategoryId, (await FreshAsync(world.TicketId)).CategoryId);
    }

    [Fact]
    public async Task ChangeCategoryAsync_InactiveCategory_Throws()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        var inactiveCategory = new Category(0, world.TeamId, "Retired", WorkType.Incident, Start, isActive: false);
        context.Add(inactiveCategory);
        await context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => world.Service.ChangeCategoryAsync(world.TicketId, inactiveCategory.Id, world.Manager));

        Assert.Equal("TICKET-INV-02", ex.RuleCode);
    }

    [Fact]
    public async Task ChangeCategoryAsync_NonexistentCategory_Throws()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        await Assert.ThrowsAsync<DomainRuleException>(
            () => world.Service.ChangeCategoryAsync(world.TicketId, 999_999, world.Manager));
    }

    [Fact]
    public async Task ChangeCategoryAsync_OnAResolvedTicket_Throws()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        await world.Service.AssignAsync(world.TicketId, world.AgentId, world.Manager);
        await world.Service.StartWorkAsync(world.TicketId, world.Agent);
        await world.Service.ResolveAsync(world.TicketId, Resolution.Fixed, "Reseated the cable.", world.Agent);
        var secondCategoryId = await TicketTestData.AddCategoryAsync(context, world.TeamId);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => world.Service.ChangeCategoryAsync(world.TicketId, secondCategoryId, world.Manager));

        Assert.Equal("TICKET-INV-08", ex.RuleCode);
    }

    [Fact] // ChangeTeamAsync derives the destination team from the chosen category — one call,
           // two audit events, and the (previously-possible) "team moved but category left
           // pointing at the old team" state is now unreachable.
    public async Task ChangeTeamAsync_MovesTeamAndCategoryTogether_ClearsTheAssignee()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        var otherTeamCategoryId = await TicketTestData.AddCategoryAsync(context, world.OtherTeamId);
        await world.Service.AssignAsync(world.TicketId, world.AgentId, world.Manager);

        await world.Service.ChangeTeamAsync(world.TicketId, otherTeamCategoryId, world.Manager);

        var ticket = await FreshAsync(world.TicketId);
        Assert.Equal(world.OtherTeamId, ticket.TeamId);
        Assert.Equal(otherTeamCategoryId, ticket.CategoryId);
        Assert.Null(ticket.AssigneeId); // always cleared, regardless of membership on the new team
        Assert.Equal(Status.Open, ticket.Status); // was Assigned; reverts when the assignee is cleared
        var eventTypes = (await EventsAsync(world.TicketId)).Select(e => e.EventType).ToList();
        Assert.Contains(TicketEventType.TeamChanged, eventTypes);
        Assert.Contains(TicketEventType.CategoryChanged, eventTypes);
    }

    [Fact]
    public async Task ChangeTeamAsync_ToACategoryInAnInactiveTeam_Throws()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        var inactiveTeamId = await TicketTestData.AddTeamAsync(context, isActive: false);
        var inactiveTeamCategoryId = await TicketTestData.AddCategoryAsync(context, inactiveTeamId);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => world.Service.ChangeTeamAsync(world.TicketId, inactiveTeamCategoryId, world.Manager));

        Assert.Equal("TICKET-INV-08", ex.RuleCode);
        Assert.Equal(world.TeamId, (await FreshAsync(world.TicketId)).TeamId);
    }

    [Fact]
    public async Task ChangeTeamAsync_ToACategoryInAnotherOrganization_Throws()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        var (_, otherOrgTeamId) = await TicketTestData.AddSecondOrganizationTeamAsync(context);
        var otherOrgCategoryId = await TicketTestData.AddCategoryAsync(context, otherOrgTeamId);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => world.Service.ChangeTeamAsync(world.TicketId, otherOrgCategoryId, world.Manager));

        Assert.Equal("TICKET-INV-08", ex.RuleCode);
        Assert.Equal(world.TeamId, (await FreshAsync(world.TicketId)).TeamId);
    }

    [Fact]
    public async Task ChangeTeamAsync_OnAResolvedTicket_Throws()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        var otherTeamCategoryId = await TicketTestData.AddCategoryAsync(context, world.OtherTeamId);
        await world.Service.AssignAsync(world.TicketId, world.AgentId, world.Manager);
        await world.Service.StartWorkAsync(world.TicketId, world.Agent);
        await world.Service.ResolveAsync(world.TicketId, Resolution.Fixed, "Reseated the cable.", world.Agent);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => world.Service.ChangeTeamAsync(world.TicketId, otherTeamCategoryId, world.Manager));

        Assert.Equal("TICKET-INV-08", ex.RuleCode);
    }

    [Fact] // Unlike Priority/Category/Team, ChangeDueDate carries no terminal-ticket restriction.
    public async Task ChangeDueDateAsync_SetsAndClears_EvenOnAResolvedTicket()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        await world.Service.AssignAsync(world.TicketId, world.AgentId, world.Manager);
        await world.Service.StartWorkAsync(world.TicketId, world.Agent);
        await world.Service.ResolveAsync(world.TicketId, Resolution.Fixed, "Reseated the cable.", world.Agent);
        var newDueDate = Start.AddDays(10);

        await world.Service.ChangeDueDateAsync(world.TicketId, newDueDate, world.Manager);
        Assert.Equal(newDueDate, (await FreshAsync(world.TicketId)).DueDate);

        await world.Service.ChangeDueDateAsync(world.TicketId, null, world.Manager);
        Assert.Null((await FreshAsync(world.TicketId)).DueDate);

        var events = await EventsAsync(world.TicketId);
        Assert.Equal(2, events.Count(e => e.EventType == TicketEventType.DueDateChanged));
    }

    [Fact]
    public async Task ChangeDueDateAsync_UnauthorizedActor_Throws()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        var outsider = TicketTestData.User(await TicketTestData.AddUserAsync(context), UserRole.Viewer, world.TeamId);

        await Assert.ThrowsAsync<TicketAccessDeniedException>(
            () => world.Service.ChangeDueDateAsync(world.TicketId, Start.AddDays(5), outsider));
    }

    // ---- helpers ----

    private async Task<Ticket> FreshAsync(int ticketId)
    {
        await using var context = _fixture.CreateContext();
        return await context.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticketId);
    }

    private async Task<List<TicketEvent>> EventsAsync(int ticketId)
    {
        await using var context = _fixture.CreateContext();
        return await context.TicketEvents
            .AsNoTracking()
            .Where(e => e.TicketId == ticketId)
            .OrderBy(e => e.OccurredAt)
            .ThenBy(e => e.Id)
            .ToListAsync();
    }

    private sealed record World(
        int TicketId,
        int TeamId,
        int OtherTeamId,
        int CategoryId,
        Guid AgentId,
        CurrentUser Agent,
        CurrentUser Manager,
        TicketService Service);

    /// <summary>One team with an assignable agent and a manager, plus a second (initially empty)
    /// team — used to prove category/team cross-boundary refusal. The ticket starts Open, Medium
    /// priority, created by the manager.</summary>
    private static async Task<World> SeedAsync(FlowOpsDbContext context)
    {
        var teamId = await TicketTestData.AddTeamAsync(context);
        var otherTeamId = await TicketTestData.AddTeamAsync(context);
        var categoryId = await TicketTestData.AddCategoryAsync(context, teamId);

        var agentId = await TicketTestData.AddUserAsync(context);
        var managerId = await TicketTestData.AddUserAsync(context);
        await TicketTestData.AddTeamMembershipAsync(context, teamId, agentId);
        await TicketTestData.AddTeamMembershipAsync(context, teamId, managerId, isTeamManager: true);

        var clock = new TicketTestData.FixedTimeProvider(Start);
        var service = new TicketService(context, clock, TestEmail.Sender, TestEmail.Options);
        var manager = TicketTestData.Manager(managerId, teamId);

        var (ticketId, _) = await service.CreateAsync(
            new CreateTicketRequest(
                Title: "Printer on 3rd floor is jammed",
                Description: "The printer near the east stairwell is jammed and needs a technician.",
                WorkType: WorkType.Incident,
                Priority: Priority.Medium,
                TeamId: teamId,
                CategoryId: categoryId,
                ProjectId: null),
            manager);

        return new World(
            ticketId,
            teamId,
            otherTeamId,
            categoryId,
            agentId,
            TicketTestData.User(agentId, UserRole.Agent, teamId),
            manager,
            service);
    }
}
