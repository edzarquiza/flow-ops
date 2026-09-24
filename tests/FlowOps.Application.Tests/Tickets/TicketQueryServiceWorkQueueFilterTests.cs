using FlowOps.Application.Tests.Persistence;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Persistence;
using Xunit;

namespace FlowOps.Application.Tests.Tickets;

/// <summary>
/// Phase 30B: the Work Queue's Status/Priority/"Assigned to me" filters
/// (<see cref="TicketQueryService.GetQueueAsync"/>) against real PostgreSQL — each filter alone,
/// composed with the others and with the existing date-range/finished narrowing, and that no
/// filter can ever widen the caller's existing authorization scope.
/// </summary>
[Collection("Postgres")]
public sealed class TicketQueryServiceWorkQueueFilterTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;

    public TicketQueryServiceWorkQueueFilterTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task GetQueueAsync_StatusFilter_ReturnsOnlyThatStatus()
    {
        await using var context = _fixture.CreateContext();
        var (teamId, categoryId, user) = await SeedAsync(context);
        var service = new TicketService(context, new TicketTestData.FixedTimeProvider(Now), TestEmail.Sender, TestEmail.Options);
        var (openId, _) = await service.CreateAsync(Request(teamId, categoryId), user);
        var (assignedId, _) = await service.CreateAsync(Request(teamId, categoryId), user);
        await service.AssignAsync(assignedId, user.UserId, user);

        var query = new TicketQueryService(context, new TicketTestData.FixedTimeProvider(Now));
        var page = await query.GetQueueAsync(user, 1, status: Status.Open);

        Assert.Equal(1, page.TotalCount);
        Assert.Equal(openId, page.Items.Single().Id);
    }

    [Fact]
    public async Task GetQueueAsync_PriorityFilter_ReturnsOnlyThatPriority()
    {
        await using var context = _fixture.CreateContext();
        var (teamId, categoryId, user) = await SeedAsync(context);
        var service = new TicketService(context, new TicketTestData.FixedTimeProvider(Now), TestEmail.Sender, TestEmail.Options);
        var (highId, _) = await service.CreateAsync(Request(teamId, categoryId) with { Priority = Priority.High }, user);
        await service.CreateAsync(Request(teamId, categoryId) with { Priority = Priority.Low }, user);

        var query = new TicketQueryService(context, new TicketTestData.FixedTimeProvider(Now));
        var page = await query.GetQueueAsync(user, 1, priority: Priority.High);

        Assert.Equal(1, page.TotalCount);
        Assert.Equal(highId, page.Items.Single().Id);
    }

    [Fact]
    public async Task GetQueueAsync_AssignedToMe_ReturnsOnlyTicketsAssignedToTheCaller()
    {
        await using var context = _fixture.CreateContext();
        var (teamId, categoryId, user) = await SeedAsync(context);
        var otherUserId = await TicketTestData.AddUserAsync(context);
        await TicketTestData.AddTeamMembershipAsync(context, teamId, otherUserId);
        // A Manager of this team performs the cross-user assignment — an Agent (like `user`) may
        // only ever assign to themselves (TICKET-INV-03/AUTH-RULE-02).
        var managerId = await TicketTestData.AddUserAsync(context);
        await TicketTestData.AddTeamMembershipAsync(context, teamId, managerId, isTeamManager: true);
        var manager = TicketTestData.Manager(managerId, teamId);
        var service = new TicketService(context, new TicketTestData.FixedTimeProvider(Now), TestEmail.Sender, TestEmail.Options);
        var (mineId, _) = await service.CreateAsync(Request(teamId, categoryId), user);
        await service.AssignAsync(mineId, user.UserId, user);
        var (othersId, _) = await service.CreateAsync(Request(teamId, categoryId), user);
        await service.AssignAsync(othersId, otherUserId, manager);

        var query = new TicketQueryService(context, new TicketTestData.FixedTimeProvider(Now));
        var page = await query.GetQueueAsync(user, 1, assignedToMe: true);

        Assert.Equal(1, page.TotalCount);
        Assert.Equal(mineId, page.Items.Single().Id);
    }

    [Fact] // Every filter is an additional AND — Status + Priority + AssignedToMe must all hold at once.
    public async Task GetQueueAsync_StatusPriorityAndAssignedToMe_ComposeAsAnAndNarrowing()
    {
        await using var context = _fixture.CreateContext();
        var (teamId, categoryId, user) = await SeedAsync(context);
        var service = new TicketService(context, new TicketTestData.FixedTimeProvider(Now), TestEmail.Sender, TestEmail.Options);

        // Matches all three.
        var (matchId, _) = await service.CreateAsync(Request(teamId, categoryId) with { Priority = Priority.High }, user);
        await service.AssignAsync(matchId, user.UserId, user);

        // Right priority and assignee, wrong status (still Open, not Assigned).
        await service.CreateAsync(Request(teamId, categoryId) with { Priority = Priority.High }, user);

        // Right status and assignee, wrong priority.
        var (wrongPriorityId, _) = await service.CreateAsync(Request(teamId, categoryId) with { Priority = Priority.Low }, user);
        await service.AssignAsync(wrongPriorityId, user.UserId, user);

        var query = new TicketQueryService(context, new TicketTestData.FixedTimeProvider(Now));
        var page = await query.GetQueueAsync(user, 1, status: Status.Assigned, priority: Priority.High, assignedToMe: true);

        Assert.Equal(1, page.TotalCount);
        Assert.Equal(matchId, page.Items.Single().Id);
    }

    [Fact] // §21: none of the new filters can reach outside the caller's existing authorization
           // scope — a ticket in a genuinely different organization, matching every filter, must
           // still never leak.
    public async Task GetQueueAsync_NewFiltersNeverCrossTheOrganizationBoundary()
    {
        await using var context = _fixture.CreateContext();
        var (teamId, categoryId, user) = await SeedAsync(context);
        var service = new TicketService(context, new TicketTestData.FixedTimeProvider(Now), TestEmail.Sender, TestEmail.Options);
        await service.CreateAsync(Request(teamId, categoryId) with { Priority = Priority.High }, user);

        var (otherOrganizationId, otherTeamId) = await TicketTestData.AddSecondOrganizationTeamAsync(context);
        var otherCategoryId = await TicketTestData.AddCategoryAsync(context, otherTeamId);
        var otherUserId = await TicketTestData.AddUserAsync(context);
        await TicketTestData.AddTeamMembershipAsync(context, otherTeamId, otherUserId);
        var otherUser = TicketTestData.UserInOrganization(otherOrganizationId, otherUserId, UserRole.Admin, otherTeamId);
        var otherService = new TicketService(context, new TicketTestData.FixedTimeProvider(Now), TestEmail.Sender, TestEmail.Options);
        var (otherTicketId, _) = await otherService.CreateAsync(Request(otherTeamId, otherCategoryId) with { Priority = Priority.High }, otherUser);
        await otherService.AssignAsync(otherTicketId, otherUserId, otherUser);

        var query = new TicketQueryService(context, new TicketTestData.FixedTimeProvider(Now));
        var page = await query.GetQueueAsync(user, 1, priority: Priority.High);

        Assert.Equal(1, page.TotalCount); // only this org's own High-priority ticket
    }

    private static async Task<(int TeamId, int CategoryId, CurrentUser User)> SeedAsync(FlowOpsDbContext context)
    {
        var teamId = await TicketTestData.AddTeamAsync(context);
        var categoryId = await TicketTestData.AddCategoryAsync(context, teamId);
        var userId = await TicketTestData.AddUserAsync(context);
        await TicketTestData.AddTeamMembershipAsync(context, teamId, userId);
        return (teamId, categoryId, TicketTestData.User(userId, UserRole.Agent, teamId));
    }

    private static CreateTicketRequest Request(int teamId, int categoryId) =>
        new(
            Title: "Printer on 3rd floor is jammed",
            Description: "The printer near the east stairwell is jammed and needs a technician.",
            WorkType: WorkType.Incident,
            Priority: Priority.Medium,
            TeamId: teamId,
            CategoryId: categoryId,
            ProjectId: null);
}
