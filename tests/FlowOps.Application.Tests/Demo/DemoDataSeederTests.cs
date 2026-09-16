using FlowOps.Application.Demo;
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
/// (<see cref="Persistence.PostgresFixture"/>): every other Application.Tests class creates
/// tickets in that same shared database, and <see cref="DemoDataSeeder"/>'s own idempotency check
/// ("skip if any ticket exists") would see those and silently no-op — exactly the false-early-
/// return <c>StartupMigrationTests</c> (Web.Tests) already avoids the same way, for the same
/// reason. Uses a small volume for speed; the production volume (600 tickets / 1500 comments) is
/// CLAUDE.md §14's own default and is exercised by <see cref="DemoDataSeeder.SeedAsync()"/>
/// (the parameterless overload), not re-run here.
/// </summary>
public sealed class DemoDataSeederTests : IAsyncLifetime
{
    private const string PersonaPassword = "Demo-Only-Passw0rd!1";
    private static readonly DemoDataSeeder.Volume SmallVolume = new(TicketCount: 12, TargetCommentCount: 20);

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
    public async Task SeedAsync_CreatesExpectedReferenceDataAndTickets()
    {
        await using var context = CreateContext();
        using var userManager = CreateUserManager(context);
        var seeder = new DemoDataSeeder(context, userManager, Options(), TimeProvider.System, NullLogger<DemoDataSeeder>.Instance);

        await seeder.SeedAsync(SmallVolume);

        Assert.Equal(1, await context.Organizations.CountAsync()); // Phase 16: exactly one demo org
        Assert.Equal(5, await context.Teams.CountAsync());
        Assert.Equal(8, await context.Projects.CountAsync());
        Assert.Equal(10, await context.Categories.CountAsync()); // 2 per team
        Assert.Equal(25, await context.Users.CountAsync());
        Assert.Equal(25, await context.OrganizationMemberships.CountAsync()); // one per user

        var organizationId = await context.Organizations.Select(o => o.Id).SingleAsync();
        Assert.True(await context.Teams.AllAsync(t => t.OrganizationId == organizationId));
        Assert.True(await context.Projects.AllAsync(p => p.OrganizationId == organizationId));
        Assert.True(await context.OrganizationMemberships.AllAsync(m => m.OrganizationId == organizationId));

        // Signal-showcase tickets (28, fixed) plus the requested bulk volume.
        Assert.Equal(28 + SmallVolume.TicketCount, await context.Tickets.CountAsync());
        Assert.True(await context.TicketComments.AnyAsync());

        await AssertAllTicketProjectReferencesExistAsync(context);

        // The four named personas exist, are demo-protected, and are members of at least one team.
        foreach (var persona in DemoPersonas.All)
        {
            var user = await userManager.FindByEmailAsync(persona.Email);
            Assert.NotNull(user);
            Assert.True(user.IsDemoProtected);
            Assert.True(await userManager.IsInRoleAsync(user, persona.Role.ToString()));
        }
    }

    [Fact]
    public async Task SeedAsync_CalledTwice_IsIdempotent()
    {
        await using var context = CreateContext();
        using var userManager = CreateUserManager(context);
        var seeder = new DemoDataSeeder(context, userManager, Options(), TimeProvider.System, NullLogger<DemoDataSeeder>.Instance);

        await seeder.SeedAsync(SmallVolume);
        var ticketCountAfterFirstRun = await context.Tickets.CountAsync();
        var userCountAfterFirstRun = await context.Users.CountAsync();

        await seeder.SeedAsync(SmallVolume);

        Assert.Equal(ticketCountAfterFirstRun, await context.Tickets.CountAsync());
        Assert.Equal(userCountAfterFirstRun, await context.Users.CountAsync());
    }

    [Fact] // CLAUDE.md §14: "Deterministic: fixed RNG seed, so the same data set is reproducible."
    public async Task SeedAsync_TwoIndependentDatabases_ProduceTheSameTicketTitles()
    {
        await using var contextA = CreateContext();
        using var userManagerA = CreateUserManager(contextA);
        await new DemoDataSeeder(contextA, userManagerA, Options(), TimeProvider.System, NullLogger<DemoDataSeeder>.Instance).SeedAsync(SmallVolume);

        await using var secondContainer = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("flowops_demo_seed_test_2").WithUsername("x").WithPassword("x").Build();
        await secondContainer.StartAsync();
        var optionsB = new DbContextOptionsBuilder<FlowOpsDbContext>().UseNpgsql(secondContainer.GetConnectionString()).UseSnakeCaseNamingConvention().Options;
        await using var contextB = new FlowOpsDbContext(optionsB);
        await contextB.Database.MigrateAsync();
        using var userManagerB = CreateUserManager(contextB);
        await new DemoDataSeeder(contextB, userManagerB, Options(), TimeProvider.System, NullLogger<DemoDataSeeder>.Instance).SeedAsync(SmallVolume);

        var titlesA = await contextA.Tickets.OrderBy(t => t.Id).Select(t => t.Title).ToListAsync();
        var titlesB = await contextB.Tickets.OrderBy(t => t.Id).Select(t => t.Title).ToListAsync();

        Assert.Equal(titlesA, titlesB);
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

        await Assert.ThrowsAsync<InvalidOperationException>(() => seeder.SeedAsync(SmallVolume));

        Assert.Equal(0, await context.Organizations.CountAsync());
        Assert.Equal(0, await context.Teams.CountAsync());
        Assert.Equal(0, await context.Tickets.CountAsync());
        Assert.Equal(0, await context.Users.CountAsync());
    }

    [Fact]
    // Reproduces the actual Render/Neon failure precondition directly, rather than relying on an
    // empty database's identity sequence happening to start at 1: Postgres sequences are never
    // rolled back, even by a failed/rolled-back transaction, so any earlier seed attempt (or any
    // other prior write to "projects") permanently advances "projects_id_seq" past whatever a
    // seeder might assume. This test forces that exact precondition deterministically — via a
    // real ALTER of the real sequence, not a guess — before seeding even starts, so a
    // positional-index-as-id regression would fail this test on every run, not by chance.
    public async Task SeedAsync_WhenProjectsIdentitySequenceAlreadyAdvanced_StillProducesValidProjectReferences()
    {
        await using var context = CreateContext();

        // Advance the real sequence well past the 8 projects the seeder is about to insert, so
        // those 8 rows get ids 1001-1008, not 1-8 — deterministic, not dependent on any prior
        // test or database state.
        await context.Database.ExecuteSqlRawAsync("SELECT setval('projects_id_seq', 1000, true);");

        using var userManager = CreateUserManager(context);
        var seeder = new DemoDataSeeder(context, userManager, Options(), TimeProvider.System, NullLogger<DemoDataSeeder>.Instance);

        await seeder.SeedAsync(SmallVolume);

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
