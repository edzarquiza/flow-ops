using FlowOps.Domain.Catalog;
using FlowOps.Domain.Directory;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Identity;
using FlowOps.Infrastructure.Persistence;

namespace FlowOps.Application.Tests.Tickets;

/// <summary>
/// Controlled reference data for the Phase 5 integration tests. Deliberately not a demo seeder
/// (CLAUDE.md §14 / Phase 15): each test creates only the teams, categories, and users it needs,
/// with unique names so tests sharing one container database cannot collide.
/// </summary>
internal static class TicketTestData
{
    public static async Task<int> AddTeamAsync(FlowOpsDbContext context)
    {
        var team = new Team(0, $"Team-{Guid.NewGuid():N}", DateTimeOffset.UtcNow);
        context.Add(team);
        await context.SaveChangesAsync();
        return team.Id;
    }

    public static async Task<int> AddCategoryAsync(FlowOpsDbContext context, int teamId)
    {
        var category = new Category(0, teamId, $"Category-{Guid.NewGuid():N}", WorkType.Incident, DateTimeOffset.UtcNow);
        context.Add(category);
        await context.SaveChangesAsync();
        return category.Id;
    }

    public static async Task<Guid> AddUserAsync(FlowOpsDbContext context)
    {
        var user = new ApplicationUser
        {
            UserName = $"{Guid.NewGuid():N}@test.local",
            Email = $"{Guid.NewGuid():N}@test.local",
            DisplayName = "Phase 5 Test User",
            IsActive = true,
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
    public static CurrentUser User(Guid userId, UserRole role, params int[] memberTeamIds) =>
        new(userId, role, memberTeamIds.ToHashSet(), new HashSet<int>());

    /// <summary>
    /// A Manager of the given teams. Separate from <see cref="User"/> because the policy's
    /// "own teams" scoping reads <c>ManagedTeamIds</c>, and a manager is by definition also a
    /// member of the teams they manage (docs/database.md §3, <c>team_members.is_team_manager</c>).
    /// </summary>
    public static CurrentUser Manager(Guid userId, params int[] managedTeamIds) =>
        new(userId, UserRole.Manager, managedTeamIds.ToHashSet(), managedTeamIds.ToHashSet());

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
