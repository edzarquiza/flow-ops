using FlowOps.Domain.Catalog;
using FlowOps.Domain.Directory;
using FlowOps.Domain.Organizations;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Identity;
using FlowOps.Infrastructure.Persistence;

namespace FlowOps.Application.Tests.Tickets;

/// <summary>
/// Controlled reference data for the Phase 5 integration tests. Deliberately not a demo seeder
/// (CLAUDE.md §14 / Phase 15): each test creates only the teams, categories, and users it needs,
/// with unique names so tests sharing one container database cannot collide.
/// </summary>
/// <remarks>
/// Phase 16: every team this helper creates belongs to one lazily-created, process-lifetime
/// Organization shared by every Application.Tests test in this collection (mirroring how these
/// tests already share one Postgres container per <c>[Collection("Postgres")]</c> run) — so
/// existing tests that create several teams and expect Admin/Viewer scoping to span all of them
/// keep exactly the behavior they had before organizations existed. A test that specifically wants
/// to exercise cross-organization isolation (Part E) uses <see cref="AddSecondOrganizationTeamAsync"/>
/// instead, which creates a genuinely separate Organization.
/// </remarks>
internal static class TicketTestData
{
    private static int? _organizationId;
    private static readonly SemaphoreSlim OrganizationLock = new(1, 1);

    /// <summary>Gets (creating on first use) the one Organization every <see cref="AddTeamAsync"/>
    /// team belongs to for the lifetime of this test process/container.</summary>
    public static async Task<int> GetOrganizationIdAsync(FlowOpsDbContext context)
    {
        if (_organizationId is { } cached)
        {
            return cached;
        }

        await OrganizationLock.WaitAsync();
        try
        {
            if (_organizationId is { } cachedAfterLock)
            {
                return cachedAfterLock;
            }

            var organization = new Organization(0, $"Application Tests Organization {Guid.NewGuid():N}", DateTimeOffset.UtcNow);
            context.Add(organization);
            await context.SaveChangesAsync();
            _organizationId = organization.Id;
            return organization.Id;
        }
        finally
        {
            OrganizationLock.Release();
        }
    }

    public static async Task<int> AddTeamAsync(FlowOpsDbContext context, bool isActive = true)
    {
        var organizationId = await GetOrganizationIdAsync(context);
        var team = new Team(0, organizationId, $"Team-{Guid.NewGuid():N}", DateTimeOffset.UtcNow, isActive);
        context.Add(team);
        await context.SaveChangesAsync();
        return team.Id;
    }

    /// <summary>
    /// Phase 16 (Part E): a team in a brand-new, genuinely separate Organization — for tests that
    /// exercise cross-organization isolation rather than ordinary cross-team scoping.
    /// </summary>
    public static async Task<(int OrganizationId, int TeamId)> AddSecondOrganizationTeamAsync(FlowOpsDbContext context)
    {
        var organization = new Organization(0, $"Other Org {Guid.NewGuid():N}", DateTimeOffset.UtcNow);
        context.Add(organization);
        await context.SaveChangesAsync();

        var team = new Team(0, organization.Id, $"Team-{Guid.NewGuid():N}", DateTimeOffset.UtcNow);
        context.Add(team);
        await context.SaveChangesAsync();
        return (organization.Id, team.Id);
    }

    public static async Task<int> AddCategoryAsync(FlowOpsDbContext context, int teamId, bool isActive = true)
    {
        var category = new Category(0, teamId, $"Category-{Guid.NewGuid():N}", WorkType.Incident, DateTimeOffset.UtcNow, isActive);
        context.Add(category);
        await context.SaveChangesAsync();
        return category.Id;
    }

    public static async Task<int> AddProjectAsync(FlowOpsDbContext context, bool isActive = true)
    {
        var organizationId = await GetOrganizationIdAsync(context);
        var project = new Project(0, organizationId, $"Project-{Guid.NewGuid():N}", DateTimeOffset.UtcNow, isActive);
        context.Add(project);
        await context.SaveChangesAsync();
        return project.Id;
    }

    public static async Task<Guid> AddUserAsync(FlowOpsDbContext context, string displayName = "Phase 5 Test User")
    {
        var user = new ApplicationUser
        {
            UserName = $"{Guid.NewGuid():N}@test.local",
            Email = $"{Guid.NewGuid():N}@test.local",
            DisplayName = displayName,
            IsActive = true,
            // Phase 24A (ADR-0024): this fixture exists to be a normal, already-usable account for
            // whatever the test under construction actually exercises — never to itself exercise
            // the approval gate. Left unset (Pending), it would stop counting as "another Admin"
            // for SoleAdminGuard's own is-active check, silently breaking every test that uses this
            // helper to set up a second Admin.
            RegistrationApprovedAt = DateTimeOffset.UtcNow,
        };

        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user.Id;
    }

    public static async Task AddTeamMembershipAsync(FlowOpsDbContext context, int teamId, Guid userId, bool isTeamManager = false)
    {
        context.Add(new TeamMember(teamId, userId, isTeamManager, DateTimeOffset.UtcNow));
        await context.SaveChangesAsync();
    }

    /// <summary>A <see cref="CurrentUser"/> as <c>CurrentUserAccessor</c> would have resolved it —
    /// built directly here so these tests exercise the services, not Identity's plumbing.</summary>
    /// <remarks>
    /// Phase 16: uses the shared <see cref="AddTeamAsync"/> Organization — callers must have
    /// already called <see cref="AddTeamAsync"/> (directly or via <see cref="GetOrganizationIdAsync"/>)
    /// at least once in this test, which every existing <c>SeedAsync</c> already does before
    /// building its <c>CurrentUser</c> values. A caller building a user for a second, genuinely
    /// separate organization (Part E) must use the <see cref="UserInOrganization"/> overload
    /// instead, passing the id <see cref="AddSecondOrganizationTeamAsync"/> returned.
    /// </remarks>
    public static CurrentUser User(Guid userId, UserRole role, params int[] memberTeamIds) =>
        UserInOrganization(RequireOrganizationId(), userId, role, memberTeamIds);

    /// <summary>Phase 16 (Part E): a user explicitly scoped to a given organization, for
    /// cross-organization isolation tests.</summary>
    public static CurrentUser UserInOrganization(int organizationId, Guid userId, UserRole role, params int[] memberTeamIds) =>
        new(userId, organizationId, role, memberTeamIds.ToHashSet(), new HashSet<int>());

    /// <summary>
    /// A Manager of the given teams. Separate from <see cref="User"/> because the policy's
    /// "own teams" scoping reads <c>ManagedTeamIds</c>, and a manager is by definition also a
    /// member of the teams they manage (docs/database.md §3, <c>team_members.is_team_manager</c>).
    /// </summary>
    public static CurrentUser Manager(Guid userId, params int[] managedTeamIds) =>
        new(userId, RequireOrganizationId(), UserRole.Manager, managedTeamIds.ToHashSet(), managedTeamIds.ToHashSet());

    private static int RequireOrganizationId() =>
        _organizationId ?? throw new InvalidOperationException(
            "TicketTestData.User/Manager was called before AddTeamAsync established this test's Organization.");

    /// <summary>Fixed, caller-controlled clock — TICKET-INV-10 requires the Domain never read the
    /// real one, and these tests assert exact persisted timestamps.</summary>
    internal sealed class FixedTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
