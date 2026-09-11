using System.Diagnostics;
using FlowOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit;

namespace FlowOps.Web.Tests;

/// <summary>
/// Phase 15 / ADR-0014: the `init-database` standalone command Render's Pre-Deploy Command uses.
/// Two tiers, per the task's own guidance ("prefer testing the extracted logic directly; use a
/// subprocess only where it proves something nothing else can"):
///
/// - <see cref="Program.InitializeDatabaseAsync"/> is called directly (no subprocess) to prove the
///   actual migrate/seed behavior against real Testcontainers PostgreSQL — the same technique
///   <c>StartupMigrationTests</c> already uses for the migration half alone.
/// - A real subprocess (`dotnet FlowOps.Web.dll init-database`) is used only to prove the one thing
///   a direct method call cannot: that the compiled command-line entry point actually exits on its
///   own rather than falling through to <c>app.Run()</c>, which would block forever. A process that
///   returns within a bounded time, with the expected exit code, is exactly evidence of that.
/// </summary>
public sealed class InitializeDatabaseCommandTests : IAsyncLifetime
{
    private static readonly string WebDllPath = Path.Combine(AppContext.BaseDirectory, "FlowOps.Web.dll");

    private PostgreSqlContainer _container = null!;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("flowops_init_db_test")
            .WithUsername("flowops_init_db_test")
            .WithPassword("flowops_init_db_test")
            .Build();
        await _container.StartAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    [Fact] // Requirement B: migrate + seed (when enabled) + return success, against real Postgres.
    public async Task InitializeDatabaseAsync_DemoEnabled_MigratesAndSeedsFreshDatabase()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:FlowOps", _container.GetConnectionString());
            builder.UseSetting("FlowOps:Database:ApplyMigrationsOnStartup", "true");
            builder.UseSetting("FlowOps:Demo:Enabled", "true");
            builder.UseSetting("FlowOps:Demo:PersonaPassword", "Init-Command-Passw0rd!1");
        });

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();

        // Not through the pipeline/app.Run() — the method under test, directly, exactly as both
        // the standalone command and the normal startup fallback call it.
        await Program.InitializeDatabaseAsync(factory.Services, factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(), NullLogger.Instance);

        Assert.NotEmpty(await dbContext.Database.GetAppliedMigrationsAsync());
        Assert.True(await dbContext.Teams.AnyAsync());
        Assert.True(await dbContext.Tickets.AnyAsync());
    }

    [Fact] // Migrations still apply when the demo flag is off — the fallback path's other half.
    public async Task InitializeDatabaseAsync_DemoDisabled_MigratesOnlyAndSeedsNothing()
    {
        await using var secondContainer = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("flowops_init_db_test_2").WithUsername("x").WithPassword("x").Build();
        await secondContainer.StartAsync();

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:FlowOps", secondContainer.GetConnectionString());
            builder.UseSetting("FlowOps:Database:ApplyMigrationsOnStartup", "true");
            // FlowOps:Demo:Enabled deliberately left unset.
        });

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();

        await Program.InitializeDatabaseAsync(factory.Services, factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(), NullLogger.Instance);

        Assert.NotEmpty(await dbContext.Database.GetAppliedMigrationsAsync());
        Assert.False(await dbContext.Teams.AnyAsync());
    }

    [Fact] // Requirements 1, 4, 6: builds services, migrates, exits 0, never binds the HTTP port.
    public async Task InitDatabaseCommand_Subprocess_ExitsZero_AndNeverStartsServing()
    {
        using var process = StartInitDatabaseProcess(_container.GetConnectionString(), demoEnabled: false);

        // A process that took the init-database branch returns on its own; one that fell through
        // to app.Run() would block indefinitely (nothing here ever sends it a shutdown signal) —
        // so a bounded, self-terminating exit is itself the proof, not just the exit code.
        var exited = process.WaitForExit(TimeSpan.FromSeconds(60));
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();

        Assert.True(exited, $"Process did not exit within 60s — it may have fallen through to app.Run(). Stdout: {output}\nStderr: {error}");
        Assert.Equal(0, process.ExitCode);
    }

    [Fact] // Requirement C / 5: a failed initialization is non-zero and does not start serving.
    public async Task InitDatabaseCommand_Subprocess_ExitsNonZero_OnUnreachableDatabase()
    {
        // A syntactically valid but unreachable connection string — the process should fail during
        // MigrateAsync's connection attempt, not hang.
        const string unreachableConnectionString = "Host=127.0.0.1;Port=1;Database=flowops_unreachable;Username=x;Password=x;Timeout=5;Command Timeout=5";
        using var process = StartInitDatabaseProcess(unreachableConnectionString, demoEnabled: false);

        var exited = process.WaitForExit(TimeSpan.FromSeconds(60));
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();

        Assert.True(exited, $"Process did not exit within 60s. Stdout: {output}\nStderr: {error}");
        Assert.NotEqual(0, process.ExitCode);
    }

    private static Process StartInitDatabaseProcess(string connectionString, bool demoEnabled)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList = { WebDllPath, "init-database" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["ConnectionStrings__FlowOps"] = connectionString;
        startInfo.Environment["FlowOps__Database__ApplyMigrationsOnStartup"] = "true";
        startInfo.Environment["FlowOps__Demo__Enabled"] = demoEnabled ? "true" : "false";

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start FlowOps.Web.dll process.");
    }
}
