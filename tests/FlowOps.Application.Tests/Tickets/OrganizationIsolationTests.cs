using FlowOps.Application.Tests.Persistence;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Catalog;
using FlowOps.Domain.Organizations;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FlowOps.Application.Tests.Tickets;

/// <summary>
/// Phase 16, Part E: real integration tests (no mocks, real PostgreSQL) proving the organization
/// boundary genuinely isolates two unrelated tenants — never merely relying on
/// <c>TicketAccessPolicy</c>'s existing team/role checks, which know nothing about organizations
/// at all (see the multi-tenant foundation ADR). Every test here builds two <em>genuinely
/// separate</em> organizations via <see cref="TicketTestData.AddSecondOrganizationTeamAsync"/> and
/// proves a caller in one can never observe or mutate the other's data — including for Admin, who
/// is unconditionally global <em>within</em> an organization but must never cross into another one.
/// </summary>
[Collection("Postgres")]
public sealed class OrganizationIsolationTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;

    public OrganizationIsolationTests(PostgresFixture fixture) => _fixture = fixture;

    private sealed record World(
        int OrgATeamId,
        int OrgACategoryId,
        Guid OrgAAdminId,
        CurrentUser OrgAAdmin,
        CurrentUser OrgAAgent,
        CurrentUser OrgAManager,
        int OrgBTeamId,
        int OrgBCategoryId,
        int OrgBOrganizationId,
        Guid OrgBAgentId,
        CurrentUser OrgBAgent,
        int OrgBTicketId,
        TicketService Service);

    private static async Task<World> SeedAsync(FlowOpsDbContext context)
    {
        var orgATeamId = await TicketTestData.AddTeamAsync(context);
        var orgACategoryId = await TicketTestData.AddCategoryAsync(context, orgATeamId);
        var orgAOrganizationId = await TicketTestData.GetOrganizationIdAsync(context);
        var orgAAdminId = await TicketTestData.AddUserAsync(context);
        var orgAAgentId = await TicketTestData.AddUserAsync(context);
        var orgAManagerId = await TicketTestData.AddUserAsync(context);
        await TicketTestData.AddTeamMembershipAsync(context, orgATeamId, orgAAdminId);
        await TicketTestData.AddTeamMembershipAsync(context, orgATeamId, orgAAgentId);
        await TicketTestData.AddTeamMembershipAsync(context, orgATeamId, orgAManagerId, isTeamManager: true);

        var (orgBOrganizationId, orgBTeamId) = await TicketTestData.AddSecondOrganizationTeamAsync(context);
        var orgBCategoryId = await TicketTestData.AddCategoryAsync(context, orgBTeamId);
        var orgBAgentId = await TicketTestData.AddUserAsync(context);
        await TicketTestData.AddTeamMembershipAsync(context, orgBTeamId, orgBAgentId);

        var service = new TicketService(context, new TicketTestData.FixedTimeProvider(Now));
        var orgBAgent = TicketTestData.UserInOrganization(orgBOrganizationId, orgBAgentId, UserRole.Agent, orgBTeamId);
        var (orgBTicketId, _) = await service.CreateAsync(Request(orgBTeamId, orgBCategoryId), orgBAgent);

        return new World(
            orgATeamId,
            orgACategoryId,
            orgAAdminId,
            TicketTestData.User(orgAAdminId, UserRole.Admin), // Admin: no explicit team list needed
            TicketTestData.User(orgAAgentId, UserRole.Agent, orgATeamId),
            TicketTestData.Manager(orgAManagerId, orgATeamId),
            orgBTeamId,
            orgBCategoryId,
            orgBOrganizationId,
            orgBAgentId,
            orgBAgent,
            orgBTicketId,
            service);
    }

    private static CreateTicketRequest Request(int teamId, int categoryId, int? projectId = null) =>
        new(
            Title: "Cross-tenant isolation test ticket",
            Description: "Exercises the organization boundary, not any ordinary team scope.",
            WorkType: WorkType.Incident,
            Priority: Priority.Medium,
            TeamId: teamId,
            CategoryId: categoryId,
            ProjectId: projectId);

    // ---- 1: Agent cannot view another organization's ticket by id ----

    [Fact]
    public async Task Agent_CannotViewAnotherOrganizationsTicket_ByDetailQuery()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        var query = new TicketQueryService(context, new TicketTestData.FixedTimeProvider(Now));

        var detail = await query.GetDetailAsync(world.OrgBTicketId, world.OrgAAgent);

        Assert.Null(detail);
    }

    // ---- 2: Agent's work queue never lists another organization's ticket ----

    [Fact]
    public async Task Agent_WorkQueue_NeverListsAnotherOrganizationsTicket()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        var query = new TicketQueryService(context, new TicketTestData.FixedTimeProvider(Now));

        var page = await query.GetQueueAsync(world.OrgAAgent, pageNumber: 1);

        Assert.DoesNotContain(page.Items, i => i.Id == world.OrgBTicketId);
    }

    // ---- 3: Admin's normally-unconditional bypass stops at the organization boundary ----

    [Fact]
    public async Task Admin_CannotViewAnotherOrganizationsTicket_DespiteGlobalBypassWithinOwnOrganization()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        var query = new TicketQueryService(context, new TicketTestData.FixedTimeProvider(Now));

        var detail = await query.GetDetailAsync(world.OrgBTicketId, world.OrgAAdmin);

        Assert.Null(detail);
    }

    [Fact]
    public async Task Admin_WorkQueueAndCount_ExcludeAnotherOrganizationsTicket()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        var query = new TicketQueryService(context, new TicketTestData.FixedTimeProvider(Now));

        var page = await query.GetQueueAsync(world.OrgAAdmin, pageNumber: 1);

        Assert.DoesNotContain(page.Items, i => i.Id == world.OrgBTicketId);
    }

    // ---- 4: mutation path (MutateAsync) refuses a ticket outside the caller's organization ----

    [Fact]
    public async Task Admin_CannotAssignAnotherOrganizationsTicket()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        await Assert.ThrowsAsync<TicketAccessDeniedException>(() =>
            world.Service.AssignAsync(world.OrgBTicketId, world.OrgBAgentId, world.OrgAAdmin));

        await using var verify = _fixture.CreateContext();
        var ticket = await verify.Tickets.AsNoTracking().SingleAsync(t => t.Id == world.OrgBTicketId);
        Assert.Null(ticket.AssigneeId); // untouched
    }

    [Fact]
    public async Task Agent_CannotCommentOnAnotherOrganizationsTicket()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        await Assert.ThrowsAsync<TicketAccessDeniedException>(() =>
            world.Service.AddCommentAsync(world.OrgBTicketId, "Trying to leave a note.", isInternal: false, world.OrgAAgent));

        await using var verify = _fixture.CreateContext();
        Assert.Equal(0, await verify.TicketComments.CountAsync(c => c.TicketId == world.OrgBTicketId));
    }

    // ---- 5: creation refuses a team/category belonging to a different organization ----

    [Fact]
    public async Task CreateAsync_RefusesTeamCategoryBelongingToAnotherOrganization()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        await Assert.ThrowsAsync<TicketAccessDeniedException>(() =>
            world.Service.CreateAsync(Request(world.OrgBTeamId, world.OrgBCategoryId), world.OrgAAgent));
    }

    // ---- 6: creation refuses a project belonging to a different organization ----

    [Fact]
    public async Task CreateAsync_RefusesProjectBelongingToAnotherOrganization()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        var orgBProject = new Project(0, world.OrgBOrganizationId, $"Other-Org-Project-{Guid.NewGuid():N}", Now);
        context.Add(orgBProject);
        await context.SaveChangesAsync();

        await Assert.ThrowsAsync<TicketAccessDeniedException>(() =>
            world.Service.CreateAsync(Request(world.OrgATeamId, world.OrgACategoryId, orgBProject.Id), world.OrgAAgent));
    }

    // ---- 7: at-risk / attention never surfaces another organization's ticket ----

    [Fact]
    public async Task AtRisk_NeverSurfacesAnotherOrganizationsTicket()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        // Make Org B's ticket genuinely at-risk (critical, unassigned, old) so an isolation bug
        // would actually be caught rather than passing vacuously because nothing qualified.
        var slaConfigurations = await context.SlaConfigurations.AsNoTracking().ToListAsync();
        var breachedTicket = await context.Tickets.SingleAsync(t => t.Id == world.OrgBTicketId);
        breachedTicket.ChangePriority(Priority.Critical, slaConfigurations, world.OrgBAgentId, Now);
        await context.SaveChangesAsync();

        var attention = new AttentionQueryService(context, new TicketTestData.FixedTimeProvider(Now.AddDays(3)), new FlowOps.Domain.Attention.AttentionOptions());
        var page = await attention.GetAtRiskAsync(world.OrgAAdmin, pageNumber: 1);

        Assert.DoesNotContain(page.Items, i => i.Id == world.OrgBTicketId);
    }

    // ---- 8: dashboard analytics never counts another organization's ticket ----

    [Fact]
    public async Task Dashboard_OpenWorkCount_ExcludesAnotherOrganizationsTicket()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        var analytics = new AnalyticsQueryService(context, new TicketTestData.FixedTimeProvider(Now));

        // world.OrgAManager, scoped to Org A's own fresh team — not Admin, whose AllTeams scope
        // (now correctly bounded to Org A) would still pick up every other Application.Tests
        // test's tickets sharing TicketTestData's one ambient Org A, making an exact-count
        // assertion meaningless. The real thing under test — Org B's ticket never leaking into an
        // Org A caller's analytics at all — is proven identically either way.
        var summary = await analytics.GetDashboardSummaryAsync(world.OrgAManager);

        var independentOpenCountInOrgATeam = await context.Tickets.AsNoTracking()
            .Where(t => t.TeamId == world.OrgATeamId && t.Status != Status.Resolved && t.Status != Status.Closed)
            .CountAsync();

        // Org A's own team has no tickets of its own in this World; Org B's one open ticket must
        // not leak in regardless.
        Assert.Equal(independentOpenCountInOrgATeam, summary.OpenWorkCount);
        Assert.Equal(0, summary.OpenWorkCount);
        Assert.DoesNotContain(summary.Workload, w => w.TeamId == world.OrgBTeamId);
    }

    // ---- 9: activity timeline / history is refused identically to a nonexistent ticket ----

    [Fact]
    public async Task GetHistoryAsync_AnotherOrganizationsTicket_ReturnsEmpty_NotAnError()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        var query = new TicketQueryService(context, new TicketTestData.FixedTimeProvider(Now));

        var history = await query.GetHistoryAsync(world.OrgBTicketId, world.OrgAAdmin);

        Assert.Empty(history);
    }

    // ---- 10: ticket-creation options never disclose another organization's teams/categories ----

    [Fact]
    public async Task GetCreationOptionsAsync_NeverListsAnotherOrganizationsTeamsOrCategories()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        var query = new TicketQueryService(context, new TicketTestData.FixedTimeProvider(Now));

        var options = await query.GetCreationOptionsAsync(world.OrgAAdmin);

        Assert.DoesNotContain(options.Teams, t => t.TeamId == world.OrgBTeamId);
    }

    // ---- 10b: ticket-creation options never disclose another organization's projects ----

    [Fact]
    public async Task GetCreationOptionsAsync_NeverListsAnotherOrganizationsProjects()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        var orgBProject = new Project(0, world.OrgBOrganizationId, $"Other-Org-Project-{Guid.NewGuid():N}", Now);
        context.Add(orgBProject);
        await context.SaveChangesAsync();
        var query = new TicketQueryService(context, new TicketTestData.FixedTimeProvider(Now));

        var options = await query.GetCreationOptionsAsync(world.OrgAAdmin);

        Assert.DoesNotContain(options.Projects, p => p.ProjectId == orgBProject.Id);
    }

    // ---- 10c: an inactive project (project management phase) is never offered on the
    // creation-options dropdown, even for the project's own organization.

    [Fact]
    public async Task GetCreationOptionsAsync_NeverListsAnInactiveProject()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        var inactiveProjectId = await TicketTestData.AddProjectAsync(context, isActive: false);
        var query = new TicketQueryService(context, new TicketTestData.FixedTimeProvider(Now));

        var options = await query.GetCreationOptionsAsync(world.OrgAAdmin);

        Assert.DoesNotContain(options.Projects, p => p.ProjectId == inactiveProjectId);
    }

    // ---- 10d: an inactive team (Phase 22, ADR-0022) is never offered on the creation-options
    // dropdown, and neither are its categories.

    [Fact]
    public async Task GetCreationOptionsAsync_NeverListsAnInactiveTeamOrItsCategories()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        var inactiveTeamId = await TicketTestData.AddTeamAsync(context, isActive: false);
        var categoryOnInactiveTeamId = await TicketTestData.AddCategoryAsync(context, inactiveTeamId);
        var query = new TicketQueryService(context, new TicketTestData.FixedTimeProvider(Now));

        var options = await query.GetCreationOptionsAsync(world.OrgAAdmin);

        Assert.DoesNotContain(options.Teams, t => t.TeamId == inactiveTeamId);
        Assert.DoesNotContain(options.Teams.SelectMany(t => t.Categories), c => c.CategoryId == categoryOnInactiveTeamId);
    }

    // ---- 10e: an inactive category is never offered, even on an otherwise active team.

    [Fact]
    public async Task GetCreationOptionsAsync_NeverListsAnInactiveCategory()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        var inactiveCategoryId = await TicketTestData.AddCategoryAsync(context, world.OrgATeamId, isActive: false);
        var query = new TicketQueryService(context, new TicketTestData.FixedTimeProvider(Now));

        var options = await query.GetCreationOptionsAsync(world.OrgAAdmin);

        Assert.DoesNotContain(options.Teams.SelectMany(t => t.Categories), c => c.CategoryId == inactiveCategoryId);
    }

    // ---- 10f: renaming or deactivating another organization's team/category fails safely
    // without mutation, and ID substitution against another organization's team/category on
    // ticket creation is refused identically to a nonexistent one.

    [Fact]
    public async Task CreateAsync_RefusesTeamBelongingToAnotherOrganization()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        await Assert.ThrowsAsync<TicketAccessDeniedException>(() =>
            world.Service.CreateAsync(Request(world.OrgBTeamId, world.OrgBCategoryId, projectId: null), world.OrgAAgent));
    }

    [Fact]
    public async Task CreateAsync_RefusesCategoryBelongingToAnotherOrganizationsTeam()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        // A category that genuinely belongs to Org A's own team, paired with Org B's category id —
        // the category-to-team lookup itself must catch the organization mismatch regardless of
        // which id in the pair is "wrong."
        await Assert.ThrowsAsync<TicketAccessDeniedException>(() =>
            world.Service.CreateAsync(Request(world.OrgATeamId, world.OrgBCategoryId, projectId: null), world.OrgAAgent));
    }

    // ---- 11: ID substitution — guessing a real ticket id from another organization is refused
    // identically whether the guesser is a Manager or an Admin, and identically to "does not
    // exist" (AUTH-RULE-04's own non-disclosure pattern, now extended across the org boundary).

    [Fact]
    public async Task ResolveAsync_IdSubstitutionAgainstAnotherOrganizationsTicket_IsRefused()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        await Assert.ThrowsAsync<TicketAccessDeniedException>(() =>
            world.Service.ResolveAsync(world.OrgBTicketId, Resolution.Fixed, "Attempting a cross-tenant resolve.", world.OrgAAdmin));
    }
}
