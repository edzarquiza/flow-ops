using FlowOps.Application.Tickets;
using FlowOps.Domain.Catalog;
using FlowOps.Domain.Directory;
using FlowOps.Domain.Organizations;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Identity;
using FlowOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FlowOps.Web.Tests.Fixtures;

/// <summary>
/// The minimum reference data the Phase 5 pages need to be exercisable: one organization, one
/// team, one category belonging to it, memberships for the users whose access is being tested,
/// and one existing ticket. Test-only and scoped to the ephemeral Testcontainers database — this
/// is not the demo seeder (CLAUDE.md §14), which is Phase 15.
/// </summary>
/// <remarks>
/// Manager and Agent are members of the team; Admin and Viewer are not. That split is what lets
/// the page tests distinguish the three interesting cases: sees-it-by-membership,
/// sees-it-because-Admin, and cannot-see-it-at-all.
///
/// Phase 16: every one of <see cref="TestUsers"/>'s four accounts also gets an
/// <see cref="OrganizationMembership"/> in this one seeded organization — <c>CurrentUserAccessor</c>
/// now refuses to resolve a <see cref="CurrentUser"/> at all for a user with no membership, so
/// without this every Web.Tests page test would see every request as unauthenticated.
/// </remarks>
internal static class TestReferenceData
{
    public const string OrganizationName = "Web Tests Organization";
    public const string TeamName = "Service Desk";
    public const string CategoryName = "Printers";

    public static async Task<(int OrganizationId, int TeamId, int CategoryId, int TicketId)> SeedAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<FlowOpsDbContext>();
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();

        var organization = new Organization(0, OrganizationName, DateTimeOffset.UtcNow);
        db.Add(organization);
        await db.SaveChangesAsync();

        var team = new Team(0, organization.Id, TeamName, DateTimeOffset.UtcNow);
        db.Add(team);
        await db.SaveChangesAsync();

        var category = new Category(0, team.Id, CategoryName, WorkType.Incident, DateTimeOffset.UtcNow);
        db.Add(category);
        await db.SaveChangesAsync();

        var adminId = await UserIdAsync(userManager, TestUsers.AdminEmail);
        var managerId = await UserIdAsync(userManager, TestUsers.ManagerEmail);
        var agentId = await UserIdAsync(userManager, TestUsers.AgentEmail);
        var viewerId = await UserIdAsync(userManager, TestUsers.ViewerEmail);

        db.Add(new OrganizationMembership(0, organization.Id, adminId, UserRole.Admin, DateTimeOffset.UtcNow));
        db.Add(new OrganizationMembership(0, organization.Id, managerId, UserRole.Manager, DateTimeOffset.UtcNow));
        db.Add(new OrganizationMembership(0, organization.Id, agentId, UserRole.Agent, DateTimeOffset.UtcNow));
        db.Add(new OrganizationMembership(0, organization.Id, viewerId, UserRole.Viewer, DateTimeOffset.UtcNow));

        db.Add(new TeamMember(team.Id, managerId, isTeamManager: true, DateTimeOffset.UtcNow));
        db.Add(new TeamMember(team.Id, agentId, isTeamManager: false, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();

        // Created through the real service so the seeded ticket is indistinguishable from one a
        // user created through the UI — same aggregate, same reference sequence, same audit event.
        var ticketService = services.GetRequiredService<TicketService>();
        var (ticketId, _) = await ticketService.CreateAsync(
            new CreateTicketRequest(
                Title: "VPN client will not connect",
                Description: "The VPN client reports an authentication failure on launch.",
                WorkType: WorkType.Incident,
                Priority: Priority.High,
                TeamId: team.Id,
                CategoryId: category.Id,
                ProjectId: null),
            new CurrentUser(agentId, organization.Id, UserRole.Agent, new HashSet<int> { team.Id }, new HashSet<int>()));

        return (organization.Id, team.Id, category.Id, ticketId);
    }

    /// <summary>Resolves a seeded test user's id from a scope, for fixtures that need one.</summary>
    public static Task<Guid> UserIdAsync(IServiceProvider services, string email) =>
        UserIdAsync(services.GetRequiredService<UserManager<ApplicationUser>>(), email);

    private static async Task<Guid> UserIdAsync(UserManager<ApplicationUser> userManager, string email)
    {
        var user = await userManager.Users.SingleAsync(u => u.Email == email);
        return user.Id;
    }
}
