using FlowOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace FlowOps.Web.Tests;

/// <summary>
/// Phase 13: proves the flag-gated startup migration (<c>Program.cs</c>, ADR-0007) at the ASP.NET
/// Core level — that <c>FlowOps:Database:ApplyMigrationsOnStartup</c> genuinely creates the schema
/// on a database that has never been migrated, and that leaving it unset does not. This is the one
/// behavior a Docker Compose stack actually depends on: a fresh Postgres volume plus
/// <c>FlowOps__Database__ApplyMigrationsOnStartup=true</c> must leave the application with a usable
/// schema, with no separate <c>dotnet ef database update</c> step. The real containerized
/// validation (building the image, running Compose, inspecting the live database) is what proves
/// this for Docker itself and is not a substitute for, or substituted by, this test.
/// </summary>
/// <remarks>
/// Deliberately does not use <see cref="FlowOps.Web.Tests.Fixtures.FlowOpsWebApplicationFactory"/>:
/// that fixture always calls <c>MigrateAsync()</c> itself in <c>InitializeAsync</c>, which would
/// mask exactly the behavior under test. This class provisions its own container and, critically,
/// never migrates it — the application's own startup path is the only thing that may.
/// </remarks>
public sealed class StartupMigrationTests : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("flowops_migration_test")
            .WithUsername("flowops_migration_test")
            .WithPassword("flowops_migration_test")
            .Build();
        await _container.StartAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    [Fact]
    public async Task ApplyMigrationsOnStartup_True_CreatesSchemaOnAFreshDatabase()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // Development, not Production: this test is about the migration flag in isolation, not
            // about also standing up database-backed Data Protection — that combination is
            // exercised for real by the Docker Compose validation, not duplicated here.
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:FlowOps", _container.GetConnectionString());
            builder.UseSetting("FlowOps:Database:ApplyMigrationsOnStartup", "true");
        });

        // Accessing Services triggers host construction, including Program.cs's startup migration
        // block, before any request is made or any test code touches the database directly.
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();

        var appliedMigrations = await dbContext.Database.GetAppliedMigrationsAsync();

        Assert.NotEmpty(appliedMigrations);
        Assert.True(await dbContext.Database.CanConnectAsync());
    }

    [Fact]
    public async Task ApplyMigrationsOnStartup_NotSet_LeavesSchemaUnmigrated()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:FlowOps", _container.GetConnectionString());
            // FlowOps:Database:ApplyMigrationsOnStartup deliberately left unset — must default to
            // disabled, per the existing contract ("absent = false").
        });

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();

        var appliedMigrations = await dbContext.Database.GetAppliedMigrationsAsync();

        Assert.Empty(appliedMigrations);
    }
}
