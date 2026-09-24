using FlowOps.Application.Tests.Email;
using FlowOps.Application.Tests.Persistence;
using FlowOps.Application.Tickets;
using FlowOps.Domain;
using FlowOps.Domain.Attention;
using FlowOps.Domain.Sla;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FlowOps.Application.Tests.Tickets;

/// <summary>
/// ADR-0036: Team Workload's authorization (reusing <see cref="TicketAccessPolicy.GetAnalyticsScope"/>
/// / <see cref="TicketAccessPolicy.CanViewTeamWorkload"/> exactly as approved — never a broader
/// scope), tenant isolation, and at-risk grouping correctness (grouped from
/// <see cref="AttentionPolicy.Evaluate"/>'s own output, never a second decision).
/// </summary>
[Collection("Postgres")]
public sealed class TeamWorkloadQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;

    public TeamWorkloadQueryTests(PostgresFixture fixture) => _fixture = fixture;

    private sealed record World(
        int TeamId,
        int OtherTeamId,
        int CategoryId,
        int OtherCategoryId,
        Guid AgentId,
        Guid OtherAgentId,
        CurrentUser Admin,
        CurrentUser Manager,
        CurrentUser Agent,
        CurrentUser Viewer);

    private static AnalyticsQueryService Analytics(FlowOpsDbContext c) => new(c, new TicketTestData.FixedTimeProvider(Now));
    private static AttentionQueryService Attention(FlowOpsDbContext c) =>
        new(c, new TicketTestData.FixedTimeProvider(Now), new AttentionOptions(), new TicketQueryService(c, new TicketTestData.FixedTimeProvider(Now)));
    private static TicketService Tickets(FlowOpsDbContext c) => new(c, new TicketTestData.FixedTimeProvider(Now), TestEmail.Sender, TestEmail.Options);

    private static async Task<World> SeedAsync(FlowOpsDbContext c)
    {
        var teamId = await TicketTestData.AddTeamAsync(c);
        var otherTeamId = await TicketTestData.AddTeamAsync(c);
        var categoryId = await TicketTestData.AddCategoryAsync(c, teamId);
        var otherCategoryId = await TicketTestData.AddCategoryAsync(c, otherTeamId);

        var adminId = await TicketTestData.AddUserAsync(c);
        var managerId = await TicketTestData.AddUserAsync(c);
        var agentId = await TicketTestData.AddUserAsync(c);
        var otherAgentId = await TicketTestData.AddUserAsync(c);
        var viewerId = await TicketTestData.AddUserAsync(c);

        await TicketTestData.AddTeamMembershipAsync(c, teamId, managerId, isTeamManager: true);
        await TicketTestData.AddTeamMembershipAsync(c, teamId, agentId);
        await TicketTestData.AddTeamMembershipAsync(c, teamId, viewerId);
        await TicketTestData.AddTeamMembershipAsync(c, otherTeamId, otherAgentId);

        return new World(
            teamId, otherTeamId, categoryId, otherCategoryId,
            agentId, otherAgentId,
            TicketTestData.User(adminId, UserRole.Admin),
            TicketTestData.Manager(managerId, teamId),
            TicketTestData.User(agentId, UserRole.Agent, teamId),
            TicketTestData.User(viewerId, UserRole.Viewer, teamId));
    }

    private static Task<(int Id, string Reference)> CreateTicketAsync(
        FlowOpsDbContext c, World w, int teamId, int categoryId, Priority priority, CurrentUser requester) =>
        Tickets(c).CreateAsync(
            new CreateTicketRequest("A ticket for team workload tests.", "A routine description.", WorkType.Incident, priority, teamId, categoryId, null),
            requester);

    // ---------------------------------------------------------------------------------------
    // Authorization: GetTeamWorkloadTableAsync / GetTeamWorkloadSummaryCountsAsync scope
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task TeamWorkloadTable_Admin_SeesEveryTeam()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);
        await CreateTicketAsync(c, w, w.TeamId, w.CategoryId, Priority.Medium, w.Manager);
        await CreateTicketAsync(c, w, w.OtherTeamId, w.OtherCategoryId, Priority.Medium, w.Admin);

        var rows = await Analytics(c).GetTeamWorkloadTableAsync(w.Admin, DashboardFilter.Default);

        Assert.Contains(rows, r => r.TeamId == w.TeamId);
        Assert.Contains(rows, r => r.TeamId == w.OtherTeamId);
    }

    [Fact] // AUTH-RULE-02 "Team analytics": Manager sees managed teams only — never every team they merely see tickets in.
    public async Task TeamWorkloadTable_Manager_SeesOnlyManagedTeam()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);
        await CreateTicketAsync(c, w, w.TeamId, w.CategoryId, Priority.Medium, w.Manager);
        await CreateTicketAsync(c, w, w.OtherTeamId, w.OtherCategoryId, Priority.Medium, w.Admin);

        var rows = await Analytics(c).GetTeamWorkloadTableAsync(w.Manager, DashboardFilter.Default);

        Assert.Contains(rows, r => r.TeamId == w.TeamId);
        Assert.DoesNotContain(rows, r => r.TeamId == w.OtherTeamId);
    }

    [Fact] // Agent's analytics scope is "own assigned tickets only" — summary counts reflect just that, never a team-wide count.
    public async Task TeamWorkloadSummary_Agent_ReflectsOwnAssignedWorkOnly()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);
        var (ticketId, _) = await CreateTicketAsync(c, w, w.TeamId, w.CategoryId, Priority.Medium, w.Manager);
        await Tickets(c).AssignAsync(ticketId, w.AgentId, w.Manager);
        // A second, unrelated open ticket on the same team, assigned to nobody — must not count for this Agent.
        await CreateTicketAsync(c, w, w.TeamId, w.CategoryId, Priority.Medium, w.Manager);

        var summary = await Analytics(c).GetTeamWorkloadSummaryCountsAsync(w.Agent, DashboardFilter.Default);

        Assert.Equal(1, summary.OpenCount);
    }

    [Fact]
    public async Task TeamWorkloadTable_Viewer_SeesOnlyMemberTeam()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);
        await CreateTicketAsync(c, w, w.TeamId, w.CategoryId, Priority.Medium, w.Manager);
        await CreateTicketAsync(c, w, w.OtherTeamId, w.OtherCategoryId, Priority.Medium, w.Admin);

        var rows = await Analytics(c).GetTeamWorkloadTableAsync(w.Viewer, DashboardFilter.Default);

        Assert.Contains(rows, r => r.TeamId == w.TeamId);
        Assert.DoesNotContain(rows, r => r.TeamId == w.OtherTeamId);
    }

    // ---------------------------------------------------------------------------------------
    // Authorization: GetTeamMemberWorkloadAsync / GetAtRiskCountsByAssigneeAsync (ADR-0036)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task TeamMemberWorkload_Manager_CanViewManagedTeam()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);

        var members = await Analytics(c).GetTeamMemberWorkloadAsync(w.Manager, w.TeamId);

        Assert.Contains(members, m => m.UserId == w.AgentId);
    }

    [Fact] // The exact scope decision ADR-0036 exists for: Manager cannot reach a team they do not manage.
    public async Task TeamMemberWorkload_Manager_CrossTeamIsDenied()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);

        await Assert.ThrowsAsync<TicketAccessDeniedException>(() =>
            Analytics(c).GetTeamMemberWorkloadAsync(w.Manager, w.OtherTeamId));
    }

    [Fact] // Agent has no team-level workload authority at all, for any team.
    public async Task TeamMemberWorkload_Agent_IsAlwaysDenied()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);

        await Assert.ThrowsAsync<TicketAccessDeniedException>(() =>
            Analytics(c).GetTeamMemberWorkloadAsync(w.Agent, w.TeamId));
    }

    [Fact] // Read-only workload visibility must never grant team-management authority — confirmed
           // by construction: GetTeamMemberWorkloadAsync exposes no mutation of any kind, and a
           // Manager reaching it is not thereby able to add/remove members (that remains
           // TeamService/DirectoryAccessPolicy's own, separate, Admin-only gate).
    public async Task TeamMemberWorkload_DoesNotGrantTeamManagementAuthority()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);

        // The Manager can read the team's member workload...
        var members = await Analytics(c).GetTeamMemberWorkloadAsync(w.Manager, w.TeamId);
        Assert.NotEmpty(members);

        // ...but TeamService's own management gate is untouched: a Manager still cannot manage teams.
        Assert.False(FlowOps.Domain.Directory.DirectoryAccessPolicy.CanManageTeams(w.Manager));
    }

    [Fact]
    public async Task AtRiskCountsByAssignee_Viewer_CanViewOwnMemberTeam()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);

        var counts = await Attention(c).GetAtRiskCountsByAssigneeAsync(w.Viewer, w.TeamId);

        Assert.NotNull(counts);
    }

    [Fact]
    public async Task AtRiskCountsByAssignee_Viewer_CrossTeamIsDenied()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);

        await Assert.ThrowsAsync<TicketAccessDeniedException>(() =>
            Attention(c).GetAtRiskCountsByAssigneeAsync(w.Viewer, w.OtherTeamId));
    }

    // ---------------------------------------------------------------------------------------
    // Tenant isolation
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task TeamWorkloadTable_NeverCrossesOrganizationBoundary()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);
        await CreateTicketAsync(c, w, w.TeamId, w.CategoryId, Priority.Medium, w.Manager);

        var (otherOrgId, otherOrgTeamId) = await TicketTestData.AddSecondOrganizationTeamAsync(c);
        var otherOrgCategoryId = await TicketTestData.AddCategoryAsync(c, otherOrgTeamId);
        var otherOrgUserId = await TicketTestData.AddUserAsync(c);
        var otherOrgAdmin = TicketTestData.UserInOrganization(otherOrgId, otherOrgUserId, UserRole.Admin);
        await Tickets(c).CreateAsync(
            new CreateTicketRequest("Another organization's own ticket.", "Never visible across the boundary.", WorkType.Incident, Priority.Medium, otherOrgTeamId, otherOrgCategoryId, null),
            otherOrgAdmin);

        // Org A's Admin (unscoped within their own org) never sees Org B's team, even though
        // Admin's AnalyticsScope is otherwise "all teams."
        var rows = await Analytics(c).GetTeamWorkloadTableAsync(w.Admin, DashboardFilter.Default);
        Assert.DoesNotContain(rows, r => r.TeamId == otherOrgTeamId);
    }

    // ---------------------------------------------------------------------------------------
    // At-risk grouping correctness (AttentionPolicy remains the sole authority)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AtRiskSummary_MatchesAttentionPolicyEvaluatedIndependently()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);

        // A ticket old enough to breach SLA — a genuine, policy-evaluated at-risk signal, not a flag.
        var clock = new TicketTestData.FixedTimeProvider(Now.AddDays(-10));
        var backdatedTickets = new TicketService(c, clock, TestEmail.Sender, TestEmail.Options);
        var (breachedId, _) = await backdatedTickets.CreateAsync(
            new CreateTicketRequest("Old enough to breach its SLA.", "A routine description.", WorkType.Incident, Priority.Medium, w.TeamId, w.CategoryId, null),
            w.Manager);

        // A freshly created, healthy ticket — must never count as at risk.
        await CreateTicketAsync(c, w, w.TeamId, w.CategoryId, Priority.Medium, w.Manager);

        var (total, byTeam) = await Attention(c).GetAtRiskSummaryAsync(w.Admin);

        // Independently re-derive the expected answer straight from AttentionPolicy itself, the
        // same way AttentionQueryServiceTests' own superset test verifies the prefilter.
        var configurations = await c.SlaConfigurations.AsNoTracking().ToListAsync();
        var allTeamTickets = await c.Tickets.AsNoTracking().Include(t => t.Events).Where(t => t.TeamId == w.TeamId).ToListAsync();
        var expectedAtRisk = allTeamTickets.Count(t => AttentionPolicy.Evaluate(
            t, Now, new AttentionOptions(),
            SlaPolicy.ResolveConfiguration(configurations, t.WorkType, t.Priority).RiskThresholdPercent).Count > 0);

        Assert.Equal(expectedAtRisk, byTeam.GetValueOrDefault(w.TeamId));
        Assert.True(total >= expectedAtRisk);
        Assert.Contains(byTeam, kv => kv.Key == w.TeamId && kv.Value >= 1);
    }

    [Fact] // Manager's at-risk breakdown is scoped to managed teams — never every team they can merely view.
    public async Task AtRiskSummary_Manager_NeverIncludesUnmanagedTeam()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);

        var clock = new TicketTestData.FixedTimeProvider(Now.AddDays(-10));
        var backdatedTickets = new TicketService(c, clock, TestEmail.Sender, TestEmail.Options);
        await backdatedTickets.CreateAsync(
            new CreateTicketRequest("Breached in the other team.", "A routine description.", WorkType.Incident, Priority.Medium, w.OtherTeamId, w.OtherCategoryId, null),
            w.Admin);

        var (_, byTeam) = await Attention(c).GetAtRiskSummaryAsync(w.Manager);

        Assert.DoesNotContain(byTeam, kv => kv.Key == w.OtherTeamId);
    }

    // ---------------------------------------------------------------------------------------
    // Drill-through correctness (Work Queue's new teamId/assigneeId parameters)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task WorkQueue_TeamIdFilter_NarrowsToThatTeamOnly()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);
        var (inTeamId, _) = await CreateTicketAsync(c, w, w.TeamId, w.CategoryId, Priority.Medium, w.Manager);
        await CreateTicketAsync(c, w, w.OtherTeamId, w.OtherCategoryId, Priority.Medium, w.Admin);

        var page = await new TicketQueryService(c, new TicketTestData.FixedTimeProvider(Now))
            .GetQueueAsync(w.Admin, 1, teamId: w.TeamId);

        var expectedTeamName = await c.Teams.AsNoTracking().Where(t => t.Id == w.TeamId).Select(t => t.Name).SingleAsync();
        Assert.Contains(page.Items, i => i.Id == inTeamId);
        Assert.All(page.Items, i => Assert.Equal(expectedTeamName, i.TeamName));
    }

    [Fact] // A teamId outside the caller's own scope yields zero rows — never a leak.
    public async Task WorkQueue_TeamIdFilter_NeverWidensTheCallersOwnScope()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);
        await CreateTicketAsync(c, w, w.OtherTeamId, w.OtherCategoryId, Priority.Medium, w.Admin);

        // Manager is not a member of OtherTeam at all — CanView's own team-membership scope excludes it.
        var page = await new TicketQueryService(c, new TicketTestData.FixedTimeProvider(Now))
            .GetQueueAsync(w.Manager, 1, teamId: w.OtherTeamId);

        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task WorkQueue_AssigneeIdFilter_NarrowsToThatAssigneeOnly()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);
        var (assignedId, _) = await CreateTicketAsync(c, w, w.TeamId, w.CategoryId, Priority.Medium, w.Manager);
        await Tickets(c).AssignAsync(assignedId, w.AgentId, w.Manager);
        await CreateTicketAsync(c, w, w.TeamId, w.CategoryId, Priority.Medium, w.Manager);

        var page = await new TicketQueryService(c, new TicketTestData.FixedTimeProvider(Now))
            .GetQueueAsync(w.Admin, 1, assigneeId: w.AgentId);

        var item = Assert.Single(page.Items);
        Assert.Equal(assignedId, item.Id);
    }

    [Fact] // The new Unassigned filter, activated for the first time via Team Workload's own drill-through.
    public async Task WorkQueue_UnassignedFilter_ExcludesAssignedTickets()
    {
        await using var c = _fixture.CreateContext();
        var w = await SeedAsync(c);
        var (unassignedId, _) = await CreateTicketAsync(c, w, w.TeamId, w.CategoryId, Priority.Medium, w.Manager);
        var (assignedId, _) = await CreateTicketAsync(c, w, w.TeamId, w.CategoryId, Priority.Medium, w.Manager);
        await Tickets(c).AssignAsync(assignedId, w.AgentId, w.Manager);

        var page = await new TicketQueryService(c, new TicketTestData.FixedTimeProvider(Now))
            .GetQueueAsync(w.Admin, 1, TicketQueueFilter.Unassigned);

        Assert.Contains(page.Items, i => i.Id == unassignedId);
        Assert.DoesNotContain(page.Items, i => i.Id == assignedId);
    }
}
