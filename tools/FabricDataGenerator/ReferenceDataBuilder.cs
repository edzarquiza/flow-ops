using FlowOps.Domain.Catalog;
using FlowOps.Domain.Directory;
using FlowOps.Domain.Organizations;
using FlowOps.Domain.Sla;
using FlowOps.Domain.Tickets;

namespace FabricDataGenerator;

/// <summary>
/// Builds the small, fixed reference dataset (organization, teams, categories, projects, users,
/// team memberships, SLA configurations) using the real FlowOps.Domain entity constructors —
/// exactly the same types <c>DemoDataSeeder</c> uses, just at portfolio-demo scale instead of the
/// 22-ticket production demo's scale. Each step takes the real (database-assigned) ids of
/// whatever it depends on, mirroring <c>DemoDataSeeder</c>'s own sequential
/// create-then-<c>SaveChanges</c> shape.
/// </summary>
internal sealed class ReferenceDataBuilder
{
    private readonly Random _random;
    private readonly DateTimeOffset _seedTime;

    public ReferenceDataBuilder(Random random, DateTimeOffset seedTime)
    {
        _random = random;
        _seedTime = seedTime;
    }

    public Organization BuildOrganization() => new(0, "Northwind IT Services", _seedTime);

    public List<Team> BuildTeams(int organizationId) =>
    [
        new(0, organizationId, "Service Desk", _seedTime),
        new(0, organizationId, "Application Support", _seedTime),
        new(0, organizationId, "Infrastructure", _seedTime),
        new(0, organizationId, "Business Operations", _seedTime),
        new(0, organizationId, "Development", _seedTime),
    ];

    /// <summary>12 categories across the 5 teams (3/3/3/2/1), each carrying a realistic default
    /// work type.</summary>
    public List<Category> BuildCategories(IReadOnlyList<Team> teams)
    {
        (string Name, WorkType WorkType, int TeamIndex)[] spec =
        [
            ("Password Reset", WorkType.ServiceRequest, 0),
            ("Access Request", WorkType.ServiceRequest, 0),
            ("Hardware", WorkType.ServiceRequest, 0),
            ("Application Issue", WorkType.Incident, 1),
            ("Account Issue", WorkType.Incident, 1),
            ("Software", WorkType.ServiceRequest, 1),
            ("Network", WorkType.Incident, 2),
            ("Configuration", WorkType.Task, 2),
            ("Incident", WorkType.Incident, 2),
            ("Data Request", WorkType.ServiceRequest, 3),
            ("Service Request", WorkType.ServiceRequest, 3),
            ("Other", WorkType.Task, 4),
        ];

        return [.. spec.Select(s => new Category(0, teams[s.TeamIndex].Id, s.Name, s.WorkType, _seedTime))];
    }

    public List<Project> BuildProjects(int organizationId) =>
        [.. Names.ProjectNames.Select(name => new Project(0, organizationId, name, _seedTime))];

    public List<GeneratedUser> BuildUsers()
    {
        var users = new List<GeneratedUser>(GenerationOptions.UserCount);
        var roleSchedule = BuildRoleSchedule();
        var usedEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < GenerationOptions.UserCount; i++)
        {
            var role = roleSchedule[i];
            var first = Names.FirstNames[_random.Next(Names.FirstNames.Length)];
            var last = Names.LastNames[_random.Next(Names.LastNames.Length)];
            var displayName = $"{first} {last}";

            var baseEmail = $"{first.ToLowerInvariant()}.{last.ToLowerInvariant()}@flowops-demo.local";
            var email = baseEmail;
            var suffix = 2;
            while (!usedEmails.Add(email))
            {
                email = $"{first.ToLowerInvariant()}.{last.ToLowerInvariant()}{suffix}@flowops-demo.local";
                suffix++;
            }

            var jobTitle = Names.JobTitles[_random.Next(Names.JobTitles.Length)];

            // A fixed per-user workload multiplier (0.4x - 2.2x) so ticket assignment is skewed —
            // some agents genuinely carry more work than others, not uniformly distributed.
            var workloadWeight = 0.4 + _random.NextDouble() * 1.8;

            users.Add(new GeneratedUser(Guid.NewGuid(), displayName, email, jobTitle, role, workloadWeight));
        }

