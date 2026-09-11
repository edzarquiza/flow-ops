using FlowOps.Application.Tests.Persistence;
using FlowOps.Application.Tickets;
using FlowOps.Domain;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FlowOps.Application.Tests.Tickets;

/// <summary>
/// Phase 5 ticket creation against real PostgreSQL: proves the aggregate, the sequence-backed
/// reference default, the audit event, and the authorization gate all work together through one
/// <c>SaveChangesAsync</c>.
/// </summary>
[Collection("Postgres")]
public sealed class TicketServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;

    public TicketServiceTests(PostgresFixture fixture) => _fixture = fixture;

    [Theory] // AUTH-RULE-02 "Create ticket": Admin, Manager and Agent may create.
    [InlineData(UserRole.Admin)]
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.Agent)]
    public async Task CreateAsync_AllowedRole_PersistsTicket(UserRole role)
    {
        await using var context = _fixture.CreateContext();
        var (teamId, categoryId, userId) = await SeedAsync(context);
        var service = new TicketService(context, new TicketTestData.FixedTimeProvider(Now));

        var (id, reference) = await service.CreateAsync(
            Request(teamId, categoryId),
            TicketTestData.User(userId, role, teamId));

        Assert.True(id > 0);
        Assert.NotNull(reference);

        await using var verify = _fixture.CreateContext();
        var persisted = await verify.Tickets.AsNoTracking().SingleAsync(t => t.Id == id);

        Assert.Equal("Printer on 3rd floor is jammed", persisted.Title);
        Assert.Equal(Status.Open, persisted.Status);
        Assert.Equal(teamId, persisted.TeamId);
        Assert.Equal(categoryId, persisted.CategoryId);
        Assert.Equal(Now, persisted.CreatedAt);
    }

    [Fact] // AUTH-RULE-02: Viewer may not create.
    public async Task CreateAsync_Viewer_IsDeniedAndPersistsNothing()
    {
        await using var context = _fixture.CreateContext();
        var (teamId, categoryId, userId) = await SeedAsync(context);
        var service = new TicketService(context, new TicketTestData.FixedTimeProvider(Now));

        await Assert.ThrowsAsync<TicketAccessDeniedException>(() =>
            service.CreateAsync(Request(teamId, categoryId), TicketTestData.User(userId, UserRole.Viewer, teamId)));

        await using var verify = _fixture.CreateContext();
        Assert.Equal(0, await verify.Tickets.CountAsync(t => t.TeamId == teamId));
    }

    [Fact] // The requester is the authenticated caller, never a client-supplied value.
    public async Task CreateAsync_RequesterIsTheAuthenticatedCaller_NotRequestInput()
    {
        await using var context = _fixture.CreateContext();
        var (teamId, categoryId, userId) = await SeedAsync(context);
        var service = new TicketService(context, new TicketTestData.FixedTimeProvider(Now));

        var (id, _) = await service.CreateAsync(
            Request(teamId, categoryId),
            TicketTestData.User(userId, UserRole.Agent, teamId));

        await using var verify = _fixture.CreateContext();
        var persisted = await verify.Tickets.AsNoTracking().SingleAsync(t => t.Id == id);

        Assert.Equal(userId, persisted.RequesterId);
        Assert.Null(persisted.AssigneeId); // creation never assigns — that is Phase 6
    }

    [Fact] // TICKET-INV-02: the category must belong to the ticket's team.
    public async Task CreateAsync_CategoryFromAnotherTeam_IsRejected()
    {
        await using var context = _fixture.CreateContext();
        var (teamId, _, userId) = await SeedAsync(context);
        var otherTeamId = await TicketTestData.AddTeamAsync(context);
        var otherCategoryId = await TicketTestData.AddCategoryAsync(context, otherTeamId);
        var service = new TicketService(context, new TicketTestData.FixedTimeProvider(Now));

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            service.CreateAsync(Request(teamId, otherCategoryId), TicketTestData.User(userId, UserRole.Agent, teamId)));

        Assert.Equal("TICKET-INV-02", ex.RuleCode);
    }

    [Fact]
    public async Task CreateAsync_UnknownCategory_IsRejected()
    {
        await using var context = _fixture.CreateContext();
        var (teamId, _, userId) = await SeedAsync(context);
        var service = new TicketService(context, new TicketTestData.FixedTimeProvider(Now));

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            service.CreateAsync(Request(teamId, categoryId: -1), TicketTestData.User(userId, UserRole.Agent, teamId)));

        Assert.Equal("TICKET-INV-02", ex.RuleCode);
    }

    [Theory] // SLA-RULE-01/02: the seeded (null, Priority) default rows resolve the target.
    [InlineData(Priority.Critical, 240)]
    [InlineData(Priority.High, 480)]
    [InlineData(Priority.Medium, 1440)]
    [InlineData(Priority.Low, 4320)]
    public async Task CreateAsync_ResolvesSeededSlaTargetForPriority(Priority priority, int expectedMinutes)
    {
        await using var context = _fixture.CreateContext();
        var (teamId, categoryId, userId) = await SeedAsync(context);
        var service = new TicketService(context, new TicketTestData.FixedTimeProvider(Now));

        var (id, _) = await service.CreateAsync(
            Request(teamId, categoryId) with { Priority = priority },
            TicketTestData.User(userId, UserRole.Agent, teamId));

        await using var verify = _fixture.CreateContext();
        var persisted = await verify.Tickets.AsNoTracking().SingleAsync(t => t.Id == id);

        Assert.Equal(expectedMinutes, persisted.SlaTargetMinutes);
        Assert.Equal(Now, persisted.SlaStartedAt);
        Assert.Equal(Now.AddMinutes(expectedMinutes), persisted.SlaDueAt);
    }

    [Fact] // docs/architecture.md §5: PostgreSQL's sequence generates the reference, not the app.
    public async Task CreateAsync_ReferenceIsGeneratedByPostgres()
    {
        await using var context = _fixture.CreateContext();
        var (teamId, categoryId, userId) = await SeedAsync(context);
        var service = new TicketService(context, new TicketTestData.FixedTimeProvider(Now));
        var user = TicketTestData.User(userId, UserRole.Agent, teamId);

        var (_, firstReference) = await service.CreateAsync(Request(teamId, categoryId), user);
        var (_, secondReference) = await service.CreateAsync(Request(teamId, categoryId), user);

        Assert.Matches(@"^FO-\d{6}$", firstReference);
        Assert.Matches(@"^FO-\d{6}$", secondReference);
        Assert.NotEqual(firstReference, secondReference);
    }

    [Fact] // TICKET-INV-09 / AUDIT-RULE-04: exactly one Created event, same transaction.
    public async Task CreateAsync_WritesExactlyOneCreatedEvent()
    {
        await using var context = _fixture.CreateContext();
        var (teamId, categoryId, userId) = await SeedAsync(context);
        var service = new TicketService(context, new TicketTestData.FixedTimeProvider(Now));

        var (id, _) = await service.CreateAsync(
            Request(teamId, categoryId),
            TicketTestData.User(userId, UserRole.Agent, teamId));

        await using var verify = _fixture.CreateContext();
        var events = await verify.TicketEvents.AsNoTracking().Where(e => e.TicketId == id).ToListAsync();

        var only = Assert.Single(events);
        Assert.Equal(TicketEventType.Created, only.EventType);
        Assert.Equal(userId, only.ActorUserId);
        Assert.Equal(Now, only.OccurredAt);
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

    private static async Task<(int TeamId, int CategoryId, Guid UserId)> SeedAsync(FlowOpsDbContext context)
    {
        var teamId = await TicketTestData.AddTeamAsync(context);
        var categoryId = await TicketTestData.AddCategoryAsync(context, teamId);
        var userId = await TicketTestData.AddUserAsync(context);
        await TicketTestData.AddTeamMembershipAsync(context, teamId, userId);
        return (teamId, categoryId, userId);
    }
}
