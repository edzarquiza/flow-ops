using FlowOps.Application.Demo;
using FlowOps.Domain.Attention;
using FlowOps.Domain.Planning;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Identity;
using FlowOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit;

namespace FlowOps.Application.Tests.Demo;

/// <summary>
/// CLAUDE.md §14. Deliberately does NOT use the shared "Postgres" collection
/// (<see cref="Persistence.PostgresFixture"/>): every other Application.Tests class creates data in
/// that same shared database, and exact-count assertions on the demo dataset (one org, 22 tickets,
/// ...) would see those rows. Each test class instance gets its own container.
/// </summary>
public sealed class DemoDataSeederTests : IAsyncLifetime
{
    private const string PersonaPassword = "Demo-Only-Passw0rd!1";

    private PostgreSqlContainer _container = null!;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("flowops_demo_seed_test")
            .WithUsername("flowops_demo_seed_test")
            .WithPassword("flowops_demo_seed_test")
            .Build();
        await _container.StartAsync();

        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    [Fact]
    public async Task SeedAsync_CreatesTheCuratedDataset()
    {
        await using var context = CreateContext();
        using var userManager = CreateUserManager(context);
        await NewSeeder(context, userManager).SeedAsync();

        Assert.Equal(1, await context.Organizations.CountAsync());
        Assert.Equal(2, await context.Teams.CountAsync());
        Assert.Equal(4, await context.Categories.CountAsync());
        Assert.Equal(2, await context.Projects.CountAsync());
        Assert.Equal(4, await context.Users.CountAsync());
        Assert.Equal(4, await context.OrganizationMemberships.CountAsync());
        Assert.Equal(3, await context.Sprints.CountAsync());
        Assert.Equal(22, await context.Tickets.CountAsync());

        // Hand-written, not generated: a handful of comments, none of the old filler titles.
        Assert.InRange(await context.TicketComments.CountAsync(), 1, 10);
        Assert.False(await context.Tickets.AnyAsync(t => t.Title.Contains("(0)") || t.Title.Contains("Test Ticket")));

        // One persona per role, all protected, pre-approved, members of the demo organization.
        var roles = new List<string>();
        foreach (var persona in DemoPersonas.All)
        {
            var user = await userManager.FindByEmailAsync(persona.Email);
            Assert.NotNull(user);
            Assert.True(user.IsDemoProtected);
            Assert.NotNull(user.RegistrationApprovedAt);
            Assert.True(await userManager.IsInRoleAsync(user, persona.Role.ToString()));
            roles.Add(persona.Role.ToString());
        }

        Assert.Equal(new[] { "Admin", "Agent", "Manager", "Viewer" }, roles.Order().ToArray());
        Assert.Equal(0, await context.Sprints.CountAsync(s => s.Status == SprintStatus.Cancelled));
    }

    [Fact]
    public async Task SeedAsync_ProjectsHoldTheDocumentedTicketDistribution()
    {
        await using var context = CreateContext();
        using var userManager = CreateUserManager(context);
        await NewSeeder(context, userManager).SeedAsync();

        var laptop = await context.Projects.SingleAsync(p => p.Name == "Laptop Refresh 2026");
        var billing = await context.Projects.SingleAsync(p => p.Name == "Billing Portal Stabilization");

        Assert.Equal(16, await context.Tickets.CountAsync(t => t.ProjectId == laptop.Id));
        Assert.Equal(4, await context.Tickets.CountAsync(t => t.ProjectId == billing.Id));
        Assert.Equal(2, await context.Tickets.CountAsync(t => t.ProjectId == null));
        Assert.Equal(0, await context.Sprints.CountAsync(s => s.ProjectId == billing.Id));
        await AssertAllTicketProjectReferencesExistAsync(context);
    }