        return users;
    }

    /// <summary>
    /// Team memberships: both Admins sit on every team (so an Admin is always a legal assignee
    /// anywhere); each of the 5 Managers manages exactly one team; the 15 Agents are split 3 per
    /// team; the 3 Viewers each sit on one team. Produces 33 rows total — comfortably over the
    /// brief's "25+" target.
    /// </summary>
    public List<TeamMember> BuildTeamMembers(IReadOnlyList<Team> teams, IReadOnlyList<GeneratedUser> users)
    {
        var admins = users.Where(u => u.Role == UserRole.Admin).ToList();
        var managers = users.Where(u => u.Role == UserRole.Manager).ToList();
        var agents = users.Where(u => u.Role == UserRole.Agent).ToList();
        var viewers = users.Where(u => u.Role == UserRole.Viewer).ToList();

        var members = new List<TeamMember>();

        foreach (var admin in admins)
        {
            foreach (var team in teams)
            {
                members.Add(new TeamMember(team.Id, admin.Id, false, _seedTime));
            }
        }

        for (var i = 0; i < managers.Count && i < teams.Count; i++)
        {
            members.Add(new TeamMember(teams[i].Id, managers[i].Id, true, _seedTime));
        }

        for (var i = 0; i < agents.Count; i++)
        {
            var team = teams[i % teams.Count];
            members.Add(new TeamMember(team.Id, agents[i].Id, false, _seedTime));
        }

        for (var i = 0; i < viewers.Count; i++)
        {
            var team = teams[i % teams.Count];
            members.Add(new TeamMember(team.Id, viewers[i].Id, false, _seedTime));
        }

        return members;
    }

    public List<OrganizationMembership> BuildOrganizationMemberships(int organizationId, IReadOnlyList<GeneratedUser> users) =>
        [.. users.Select(u => new OrganizationMembership(0, organizationId, u.Id, u.Role, _seedTime))];

    /// <summary>2 Admins, 5 Managers (one per team), 15 Agents, 3 Viewers — sums to
    /// <see cref="GenerationOptions.UserCount"/> (25).</summary>
    private static UserRole[] BuildRoleSchedule()
    {
        var roles = new List<UserRole>();
        roles.AddRange(Enumerable.Repeat(UserRole.Admin, 2));
        roles.AddRange(Enumerable.Repeat(UserRole.Manager, 5));
        roles.AddRange(Enumerable.Repeat(UserRole.Agent, 15));
        roles.AddRange(Enumerable.Repeat(UserRole.Viewer, 3));
        return [.. roles];
    }

    /// <summary>Exactly 20 rows: one exact (WorkType, Priority) row for all 4 work types times 4
    /// priorities, plus one (null, Priority) default row per priority — matching SLA-RULE-01's
    /// resolution order and landing squarely on the brief's "approximately 20" target.</summary>
    public static List<SlaConfiguration> BuildSlaConfigurations()
    {
        // Base target minutes per priority for the default row; exact-match rows adjust from this.
        var basePriorityMinutes = new Dictionary<Priority, int>
        {
            [Priority.Critical] = 240,
            [Priority.High] = 480,
            [Priority.Medium] = 1440,
            [Priority.Low] = 4320,
        };

        // Per-work-type multiplier against the base, so e.g. Incidents are tighter than Tasks.
        var workTypeMultiplier = new Dictionary<WorkType, double>
        {
            [WorkType.Incident] = 0.75,
            [WorkType.Problem] = 1.0,
            [WorkType.ServiceRequest] = 1.1,
            [WorkType.Task] = 1.3,
        };

        // The four (null, Priority) default rows already exist — SlaConfigurationConfiguration's
        // own HasData seeds them via the real EF migrations this tool applies above, so only the
        // per-work-type override rows (which the migrations do NOT seed) are generated here.
        var configurations = new List<SlaConfiguration>();

        foreach (var priority in Enum.GetValues<Priority>())
        {
            foreach (var workType in Enum.GetValues<WorkType>())
            {
                var minutes = (int)Math.Round(basePriorityMinutes[priority] * workTypeMultiplier[workType]);
                configurations.Add(new SlaConfiguration(0, workType, priority, minutes, 80));
            }
        }

        return configurations;
    }
}

internal sealed record GeneratedUser(Guid Id, string DisplayName, string Email, string JobTitle, UserRole Role, double WorkloadWeight);
