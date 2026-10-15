using FlowOps.Domain.Catalog;
using FlowOps.Domain.Directory;
using FlowOps.Domain.Sla;
using FlowOps.Domain.Tickets;

namespace FabricDataGenerator;

/// <summary>Precomputed lookups (real database ids already assigned) that <see cref="TicketGenerator"/>
/// needs to pick valid, related rows for each synthetic ticket.</summary>
internal sealed class TicketWorkContext
{
    public required IReadOnlyList<Team> Teams { get; init; }
    public required IReadOnlyList<Category> Categories { get; init; }
    public required IReadOnlyList<int> CategoryTeamIndex { get; init; }
    public required IReadOnlyList<Project> Projects { get; init; }
    public required IReadOnlyList<GeneratedUser> Users { get; init; }
    public required IReadOnlyList<SlaConfiguration> SlaConfigurations { get; init; }

    /// <summary>teamId -> active, work-eligible members (Admin/Manager/Agent — Viewers are
    /// members for visibility but are never picked as an assignee here).</summary>
    public required IReadOnlyDictionary<int, List<GeneratedUser>> TeamWorkers { get; init; }

    /// <summary>teamId -> the Manager (or an Admin, if none) who acts as assigner/closer for that team.</summary>
    public required IReadOnlyDictionary<int, GeneratedUser> TeamLead { get; init; }

    public static TicketWorkContext Build(
        IReadOnlyList<Team> teams,
        IReadOnlyList<Category> categories,
        IReadOnlyList<int> categoryTeamIndex,
        IReadOnlyList<Project> projects,
        IReadOnlyList<GeneratedUser> users,
        IReadOnlyList<SlaConfiguration> slaConfigurations,
        IReadOnlyList<TeamMember> teamMembers)
    {
        var teamWorkers = new Dictionary<int, List<GeneratedUser>>();
        var teamLead = new Dictionary<int, GeneratedUser>();

        foreach (var team in teams)
        {
            var memberIds = teamMembers.Where(m => m.TeamId == team.Id).Select(m => m.UserId).ToHashSet();
            var workers = users.Where(u => memberIds.Contains(u.Id) && u.Role != UserRole.Viewer).ToList();
            teamWorkers[team.Id] = workers;

            var lead = workers.FirstOrDefault(u => u.Role == UserRole.Manager)
                ?? workers.FirstOrDefault(u => u.Role == UserRole.Admin)
                ?? workers.First();
            teamLead[team.Id] = lead;
        }

        return new TicketWorkContext
        {
            Teams = teams,
            Categories = categories,
            CategoryTeamIndex = categoryTeamIndex,
            Projects = projects,
            Users = users,
            SlaConfigurations = slaConfigurations,
            TeamWorkers = teamWorkers,
            TeamLead = teamLead,
        };
    }
}