    [Fact]
    public async Task SeedAsync_SprintHistoryIsRealSnapshotsAndAnExplicitCarryForward()
    {
        await using var context = CreateContext();
        using var userManager = CreateUserManager(context);
        await NewSeeder(context, userManager).SeedAsync();

        var sprints = await context.Sprints.OrderBy(s => s.StartDate).ToListAsync();
        Assert.Equal(new[] { SprintStatus.Completed, SprintStatus.Active, SprintStatus.Planned }, sprints.Select(s => s.Status).ToArray());
        var (completed, active, planned) = (sprints[0], sprints[1], sprints[2]);

        // Sprint 1 is frozen: 6 tickets, 4 done, 2 unfinished — read from the completion snapshot.
        var snapshots = await context.SprintTicketSnapshots.Where(x => x.SprintId == completed.Id).ToListAsync();
        Assert.Equal(6, snapshots.Count);
        Assert.Equal(4, snapshots.Count(x => x.WasDone));

        // The two unfinished tickets were explicitly carried into Sprint 2 (they belong to it now,
        // pulled onto the board), while the snapshot still records them as Sprint 1's.
        var unfinishedIds = snapshots.Where(x => !x.WasDone).Select(x => x.TicketId).ToList();
        var carried = await context.Tickets.Where(t => unfinishedIds.Contains(t.Id)).ToListAsync();
        Assert.Equal(2, carried.Count);
        Assert.All(carried, t => Assert.Equal(active.Id, t.SprintId));
        Assert.All(carried, t => Assert.False(t.SprintBacklog));

        // Sprint 2: 2 carried + 7 new = 9, spread over all five board columns.
        var s2 = await context.Tickets.Where(t => t.SprintId == active.Id).ToListAsync();
        Assert.Equal(9, s2.Count);
        Assert.Equal(2, s2.Count(t => t.SprintBacklog));
        var board = s2.Where(t => !t.SprintBacklog).ToList();
        Assert.Equal(2, board.Count(t => t.Status is Status.Open or Status.Assigned));
        Assert.Equal(2, board.Count(t => t.Status == Status.InProgress));
        Assert.Equal(1, board.Count(t => t.Status == Status.Pending));
        Assert.Equal(2, board.Count(t => t.Status is Status.Resolved or Status.Closed));

        Assert.Equal(3, await context.Tickets.CountAsync(t => t.SprintId == planned.Id));
        // Only the four finished tickets still belong to the completed sprint.
        Assert.Equal(4, await context.Tickets.CountAsync(t => t.SprintId == completed.Id));
    }

    [Fact] // Persistent signals must come from genuine timestamps, and stay true as time passes.
    public async Task SeedAsync_ProducesGenuinePersistentAttentionSignals()
    {
        await using var context = CreateContext();
        using var userManager = CreateUserManager(context);
        await NewSeeder(context, userManager).SeedAsync();

        var tickets = await context.Tickets.Include(t => t.Events).ToListAsync();
        var options = new AttentionOptions();

        // Long after seeding, only durable signals can remain — and they all do.
        var later = DateTimeOffset.UtcNow.AddDays(30);
        var laterCodes = tickets.SelectMany(t => AttentionPolicy.Evaluate(t, later, options, 80)).Select(x => x.Code).ToHashSet();
        foreach (var code in new[]
        {
            AttentionSignalCode.SlaBreached, AttentionSignalCode.Overdue, AttentionSignalCode.UnassignedUrgent,
            AttentionSignalCode.Aging, AttentionSignalCode.Stalled, AttentionSignalCode.Reopened,
        })
        {
            Assert.Contains(code, laterCodes);
        }

        // Right after seeding, the same holds without relying on the passage of time.
        var nowCodes = tickets.SelectMany(t => AttentionPolicy.Evaluate(t, DateTimeOffset.UtcNow, options, 80)).Select(x => x.Code).ToHashSet();
        foreach (var code in new[]
        {
            AttentionSignalCode.SlaBreached, AttentionSignalCode.Overdue, AttentionSignalCode.UnassignedUrgent,
            AttentionSignalCode.Aging, AttentionSignalCode.Stalled, AttentionSignalCode.Reopened,
        })
        {
            Assert.Contains(code, nowCodes);
        }

        Assert.Contains(tickets, t => t.PlannedStartDate != null && t.DueDate != null);
    }

