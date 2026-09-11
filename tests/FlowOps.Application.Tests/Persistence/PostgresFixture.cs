using FlowOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;
using Xunit;

namespace FlowOps.Application.Tests.Persistence;

/// <summary>
/// One real PostgreSQL container per test collection (CLAUDE.md §15 — Testcontainers, no
/// in-memory/SQLite provider). Requires Docker to be running; if Docker is unavailable, tests in
/// the "Postgres" collection fail at <see cref="InitializeAsync"/> rather than silently skipping
/// or faking Postgres-specific behavior (CHECK constraints, FKs, xmin concurrency).
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("flowops_test")
            .WithUsername("flowops_test")
            .WithPassword("flowops_test")
            .Build();

        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();

        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <param name="logSql">When supplied, receives the SQL EF Core actually generates. Used by
    /// the AUTH-RULE-05 test to prove the visibility scope reaches the database as a WHERE clause
    /// instead of being applied after materialisation.</param>
    public FlowOpsDbContext CreateContext(Action<string>? logSql = null)
    {
        var builder = new DbContextOptionsBuilder<FlowOpsDbContext>()
            .UseNpgsql(ConnectionString)
            .UseSnakeCaseNamingConvention();

        if (logSql is not null)
        {
            builder.LogTo(logSql, [DbLoggerCategory.Database.Command.Name], LogLevel.Information);
        }

        return new FlowOpsDbContext(builder.Options);
    }
}

[CollectionDefinition("Postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
