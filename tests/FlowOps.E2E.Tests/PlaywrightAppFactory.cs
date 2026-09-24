using FlowOps.Application.Tickets;
using FlowOps.Domain.Catalog;
using FlowOps.Domain.Directory;
using FlowOps.Domain.Organizations;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Identity;
using FlowOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Xunit;

namespace FlowOps.E2E.Tests;

/// <summary>
/// Same real-PostgreSQL-via-Testcontainers pattern <c>FlowOps.Web.Tests</c>' own
/// <c>FlowOpsWebApplicationFactory</c> uses, with one difference a real browser needs:
/// <see cref="CreateHost"/> is overridden to start a genuine Kestrel listener on a real loopback
/// port instead of the in-process <c>TestServer</c>, because Playwright drives an actual browser
/// over the network — it cannot talk to an in-memory host. Everything else (migrate, seed via the
/// real application services, the login-rate-limit relaxation) mirrors that fixture exactly, kept
/// as its own small copy here rather than a cross-project reference, since both fixtures' internal
/// seed helpers are deliberately not part of either project's public surface.
/// </summary>
public sealed class PlaywrightAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string Password = "Test-Only-Passw0rd!1";
    public const string AdminEmail = "e2e.admin@test.flowops.local";
    public const string ManagerEmail = "e2e.manager@test.flowops.local";
    public const string AgentEmail = "e2e.agent@test.flowops.local";
    public const string ViewerEmail = "e2e.viewer@test.flowops.local";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("flowops_e2e")
        .WithUsername("flowops_e2e")
        .WithPassword("flowops_e2e")
        .Build();

    /// <summary>The real base address of the running Kestrel host, e.g. "http://127.0.0.1:53214".</summary>
    public string BaseAddress { get; private set; } = string.Empty;

    public int TeamId { get; private set; }

    public int CategoryId { get; private set; }

    /// <summary>An inactive team, so the "Show inactive" tests have something to find.</summary>
    public int InactiveTeamId { get; private set; }

    public string InactiveTeamName { get; private set; } = string.Empty;

    public int SeededTicketId { get; private set; }

    /// <summary>The seeded "Reference Project", for Phase 29D's sidebar navigation E2E tests.</summary>
    public int ProjectId { get; private set; }

    /// <summary>The real host's service provider — for a test that needs to seed its own extra
    /// data (e.g. a backdated at-risk ticket) directly through the application services, the same
    /// way <c>FlowOpsWebApplicationFactory.CreateAtRiskTicketAsync</c> does for Web.Tests. Named
    /// distinctly from the base class's own <c>Services</c> (which throws — see this class's own
    /// remarks on why <c>_host</c> must be used directly instead).</summary>
    public IServiceProvider HostServices => _host.Services;

    /// <summary>
    /// The real, running host — set inside <see cref="CreateHost"/>. <see cref="WebApplicationFactory{TEntryPoint}"/>'s
    /// own <c>Server</c>/<c>Services</c>/<c>CreateClient()</c> shortcuts assume the default in-process
    /// <c>TestServer</c> and hard-cast to it internally; once <see cref="CreateHost"/> is overridden to
    /// build a real Kestrel host instead (required for a real browser to reach it), those shortcuts throw
    /// <see cref="InvalidCastException"/>. This field is the supported way around that — everything below
    /// goes through it directly instead of through the base class.
    /// </summary>
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // The only way to make the base class actually invoke the CreateHost override below is to
        // touch one of its own TestServer-oriented conveniences (Server/Services/CreateClient) — all
        // three fall through the same internal EnsureServer() path. By the time that path reaches its
        // own bookkeeping (caching the IServer as a TestServer, which our real Kestrel server is not,
        // hence the InvalidCastException swallowed below), CreateHost has already run to completion:
        // _host is a real, listening Kestrel host. Everything after this point uses that field
        // directly — never the base class's Server/Services/CreateClient again.
        try
        {
            using var warmUp = CreateClient();
        }
        catch (InvalidCastException)
        {
        }

        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        await db.Database.MigrateAsync();
        await SeedAsync(scope.ServiceProvider, db);

        var server = _host.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("The Kestrel host has no IServerAddressesFeature.");
        BaseAddress = addresses.Addresses.First();
    }

    private async Task SeedAsync(IServiceProvider services, FlowOpsDbContext db)
    {
        var organization = new Organization(0, $"E2E Org {Guid.NewGuid():N}", DateTimeOffset.UtcNow);
        db.Add(organization);
        await db.SaveChangesAsync();

        var team = new Team(0, organization.Id, "Service Desk", DateTimeOffset.UtcNow);
        var inactiveTeam = new Team(0, organization.Id, $"Retired Team {Guid.NewGuid():N}", DateTimeOffset.UtcNow, isActive: false);
        db.AddRange(team, inactiveTeam);
        await db.SaveChangesAsync();

        var category = new Category(0, team.Id, "Printers", WorkType.Incident, DateTimeOffset.UtcNow);
        db.Add(category);
        var project = new Project(0, organization.Id, "Reference Project", DateTimeOffset.UtcNow); // keeps the workspace-setup checklist satisfied
        db.Add(project);
        await db.SaveChangesAsync();

        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var adminId = await CreateUserAsync(userManager, AdminEmail, WellKnownRoles.Admin);
        var managerId = await CreateUserAsync(userManager, ManagerEmail, WellKnownRoles.Manager);
        var agentId = await CreateUserAsync(userManager, AgentEmail, WellKnownRoles.Agent);
        var viewerId = await CreateUserAsync(userManager, ViewerEmail, WellKnownRoles.Viewer);

        db.Add(new OrganizationMembership(0, organization.Id, adminId, UserRole.Admin, DateTimeOffset.UtcNow));
        db.Add(new OrganizationMembership(0, organization.Id, managerId, UserRole.Manager, DateTimeOffset.UtcNow));
        db.Add(new OrganizationMembership(0, organization.Id, agentId, UserRole.Agent, DateTimeOffset.UtcNow));
        db.Add(new OrganizationMembership(0, organization.Id, viewerId, UserRole.Viewer, DateTimeOffset.UtcNow));
        db.Add(new TeamMember(team.Id, managerId, isTeamManager: true, DateTimeOffset.UtcNow));
        db.Add(new TeamMember(team.Id, agentId, isTeamManager: false, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();

        var ticketService = services.GetRequiredService<TicketService>();
        var (ticketId, _) = await ticketService.CreateAsync(
            new CreateTicketRequest("VPN client will not connect", "Reported by a Phase 30A E2E fixture.", WorkType.Incident, Priority.High, team.Id, category.Id, null),
            new CurrentUser(agentId, organization.Id, UserRole.Agent, new HashSet<int> { team.Id }, new HashSet<int>()));

        TeamId = team.Id;
        CategoryId = category.Id;
        InactiveTeamId = inactiveTeam.Id;
        InactiveTeamName = inactiveTeam.Name;
        SeededTicketId = ticketId;
        ProjectId = project.Id;
    }

    private static async Task<Guid> CreateUserAsync(UserManager<ApplicationUser> userManager, string email, string role)
    {
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = email,
            IsActive = true,
            RegistrationApprovedAt = DateTimeOffset.UtcNow,
        };

        var createResult = await userManager.CreateAsync(user, Password);
        if (!createResult.Succeeded)
        {
            throw new InvalidOperationException($"Failed to seed {email}: {string.Join("; ", createResult.Errors.Select(e => e.Description))}");
        }

        var roleResult = await userManager.AddToRoleAsync(user, role);
        if (!roleResult.Succeeded)
        {
            throw new InvalidOperationException($"Failed to assign {role} to {email}: {string.Join("; ", roleResult.Errors.Select(e => e.Description))}");
        }

        return user.Id;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseKestrel();
        builder.UseUrls("http://127.0.0.1:0"); // OS-assigned free port — real, not in-process
        builder.UseSetting("ConnectionStrings:FlowOps", _container.GetConnectionString());
        // Every request in this test run shares one machine's loopback address; without this the
        // production 5/min/IP login limit throttles test setup, not a real attacker (same reasoning
        // as FlowOpsWebApplicationFactory's identical setting).
        builder.UseSetting("RateLimiting:Login:PermitLimitPerMinute", "1000");
    }

    /// <summary>
    /// The one real difference from the base class: build and start the host as an actual running
    /// Kestrel server (so Playwright's browser can reach it over real sockets) instead of wrapping it
    /// in <c>TestServer</c>. This is the documented pattern for driving a <c>WebApplicationFactory</c>
    /// app with a real browser rather than the in-process HttpClient every other Web test uses.
    /// </summary>
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureWebHost(webBuilder => webBuilder.UseKestrel());
        var host = builder.Build();
        host.Start();
        _host = host;
        return host;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        await _container.DisposeAsync();
        // Not base.DisposeAsync(): it disposes the base class's own cached Server/Client, neither of
        // which was ever successfully created (see InitializeAsync) — disposing the real host above
        // is the actual cleanup needed.
    }
}
