using FlowOps.Application.Tests.Persistence;
using FlowOps.Application.Tickets;
using FlowOps.Domain;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Persistence;
using Xunit;

namespace FlowOps.Application.Tests.Tickets;

/// <summary>
/// Phase 29B: the Work Queue opens on unfinished work. <c>includeFinished: false</c> only ever
/// removes Resolved/Closed rows from what the caller's authorization scope already allows — it never
/// widens visibility — and the default (<c>true</c>) leaves every existing caller unchanged.
/// </summary>
[Collection("Postgres")]
public sealed class WorkQueueActiveWorkTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;

    public WorkQueueActiveWorkTests(PostgresFixture fixture) => _fixture = fixture;

    private sealed record World(
        int TeamId, int CategoryId, CurrentUser Admin, CurrentUser Manager, CurrentUser Agent, CurrentUser OutsideAgent,
        int Open, int InProgress, int Pending, int Resolved, int Closed);

    private static TicketService Tickets(FlowOpsDbContext c) => new(c, new TicketTestData.FixedTimeProvider(Now), TestEmail.Sender, TestEmail.Options);
    private static TicketQueryService Query(FlowOpsDbContext c) => new(c, new TicketTestData.FixedTimeProvider(Now));

    private static async Task<World> SeedAsync(FlowOpsDbContext c)
    {
        var teamId = await TicketTestData.AddTeamAsync(c);
        var categoryId = await TicketTestData.AddCategoryAsync(c, teamId);
        var otherTeam = await TicketTestData.AddTeamAsync(c);
        var adminId = await TicketTestData.AddUserAsync(c);
        var managerId = await TicketTestData.AddUserAsync(c);
        var agentId = await TicketTestData.AddUserAsync(c);
        var outsiderId = await TicketTestData.AddUserAsync(c);
        await TicketTestData.AddTeamMembershipAsync(c, teamId, managerId, isTeamManager: true);
        await TicketTestData.AddTeamMembershipAsync(c, teamId, agentId);
        await TicketTestData.AddTeamMembershipAsync(c, otherTeam, outsiderId);

        var manager = TicketTestData.Manager(managerId, teamId);
        var agent = TicketTestData.User(agentId, UserRole.Agent, teamId);
        var tickets = Tickets(c);

        async Task<int> NewAsync(string title) =>
            (await tickets.CreateAsync(new CreateTicketRequest(title, "A ticket for work queue tests.", WorkType.Incident, Priority.Medium, teamId, categoryId, null), manager)).Id;

        var open = await NewAsync("Still open");

        var inProgress = await NewAsync("Being worked");
        await tickets.AssignAsync(inProgress, agent.UserId, manager);
        await tickets.StartWorkAsync(inProgress, agent);

        var pending = await NewAsync("Waiting on someone");
        await tickets.AssignAsync(pending, agent.UserId, manager);
        await tickets.StartWorkAsync(pending, agent);
        await tickets.PutOnHoldAsync(pending, "Waiting for a reply.", agent);

        var resolved = await NewAsync("Fixed already");
        await tickets.AssignAsync(resolved, agent.UserId, manager);
        await tickets.StartWorkAsync(resolved, agent);
        await tickets.ResolveAsync(resolved, Resolution.Fixed, "Applied the standard fix.", agent);

        var closed = await NewAsync("Done and closed");
        await tickets.AssignAsync(closed, agent.UserId, manager);
        await tickets.StartWorkAsync(closed, agent);
        await tickets.ResolveAsync(closed, Resolution.Fixed, "Applied the standard fix.", agent);
        await tickets.CloseAsync(closed, manager);

        return new World(teamId, categoryId, TicketTestData.User(adminId, UserRole.Admin), manager, agent,
            TicketTestData.User(outsiderId, UserRole.Agent, otherTeam), open, inProgress, pending, resolved, closed);
    }

    private static HashSet<int> Ids(PagedResult<TicketListItem> page, World w) =>
        page.Items.Select(i => i.Id).Intersect([w.Open, w.InProgress, w.Pending, w.Resolved, w.Closed]).ToHashSet();

    /// <summary>Every page of the caller's queue. An Admin's scope is the whole shared test database, so
    /// this world's tickets are not guaranteed to be on page 1 as other test classes add tickets.</summary>
    private static async Task<HashSet<int>> AllIdsAsync(FlowOpsDbContext c, CurrentUser user, World w, bool includeFinished)
    {
        var found = new HashSet<int>();
        for (var pageNumber = 1; ; pageNumber++)
        {
            var page = await Query(c).GetQueueAsync(user, pageNumber, includeFinished: includeFinished);
            if (page.Items.Count == 0)
            {
                return found;
            }

            found.UnionWith(Ids(page, w));
        }
    }

    [Fact]
    public async Task ActiveOnly_ShowsOpenInProgressAndPending_ForAgentManagerAndAdmin()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);

        foreach (var user in new[] { w.Agent, w.Manager, w.Admin })
        {
            Assert.Equal(new HashSet<int> { w.Open, w.InProgress, w.Pending }, await AllIdsAsync(c, user, w, includeFinished: false));
        }
    }

    [Fact]
    public async Task IncludingFinished_KeepsResolvedAndClosedTicketsDiscoverable()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);

        var active = await Query(c).GetQueueAsync(w.Agent, 1, includeFinished: false);
        var all = await Query(c).GetQueueAsync(w.Agent, 1, includeFinished: true);
        var defaultCall = await Query(c).GetQueueAsync(w.Agent, 1); // every existing caller

        Assert.Equal(new HashSet<int> { w.Open, w.InProgress, w.Pending, w.Resolved, w.Closed }, Ids(all, w));
        Assert.Equal(Ids(all, w), Ids(defaultCall, w));
        Assert.True(all.TotalCount > active.TotalCount);
    }

    [Fact] // The narrowing happens after authorization: it can only remove rows.
    public async Task AuthorizationIsUnchanged_ATeamOutsiderNeverSeesTheseTickets_EitherWay()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);

        Assert.Empty(Ids(await Query(c).GetQueueAsync(w.OutsideAgent, 1, includeFinished: false), w));
        Assert.Empty(Ids(await Query(c).GetQueueAsync(w.OutsideAgent, 1, includeFinished: true), w));
    }

    [Fact] // The total (page header count and pager) reflects the narrowed list.
    public async Task ActiveOnly_TotalCountAndPagingCountOnlyActiveWork()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);

        var active = await Query(c).GetQueueAsync(w.Agent, 1, includeFinished: false);

        Assert.All(active.Items, i => Assert.True(i.Status is not (Status.Resolved or Status.Closed)));
        Assert.Equal(active.Items.Count, active.TotalCount);
    }
}