    [Fact]
    public async Task SeedAsync_CalledTwice_IsIdempotent()
    {
        await using var context = CreateContext();
        using var userManager = CreateUserManager(context);
        var seeder = NewSeeder(context, userManager);

        await seeder.SeedAsync();
        var counts = await CountsAsync(context);

        await seeder.SeedAsync();

        Assert.Equal(counts, await CountsAsync(context));
    }

    [Fact] // The key is the demo organization, not "any ticket": an existing demo org is left exactly as it is.
    public async Task SeedAsync_WhenDemoOrganizationExists_ChangesNothing()
    {
        await using var context = CreateContext();
        using var userManager = CreateUserManager(context);
        context.Organizations.Add(new FlowOps.Domain.Organizations.Organization(0, DemoDataSeeder.OrganizationName, DateTimeOffset.UtcNow));
        await context.SaveChangesAsync();

        await NewSeeder(context, userManager).SeedAsync();

        Assert.Equal(1, await context.Organizations.CountAsync());
        Assert.Equal(0, await context.Tickets.CountAsync());
        Assert.Equal(0, await context.Users.CountAsync());
    }

    [Fact] // An unrelated organization (e.g. a real tenant) neither blocks seeding nor is modified by it.
    public async Task SeedAsync_WhenAnotherOrganizationExists_StillSeedsAndLeavesItAlone()
    {
        await using var context = CreateContext();
        using var userManager = CreateUserManager(context);
        context.Organizations.Add(new FlowOps.Domain.Organizations.Organization(0, "Someone Else", DateTimeOffset.UtcNow));
        await context.SaveChangesAsync();

        await NewSeeder(context, userManager).SeedAsync();

        Assert.Equal(2, await context.Organizations.CountAsync());
        Assert.Equal(22, await context.Tickets.CountAsync());
        Assert.Equal(1, await context.Organizations.CountAsync(o => o.Name == "Someone Else"));
    }

    [Fact] // CLAUDE.md §14: deterministic — same titles, statuses, and priorities every time.
    public async Task SeedAsync_TwoIndependentDatabases_ProduceTheSameDataset()
    {
        await using var contextA = CreateContext();
        using var userManagerA = CreateUserManager(contextA);
        await NewSeeder(contextA, userManagerA).SeedAsync();

        await using var secondContainer = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("flowops_demo_seed_test_2").WithUsername("x").WithPassword("x").Build();
        await secondContainer.StartAsync();
        var optionsB = new DbContextOptionsBuilder<FlowOpsDbContext>().UseNpgsql(secondContainer.GetConnectionString()).UseSnakeCaseNamingConvention().Options;
        await using var contextB = new FlowOpsDbContext(optionsB);
        await contextB.Database.MigrateAsync();
        using var userManagerB = CreateUserManager(contextB);
        await NewSeeder(contextB, userManagerB).SeedAsync();

        static async Task<List<string>> Shape(FlowOpsDbContext c) =>
            (await c.Tickets.OrderBy(t => t.Id).Select(t => new { t.Title, t.Status, t.Priority, t.SprintBacklog }).ToListAsync())
                .Select(t => $"{t.Title}|{t.Status}|{t.Priority}|{t.SprintBacklog}").ToList();

        Assert.Equal(await Shape(contextA), await Shape(contextB));
    }

