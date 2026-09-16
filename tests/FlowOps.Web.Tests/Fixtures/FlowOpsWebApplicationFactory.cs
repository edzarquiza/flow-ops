using FlowOps.Application.Tickets;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace FlowOps.Web.Tests.Fixtures;

/// <summary>
/// A real PostgreSQL-backed <see cref="WebApplicationFactory{TEntryPoint}"/> for the
/// login/logout/authorization tests that "do not stub the auth handler" (CLAUDE.md §15) — sign-in
/// genuinely queries Identity's tables in a real database. Requires Docker; if unavailable,
/// <see cref="InitializeAsync"/> fails with a clear <c>DockerUnavailableException</c> rather than
/// silently skipping or faking PostgreSQL-backed authentication.
/// </summary>
public sealed class FlowOpsWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("flowops_webtest")
        .WithUsername("flowops_webtest")
        .WithPassword("flowops_webtest")
        .Build();

    /// <summary>The organization every seeded test user belongs to (Phase 16).</summary>
    public int OrganizationId { get; private set; }

    /// <summary>The team Manager and Agent belong to. Admin and Viewer deliberately do not.</summary>
    public int TeamId { get; private set; }

    /// <summary>A category belonging to <see cref="TeamId"/>, so ticket creation satisfies TICKET-INV-02.</summary>
    public int CategoryId { get; private set; }

    /// <summary>A ticket on <see cref="TeamId"/>, seeded through the real application service so
    /// authorization tests have something to be allowed or denied without an HTTP round trip.</summary>
    public int SeededTicketId { get; private set; }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Accessing Services triggers host construction, using the connection string configured
        // in ConfigureWebHost below (now that the container is running and its port is known).
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
        await db.Database.MigrateAsync();
        await TestUsers.SeedAsync(scope.ServiceProvider);

        var reference = await TestReferenceData.SeedAsync(scope.ServiceProvider);
        OrganizationId = reference.OrganizationId;
        TeamId = reference.TeamId;
        CategoryId = reference.CategoryId;
        SeededTicketId = reference.TicketId;
    }

    /// <summary>
    /// Creates a fresh Open ticket on <see cref="TeamId"/> with the named user as requester, so a
    /// workflow test can transition it without depending on — or disturbing — another test's
    /// ticket. Goes through the real application service, not a raw insert.
    /// </summary>
    public async Task<int> CreateTicketAsync(string requesterEmail, string title)
    {
        using var scope = Services.CreateScope();
        var requesterId = await TestReferenceData.UserIdAsync(scope.ServiceProvider, requesterEmail);
        var ticketService = scope.ServiceProvider.GetRequiredService<TicketService>();

        var (ticketId, _) = await ticketService.CreateAsync(
            new CreateTicketRequest(
                Title: title,
                Description: "Seeded by a Phase 6 workflow test.",
                WorkType: WorkType.Incident,
                Priority: Priority.Medium,
                TeamId: TeamId,
                CategoryId: CategoryId,
                ProjectId: null),
            new CurrentUser(requesterId, OrganizationId, UserRole.Agent, new HashSet<int> { TeamId }, new HashSet<int>()));

        return ticketId;
    }

    /// <summary>
    /// Creates a ticket that genuinely needs attention: Critical, unassigned, and back-dated far
    /// enough to have breached its SLA and tripped UnassignedUrgent. Back-dating is done with a
    /// fixed clock through the real service, so the ticket's whole SLA cycle is internally
    /// consistent rather than patched after the fact.
    /// </summary>
    public async Task<int> CreateAtRiskTicketAsync(string title)
    {
        using var scope = Services.CreateScope();
        var requesterId = await TestReferenceData.UserIdAsync(scope.ServiceProvider, TestUsers.ManagerEmail);
        var db = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();

        var longAgo = TimeProvider.System.GetUtcNow().AddDays(-3);
        var ticketService = new TicketService(db, new BackdatedTimeProvider(longAgo));

        var (ticketId, _) = await ticketService.CreateAsync(
            new CreateTicketRequest(
                Title: title,
                Description: "Seeded by a Phase 8 at-risk test.",
                WorkType: WorkType.Incident,
                Priority: Priority.Critical,
                TeamId: TeamId,
                CategoryId: CategoryId,
                ProjectId: null),
            new CurrentUser(requesterId, OrganizationId, UserRole.Agent, new HashSet<int> { TeamId }, new HashSet<int>()));

        return ticketId;
    }

    private sealed class BackdatedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // UseSetting, not ConfigureAppConfiguration: an IWebHostBuilder configuration callback
        // runs before WebApplication.CreateBuilder layers appsettings.Development.json on top, so
        // an in-memory ConnectionStrings:FlowOps here loses to the file and the tests silently run
        // against the developer's real local database instead of this container. UseSetting is
        // applied to the host's own configuration and survives that layering.
        builder.UseSetting("ConnectionStrings:FlowOps", _container.GetConnectionString());

        // Phase 24A: registration no longer auto-signs-in (approval is required first), so a Web
        // test fixture that needs an authenticated session now needs one extra, genuine
        // /Account/Login POST per registered account on top of the registration itself. Every
        // request this in-process TestServer handles shares one synthetic remote IP, so without
        // this the production 5/min/IP login limit (CLAUDE.md §12, unchanged in Program.cs's own
        // default) would throttle test setup, not a real attacker. No appsettings.*.json file sets
        // this key, so a real deployment is entirely unaffected.
        builder.UseSetting("RateLimiting:Login:PermitLimitPerMinute", "1000");
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _container.DisposeAsync();
        await base.DisposeAsync();
    }
}
