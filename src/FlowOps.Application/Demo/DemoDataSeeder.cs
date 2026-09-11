using FlowOps.Application.Tickets;
using FlowOps.Domain.Catalog;
using FlowOps.Domain.Directory;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Identity;
using FlowOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FlowOps.Application.Demo;

/// <summary>
/// CLAUDE.md §14: the public-portfolio demo dataset. Runs once, at startup, only when
/// <see cref="DemoOptions.Enabled"/> is true — see <c>Program.cs</c> for the ordering (after
/// migrations, before the app starts serving). Idempotent: skips entirely if any ticket already
/// exists, matching CLAUDE.md §14's exact wording ("idempotent (skips if tickets exist)").
/// </summary>
/// <remarks>
/// Every ticket, transition, and comment is created through <see cref="TicketService"/> — the same
/// authorization/audit path a real user's request takes — never a raw insert, per CLAUDE.md §14:
/// "Seeded tickets are created through the domain methods, not by inserting rows that bypass
/// invariants. If the seeder cannot produce a state through legal transitions, the state is
/// illegal and the seeder is right to fail." Only genuinely invariant-free reference data (teams,
/// categories, projects, users, team memberships) is inserted directly, the same way the existing
/// Web.Tests fixtures already seed that same kind of data.
/// </remarks>
public sealed class DemoDataSeeder
{
    // CLAUDE.md §14's exact volume. Kept as instance state (not a hardcoded literal inside the
    // method) so a test can construct this seeder with a smaller volume without duplicating the
    // whole class — the production default is what Program.cs actually uses.
    public sealed record Volume(int TicketCount, int TargetCommentCount)
    {
        public static readonly Volume Default = new(TicketCount: 600, TargetCommentCount: 1500);
    }

    // CLAUDE.md §14's team/composition table. The five composition percentages and the five team
    // names are given as two separate lists of the same length with no explicit mapping between
    // them; this seeder maps them positionally (first percentage to first team name, and so on) —
    // a documented interpretation, not an invented one.
    private static readonly (string Name, double Weight)[] TeamDefinitions =
    [
        ("Service Desk", 0.45),
        ("IT Infrastructure", 0.20),
        ("Application Support", 0.10),
        ("Platform Engineering", 0.15),
        ("Business Operations", 0.10),
    ];

    private static readonly string[] ProjectNames =
    [
        "Website Refresh", "ERP Migration", "Office Relocation", "Security Hardening",
        "Customer Portal", "Data Warehouse", "Mobile App Pilot", "Vendor Consolidation",
    ];

    // Matches docs/database.md's seeded SLA-RULE-02 target minutes exactly — used only to compute
    // realistic backdating offsets here, never to decide the ticket's own SLA fields (TicketService
    // still resolves those itself from the real SlaConfigurations table, per SLA-RULE-12).
    private static readonly Dictionary<Priority, int> SeededSlaTargetMinutes = new()
    {
        [Priority.Critical] = 240,
        [Priority.High] = 480,
        [Priority.Medium] = 1440,
        [Priority.Low] = 4320,
    };

    private readonly FlowOpsDbContext _dbContext;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly DemoOptions _options;
    private readonly TimeProvider _realTimeProvider;
    private readonly ILogger<DemoDataSeeder> _logger;