    [Fact] // CLAUDE.md §14: "inside a transaction" — a mid-seed failure must leave nothing behind.
    public async Task SeedAsync_FailureMidSeed_RollsBackCompletely()
    {
        await using var context = CreateContext();
        using var userManager = CreateUserManager(context);
        // A password that fails Identity's default policy makes the very first demo-user creation
        // throw, deep inside the seed — proving the whole operation, including the teams/
        // categories/projects already added before it, rolls back rather than leaving partial data.
        var seeder = new DemoDataSeeder(context, userManager, Options(personaPassword: "short"), TimeProvider.System, NullLogger<DemoDataSeeder>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => seeder.SeedAsync());

        Assert.Equal(0, await context.Organizations.CountAsync());
        Assert.Equal(0, await context.Teams.CountAsync());
        Assert.Equal(0, await context.Tickets.CountAsync());
        Assert.Equal(0, await context.Users.CountAsync());
    }

    [Fact]
    // Postgres sequences are never rolled back, so any earlier seed attempt (or any other prior write
    // to "projects") permanently advances "projects_id_seq". This forces that exact precondition
    // deterministically — via a real ALTER of the real sequence — so a positional-index-as-id
    // regression would fail on every run, not by chance.
    public async Task SeedAsync_WhenProjectsIdentitySequenceAlreadyAdvanced_StillProducesValidProjectReferences()
    {
        await using var context = CreateContext();
        await context.Database.ExecuteSqlRawAsync("SELECT setval('projects_id_seq', 1000, true);");

        using var userManager = CreateUserManager(context);
        await NewSeeder(context, userManager).SeedAsync();

        var projectIds = await context.Projects.Select(p => p.Id).ToListAsync();
        Assert.All(projectIds, id => Assert.True(id > 1000, $"expected an id past the advanced sequence, got {id}"));

        await AssertAllTicketProjectReferencesExistAsync(context);
    }

    /// <summary>The exact invariant a fabricated (rather than persisted) project id would violate:
    /// every ticket that has a project at all must reference one that genuinely exists.</summary>
    private static async Task AssertAllTicketProjectReferencesExistAsync(FlowOpsDbContext context)
    {
        var projectIds = (await context.Projects.Select(p => p.Id).ToListAsync()).ToHashSet();
        var ticketProjectIds = await context.Tickets
            .Where(t => t.ProjectId != null)
            .Select(t => t.ProjectId!.Value)
            .ToListAsync();

        Assert.NotEmpty(ticketProjectIds); // otherwise this assertion would be vacuous
        Assert.All(ticketProjectIds, id => Assert.Contains(id, projectIds));
    }

    private static DemoDataSeeder NewSeeder(FlowOpsDbContext context, UserManager<ApplicationUser> userManager) =>
        new(context, userManager, Options(), TimeProvider.System, NullLogger<DemoDataSeeder>.Instance);

    private static async Task<int[]> CountsAsync(FlowOpsDbContext c) =>
    [
        await c.Organizations.CountAsync(), await c.Users.CountAsync(), await c.Teams.CountAsync(),
        await c.Categories.CountAsync(), await c.Projects.CountAsync(), await c.Sprints.CountAsync(),
        await c.Tickets.CountAsync(), await c.TicketComments.CountAsync(), await c.SprintTicketSnapshots.CountAsync(),
        await c.TeamMembers.CountAsync(), await c.OrganizationMemberships.CountAsync(),
    ];

    private static DemoOptions Options(string personaPassword = PersonaPassword) => new() { Enabled = true, PersonaPassword = personaPassword };

    private FlowOpsDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<FlowOpsDbContext>().UseNpgsql(_container.GetConnectionString()).UseSnakeCaseNamingConvention().Options);

    private static UserManager<ApplicationUser> CreateUserManager(FlowOpsDbContext context)
    {
        var services = new ServiceCollection();
        services.AddSingleton(context);
        services.AddLogging();
        services
            .AddIdentityCore<ApplicationUser>()
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<FlowOpsDbContext>();

        return services.BuildServiceProvider().GetRequiredService<UserManager<ApplicationUser>>();
    }
}