    public DemoDataSeeder(
        FlowOpsDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        DemoOptions options,
        TimeProvider realTimeProvider,
        ILogger<DemoDataSeeder> logger)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _options = options;
        _realTimeProvider = realTimeProvider;
        _logger = logger;
    }

    public Task SeedAsync(CancellationToken cancellationToken = default) => SeedAsync(Volume.Default, cancellationToken);

    public async Task SeedAsync(Volume volume, CancellationToken cancellationToken = default)
    {
        if (await _dbContext.Tickets.AnyAsync(cancellationToken))
        {
            _logger.LogInformation("Demo seeding skipped: tickets already exist.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.PersonaPassword))
        {
            // Defensive: Program.cs already validates this on start via ValidateOnStart. Guards
            // against any other caller (e.g. a future admin-triggered re-seed) skipping that check.
            throw new InvalidOperationException("FlowOps:Demo:PersonaPassword must be set to seed demo data.");
        }

        // CLAUDE.md §14: "inside a transaction". EnableRetryOnFailure (Program.cs) requires any
        // user-initiated transaction to be wrapped in an execution strategy — a retried attempt
        // starts this whole delegate over, including a fresh BeginTransactionAsync, so a partial
        // failure never leaves partial state behind for the retry to build on top of.
        var strategy = _dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

            var seedTime = _realTimeProvider.GetUtcNow();
            var rng = new Random(20260101); // fixed seed — CLAUDE.md §14: "Deterministic".

            var teams = await CreateTeamsAsync(seedTime, cancellationToken);
            var categories = await CreateCategoriesAsync(teams, seedTime, cancellationToken);
            await CreateProjectsAsync(seedTime, cancellationToken);
            var users = await CreateUsersAsync(teams, cancellationToken);

            var clock = new SeederClock(seedTime);
            var ticketService = new TicketService(_dbContext, clock);

            await SeedSignalShowcaseTicketsAsync(ticketService, clock, teams, categories, users, seedTime, cancellationToken);
            await SeedBulkTicketsAsync(ticketService, clock, teams, categories, users, seedTime, rng, volume, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            _logger.LogInformation("Demo seeding completed: {TicketCount} tickets targeted.", volume.TicketCount);
        });
    }

    // ---- Reference data -----------------------------------------------------------------------

    private async Task<List<Team>> CreateTeamsAsync(DateTimeOffset seedTime, CancellationToken cancellationToken)
    {
        var teams = TeamDefinitions.Select(t => new Team(0, t.Name, seedTime)).ToList();
        _dbContext.Teams.AddRange(teams);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return teams;
    }

    private async Task<Dictionary<int, List<Category>>> CreateCategoriesAsync(
        List<Team> teams, DateTimeOffset seedTime, CancellationToken cancellationToken)
    {
        // Two categories per team is enough for TICKET-INV-02 variation without inventing a
        // separate catalog-design concept this phase was never asked to build.
        var namesByWorkType = new (string Name, WorkType WorkType)[]
        {
            ("Incidents", WorkType.Incident),
            ("Requests", WorkType.ServiceRequest),
        };

        var byTeam = new Dictionary<int, List<Category>>();
        foreach (var team in teams)
        {
            var categories = namesByWorkType
                .Select(n => new Category(0, team.Id, $"{team.Name} {n.Name}", n.WorkType, seedTime))
                .ToList();
            _dbContext.Categories.AddRange(categories);
            byTeam[team.Id] = categories;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return byTeam;
    }

    private async Task CreateProjectsAsync(DateTimeOffset seedTime, CancellationToken cancellationToken)
    {
        _dbContext.Projects.AddRange(ProjectNames.Select(name => new Project(0, name, seedTime)));
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>One seeded account plus the <see cref="CurrentUser"/> shape every
    /// <see cref="TicketService"/> call needs — resolved once, reused for every ticket.</summary>
    private sealed record SeededUser(Guid Id, UserRole Role, int TeamId, CurrentUser AsCurrentUser);

    private async Task<List<SeededUser>> CreateUsersAsync(List<Team> teams, CancellationToken cancellationToken)
    {
        var seeded = new List<SeededUser>();

        // The four named, log-in-able personas (CLAUDE.md §14) — fixed teams matching their
        // showcased role, and marked IsDemoProtected so DemoProtectionPolicy guards them.
        var personaTeams = new Dictionary<string, Team>
        {
            [DemoPersonas.All[0].Email] = teams[0], // Service Desk Manager -> Service Desk
            [DemoPersonas.All[1].Email] = teams[0], // IT Support Agent -> Service Desk
            [DemoPersonas.All[2].Email] = teams[2], // Application Support Agent -> Application Support
        };

        foreach (var persona in DemoPersonas.All)
        {
            var isManager = persona.Role == UserRole.Manager;
            var team = personaTeams.GetValueOrDefault(persona.Email);
            var user = await CreateUserAsync(persona.DisplayName, persona.Email, persona.Role, isDemoProtected: true, cancellationToken);

            if (team is not null)
            {
                _dbContext.TeamMembers.Add(new TeamMember(team.Id, user.Id, isManager, DateTimeOffset.UtcNow));
                seeded.Add(new SeededUser(user.Id, persona.Role, team.Id, new CurrentUser(user.Id, persona.Role, new HashSet<int> { team.Id }, isManager ? new HashSet<int> { team.Id } : new HashSet<int>())));
            }
            else
            {
                // Executive Viewer: a member of every team (non-manager), matching an "executive,
                // read-only, cross-team analytics" persona — TicketAccessPolicy.CanView requires
                // team membership for a Viewer exactly like every other non-Admin role.
                var allTeamIds = teams.Select(t => t.Id).ToHashSet();
                foreach (var t in teams)
                {
                    _dbContext.TeamMembers.Add(new TeamMember(t.Id, user.Id, isTeamManager: false, DateTimeOffset.UtcNow));
                }

                seeded.Add(new SeededUser(user.Id, persona.Role, teams[0].Id, new CurrentUser(user.Id, persona.Role, allTeamIds, new HashSet<int>())));
            }
        }

        // One additional manager per remaining team (4 more managers; Service Desk's manager is
        // already the named persona above), then fill the rest with Agents so every team has
        // enough active assignees for ~600 tickets' worth of assignment/reassignment.
        var firstNames = new[] { "Alex", "Jordan", "Taylor", "Morgan", "Casey", "Riley", "Sam", "Drew", "Jamie", "Avery", "Quinn", "Reese", "Skyler", "Rowan", "Emerson", "Dakota", "Hayden", "Kendall", "Peyton", "Charlie" };
        var lastNames = new[] { "Bennett", "Osei", "Nguyen", "Farrell", "Kowalski", "Iyer", "Novak", "Reyes", "Larsen", "Okafor", "Duarte", "Whitfield", "Sato", "Mercer", "Delgado", "Voss", "Abara", "Lindqvist", "Marchetti", "Solberg" };
        var nameIndex = 0;
        string NextName() => $"{firstNames[nameIndex % firstNames.Length]} {lastNames[nameIndex++ % lastNames.Length]}";

        for (var teamIndex = 1; teamIndex < teams.Count; teamIndex++)
        {
            var team = teams[teamIndex];
            var name = NextName();
            var user = await CreateUserAsync(name, $"manager.{teamIndex}@demo.flowops.dev", UserRole.Manager, isDemoProtected: false, cancellationToken);
            _dbContext.TeamMembers.Add(new TeamMember(team.Id, user.Id, isTeamManager: true, DateTimeOffset.UtcNow));
            seeded.Add(new SeededUser(user.Id, UserRole.Manager, team.Id, new CurrentUser(user.Id, UserRole.Manager, new HashSet<int> { team.Id }, new HashSet<int> { team.Id })));
        }

        // Remaining headcount up to 25, distributed round-robin across teams as Agents, with two
        // final Viewers thrown in for role variety.
        var remaining = 25 - seeded.Count - 2;
        for (var i = 0; i < remaining; i++)
        {
            var team = teams[i % teams.Count];
            var name = NextName();
            var user = await CreateUserAsync(name, $"agent.{i}@demo.flowops.dev", UserRole.Agent, isDemoProtected: false, cancellationToken);
            _dbContext.TeamMembers.Add(new TeamMember(team.Id, user.Id, isTeamManager: false, DateTimeOffset.UtcNow));
            seeded.Add(new SeededUser(user.Id, UserRole.Agent, team.Id, new CurrentUser(user.Id, UserRole.Agent, new HashSet<int> { team.Id }, new HashSet<int>())));
        }

        for (var i = 0; i < 2; i++)
        {
            var team = teams[i % teams.Count];
            var name = NextName();
            var user = await CreateUserAsync(name, $"viewer.{i}@demo.flowops.dev", UserRole.Viewer, isDemoProtected: false, cancellationToken);
            _dbContext.TeamMembers.Add(new TeamMember(team.Id, user.Id, isTeamManager: false, DateTimeOffset.UtcNow));
            seeded.Add(new SeededUser(user.Id, UserRole.Viewer, team.Id, new CurrentUser(user.Id, UserRole.Viewer, new HashSet<int> { team.Id }, new HashSet<int>())));
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return seeded;
    }

    private async Task<ApplicationUser> CreateUserAsync(string displayName, string email, UserRole role, bool isDemoProtected, CancellationToken cancellationToken)
    {
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = displayName,
            IsActive = true,
            IsDemoProtected = isDemoProtected,
        };

        var createResult = await _userManager.CreateAsync(user, _options.PersonaPassword!);
        if (!createResult.Succeeded)
        {
            throw new InvalidOperationException(
                $"Failed to seed demo user {email}: {string.Join("; ", createResult.Errors.Select(e => e.Description))}");
        }

        var roleResult = await _userManager.AddToRoleAsync(user, role.ToString());
        if (!roleResult.Succeeded)
        {
            throw new InvalidOperationException(
                $"Failed to assign role {role} to demo user {email}: {string.Join("; ", roleResult.Errors.Select(e => e.Description))}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return user;
    }

    // ---- Tickets: signal showcase --------------------------------------------------------------

    /// <summary>
    /// CLAUDE.md §14: "Every signal in §9.1 has at least three examples." Deliberately explicit
    /// and backdated well past each threshold, rather than left to chance in the bulk generator
    /// below — a demo whose at-risk queue is empty on a bad RNG day is not a working demo.
    /// </summary>
    /// <remarks>
    /// The <c>Overdue</c> signal (driven by the optional <see cref="Ticket.DueDate"/> field, via
    /// <c>Ticket.ChangeDueDate</c>) is not demonstrated here: no <see cref="TicketService"/> method
    /// exposes that domain method to the Application layer today, and reaching into the aggregate
    /// directly from the seeder would bypass the same authorization/audit path every other
    /// mutation in this class goes through. Flagged as a known limitation, not silently skipped.
    /// </remarks>
    private async Task SeedSignalShowcaseTicketsAsync(
        TicketService ticketService,
        SeederClock clock,
        List<Team> teams,
        Dictionary<int, List<Category>> categories,
        List<SeededUser> users,
        DateTimeOffset seedTime,
        CancellationToken cancellationToken)
    {
        var team = teams[0];
        var category = categories[team.Id][0];
        var manager = users.First(u => u.TeamId == team.Id && u.Role == UserRole.Manager);
        var agents = users.Where(u => u.TeamId == team.Id && u.Role == UserRole.Agent).ToList();
        var requester = users.First(u => u.TeamId == team.Id);

        // UnassignedUrgent (needs > 15 minutes unassigned, Critical/High) — 4 examples.
        for (var i = 0; i < 4; i++)
        {
            await CreateTicketAtAsync(
                ticketService, clock, requester, team, category,
                $"Payroll export failing since last night ({i})", Priority.Critical, WorkType.Incident, null,
                seedTime.AddHours(-2));
        }

        // Aging (Low priority, created > 30 days ago, still open) — 4 examples.
        for (var i = 0; i < 4; i++)
        {
            await CreateTicketAtAsync(
                ticketService, clock, requester, team, category,
                $"Old low-priority request still open ({i})", Priority.Low, WorkType.ServiceRequest, null,
                seedTime.AddDays(-45));
        }

        // Stalled — Pending branch (PutOnHold > 3 days ago, never resumed) — 4 examples.
        for (var i = 0; i < 4; i++)
        {
            var (ticketId, _) = await CreateTicketAtAsync(
                ticketService, clock, requester, team, category,
                $"Waiting on vendor, stalled ({i})", Priority.Medium, WorkType.Incident, null,
                seedTime.AddDays(-10));
            var assignee = agents[i % agents.Count];
            await ticketService.AssignAsync(ticketId, assignee.Id, manager.AsCurrentUser, cancellationToken);
            await ticketService.PutOnHoldAsync(ticketId, "Awaiting vendor response", assignee.AsCurrentUser, cancellationToken);
        }

        // Stalled — InProgress branch (started > 5 days ago, no update since) — 4 examples.
        for (var i = 0; i < 4; i++)
        {
            var (ticketId, _) = await CreateTicketAtAsync(
                ticketService, clock, requester, team, category,
                $"In progress but gone quiet ({i})", Priority.Medium, WorkType.Incident, null,
                seedTime.AddDays(-9));
            var assignee = agents[i % agents.Count];
            await ticketService.AssignAsync(ticketId, assignee.Id, manager.AsCurrentUser, cancellationToken);
            await ticketService.StartWorkAsync(ticketId, assignee.AsCurrentUser, cancellationToken);
        }

        // Churn (reassigned >= 3 times) — 3 examples.
        for (var i = 0; i < 3; i++)
        {
            var (ticketId, _) = await CreateTicketAtAsync(
                ticketService, clock, requester, team, category,
                $"Bounced between owners ({i})", Priority.Medium, WorkType.Incident, null,
                seedTime.AddDays(-4));
            await ticketService.AssignAsync(ticketId, agents[0].Id, manager.AsCurrentUser, cancellationToken);
            await ticketService.ReassignAsync(ticketId, agents[1 % agents.Count].Id, manager.AsCurrentUser, cancellationToken);
            await ticketService.ReassignAsync(ticketId, agents[2 % agents.Count].Id, manager.AsCurrentUser, cancellationToken);
            await ticketService.ReassignAsync(ticketId, agents[0].Id, manager.AsCurrentUser, cancellationToken);
        }

        // Reopened (>= 1 reopen) — 3 examples.
        for (var i = 0; i < 3; i++)
        {
            var (ticketId, _) = await CreateTicketAtAsync(
                ticketService, clock, requester, team, category,
                $"Reopened after the fix didn't hold ({i})", Priority.High, WorkType.Incident, null,
                seedTime.AddDays(-6));
            var assignee = agents[i % agents.Count];
            await ticketService.AssignAsync(ticketId, assignee.Id, manager.AsCurrentUser, cancellationToken);
            await ticketService.StartWorkAsync(ticketId, assignee.AsCurrentUser, cancellationToken);
            await ticketService.ResolveAsync(ticketId, Resolution.Fixed, "Applied the standard fix.", assignee.AsCurrentUser, cancellationToken);
            await ticketService.ReopenAsync(ticketId, "Issue recurred within a day.", requester.AsCurrentUser, cancellationToken);
        }

        // SlaBreached (resolved-nothing, past due) and SlaAtRisk (well into the target window,
        // still open) — 3 examples each, using each priority's known seeded target minutes.
        for (var i = 0; i < 3; i++)
        {
            var priority = Priority.High;
            var targetMinutes = SeededSlaTargetMinutes[priority];
            await CreateTicketAtAsync(
                ticketService, clock, requester, team, category,
                $"Breached SLA, still unresolved ({i})", priority, WorkType.Incident, null,
                seedTime.AddMinutes(-(targetMinutes * 1.5)));
        }

        for (var i = 0; i < 3; i++)
        {
            var priority = Priority.Medium;
            var targetMinutes = SeededSlaTargetMinutes[priority];
            await CreateTicketAtAsync(
                ticketService, clock, requester, team, category,
                $"Deep into its SLA window ({i})", priority, WorkType.Incident, null,
                seedTime.AddMinutes(-(targetMinutes * 0.9)));
        }
    }

    // ---- Tickets: bulk realistic volume ---------------------------------------------------------

    private static readonly (Priority Priority, double Weight)[] PriorityWeights =
    [
        (Priority.Low, 0.30), (Priority.Medium, 0.40), (Priority.High, 0.20), (Priority.Critical, 0.10),
    ];

    private enum Outcome { OpenUnassigned, AssignedOnly, InProgress, Pending, ResolvedOnTime, ResolvedBreached, ClosedOnTime, ClosedBreached }

    private static readonly (Outcome Outcome, double Weight)[] OutcomeWeights =
    [
        (Outcome.OpenUnassigned, 0.10),
        (Outcome.AssignedOnly, 0.10),
        (Outcome.InProgress, 0.15),
        (Outcome.Pending, 0.05),
        (Outcome.ResolvedOnTime, 0.30), // resolved outcomes skew compliant -> ~80-88% overall SLA met
        (Outcome.ResolvedBreached, 0.08),
        (Outcome.ClosedOnTime, 0.18),
        (Outcome.ClosedBreached, 0.04),
    ];

    private async Task SeedBulkTicketsAsync(
        TicketService ticketService,
        SeederClock clock,
        List<Team> teams,
        Dictionary<int, List<Category>> categories,
        List<SeededUser> users,
        DateTimeOffset seedTime,
        Random rng,
        Volume volume,
        CancellationToken cancellationToken)
    {
        var commentsRemaining = volume.TargetCommentCount;

        for (var i = 0; i < volume.TicketCount; i++)
        {
            var team = WeightedPick(teams, TeamDefinitions.Select(t => t.Weight).ToArray(), rng);
            var category = categories[team.Id][rng.Next(categories[team.Id].Count)];
            var priority = WeightedPick(PriorityWeights.Select(p => p.Priority).ToArray(), PriorityWeights.Select(p => p.Weight).ToArray(), rng);
            var teamUsers = users.Where(u => u.TeamId == team.Id).ToList();
            // TicketAccessPolicy.CanCreate: Admin, Manager, and Agent may create tickets — Viewer
            // may not, so a Viewer can never be picked as a ticket's requester here.
            var eligibleRequesters = teamUsers.Where(u => u.Role != UserRole.Viewer).ToList();
            var requester = eligibleRequesters[rng.Next(eligibleRequesters.Count)];
            var agents = teamUsers.Where(u => u.Role is UserRole.Agent or UserRole.Manager).ToList();
            var manager = teamUsers.First(u => u.Role == UserRole.Manager);

            // Spread creation across the last 90 days so the dashboard's fixed reporting window
            // (Phase 10) has real, varied resolution-time/compliance data, weighted toward more
            // recent tickets so there is meaningful open work "now" as well as history.
            var ageDays = Math.Pow(rng.NextDouble(), 2) * 90;
            var createdAt = seedTime.AddDays(-ageDays);

            var (ticketId, _) = await CreateTicketAtAsync(
                ticketService, clock, requester, team, category,
                GenerateTitle(category.Name, rng), priority, category.DefaultWorkType, PickOrNull(rng, 8) ? rng.Next(1, ProjectNames.Length + 1) : null,
                createdAt);

            var outcome = WeightedPick(OutcomeWeights.Select(o => o.Outcome).ToArray(), OutcomeWeights.Select(o => o.Weight).ToArray(), rng);
            var targetMinutes = SeededSlaTargetMinutes[priority];
            var assignee = agents[rng.Next(agents.Count)];

            await ApplyOutcomeAsync(ticketService, clock, ticketId, outcome, createdAt, targetMinutes, requester, assignee, manager, rng, cancellationToken);

            // Distribute the remaining comment budget roughly evenly, biased toward tickets that
            // reached at least Assigned (an untouched Open ticket realistically has fewer notes).
            var commentBudgetForTicket = outcome == Outcome.OpenUnassigned ? rng.Next(0, 2) : rng.Next(1, 4);
            for (var c = 0; c < commentBudgetForTicket && commentsRemaining > 0; c++, commentsRemaining--)
            {
                clock.Set(createdAt.AddHours(rng.Next(1, 72)));
                var author = rng.NextDouble() < 0.5 ? requester : assignee;
                await ticketService.AddCommentAsync(ticketId, GenerateCommentBody(rng), isInternal: author.Id != requester.Id && rng.NextDouble() < 0.4, author.AsCurrentUser, cancellationToken);
            }
        }
    }

    private async Task ApplyOutcomeAsync(
        TicketService ticketService,
        SeederClock clock,
        int ticketId,
        Outcome outcome,
        DateTimeOffset createdAt,
        int targetMinutes,
        SeededUser requester,
        SeededUser assignee,
        SeededUser manager,
        Random rng,
        CancellationToken cancellationToken)
    {
        if (outcome == Outcome.OpenUnassigned)
        {
            return;
        }

        clock.Set(createdAt.AddMinutes(rng.Next(5, 60)));
        await ticketService.AssignAsync(ticketId, assignee.Id, manager.AsCurrentUser, cancellationToken);

        if (outcome == Outcome.AssignedOnly)
        {
            return;
        }

        clock.Set(createdAt.AddMinutes(rng.Next(60, 180)));
        await ticketService.StartWorkAsync(ticketId, assignee.AsCurrentUser, cancellationToken);

        if (outcome == Outcome.InProgress)
        {
            return;
        }

        if (outcome == Outcome.Pending)
        {
            clock.Set(createdAt.AddMinutes(rng.Next(180, 400)));
            await ticketService.PutOnHoldAsync(ticketId, "Waiting on additional information.", assignee.AsCurrentUser, cancellationToken);
            return;
        }

        var onTime = outcome is Outcome.ResolvedOnTime or Outcome.ClosedOnTime;
        var resolveOffsetMinutes = onTime
            ? (int)(targetMinutes * (0.4 + rng.NextDouble() * 0.4)) // 40-80% of target
            : (int)(targetMinutes * (1.1 + rng.NextDouble() * 0.5)); // 110-160% of target

        clock.Set(createdAt.AddMinutes(resolveOffsetMinutes));
        await ticketService.ResolveAsync(ticketId, PickResolution(rng), "Resolved during demo data generation.", assignee.AsCurrentUser, cancellationToken);

        if (outcome is Outcome.ClosedOnTime or Outcome.ClosedBreached)
        {
            clock.Set(clock.GetUtcNow().AddDays(rng.Next(1, 5)));
            await ticketService.CloseAsync(ticketId, requester.AsCurrentUser, cancellationToken);
        }
    }

    private static async Task<(int Id, string Reference)> CreateTicketAtAsync(
        TicketService ticketService,
        SeederClock clock,
        SeededUser requester,
        Team team,
        Category category,
        string title,
        Priority priority,
        WorkType workType,
        int? projectId,
        DateTimeOffset now)
    {
        clock.Set(now);

        var request = new CreateTicketRequest(title, GenerateDescription(title), workType, priority, team.Id, category.Id, projectId);
        return await ticketService.CreateAsync(request, requester.AsCurrentUser);
    }

    private static T WeightedPick<T>(IReadOnlyList<T> items, IReadOnlyList<double> weights, Random rng)
    {
        var total = weights.Sum();
        var roll = rng.NextDouble() * total;
        var cumulative = 0.0;
        for (var i = 0; i < items.Count; i++)
        {
            cumulative += weights[i];
            if (roll <= cumulative)
            {
                return items[i];
            }
        }

        return items[^1];
    }

    private static bool PickOrNull(Random rng, int oneInN) => rng.Next(oneInN) == 0;

    private static Resolution PickResolution(Random rng) =>
        (Resolution)rng.Next(Enum.GetValues<Resolution>().Length);

    private static string GenerateTitle(string categoryName, Random rng)
    {
        var subjects = new[] { "Login failure", "Slow performance", "Access request", "Hardware fault", "Configuration change", "Data discrepancy", "Printer offline", "VPN drops", "New starter setup", "License renewal", "Email delivery delay", "Report generation error" };
        return $"{subjects[rng.Next(subjects.Length)]} — {categoryName}";
    }

    private static string GenerateDescription(string title) =>
        $"{title}. Reported during demo data generation; describes a plausible, self-contained scenario with no real user data.";

    private static string GenerateCommentBody(Random rng)
    {
        var notes = new[]
        {
            "Investigating now.",
            "Confirmed with the requester, working on a fix.",
            "Escalated to the platform team for input.",
            "Applied a workaround while the root cause is investigated.",
            "Waiting on a response from the requester.",
            "Verified the fix in the affected environment.",
        };
        return notes[rng.Next(notes.Length)];
    }

    /// <summary>A settable clock so the seeder can back- and forward-date each ticket's own
    /// lifecycle deterministically — the same technique <c>FlowOpsWebApplicationFactory</c>'s
    /// <c>BackdatedTimeProvider</c> already uses for a single fixed moment, made mutable here since
    /// one seeder-owned <see cref="TicketService"/> plays out many tickets' full histories.</summary>
    private sealed class SeederClock(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _now = initial;

        public void Set(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
