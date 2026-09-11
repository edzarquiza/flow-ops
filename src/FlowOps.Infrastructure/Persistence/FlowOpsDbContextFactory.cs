using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FlowOps.Infrastructure.Persistence;

/// <summary>
/// Design-time-only factory so <c>dotnet ef migrations</c> can construct
/// <see cref="FlowOpsDbContext"/> without a startup project wiring up DI yet (Web's composition
/// root does not configure the DbContext until a later phase). Never used at runtime — the real
/// app registers <see cref="FlowOpsDbContext"/> through <c>AddDbContext</c> in the Web
/// composition root instead.
///
/// The connection string must name the local development database from <c>docker-compose.yml</c>,
/// and must state the port explicitly. Only <c>migrations add</c> works without reaching a
/// database; <c>database update</c>, <c>migrations list</c>, and
/// <c>has-pending-model-changes</c> all connect. Omitting the port silently defaults Npgsql to
/// 5432, so those commands would target whatever unrelated PostgreSQL instance happens to occupy
/// the default port rather than FlowOps's container on 5433 (CLAUDE.md §17).
///
/// These are the development-only credentials from <c>docker-compose.yml</c>, deliberately
/// duplicated here and never reused anywhere else (CLAUDE.md §17). They are not read from
/// <c>appsettings.Development.json</c> because that file belongs to FlowOps.Web: consuming it
/// would need a new Microsoft.Extensions.Configuration package reference in this project
/// (CLAUDE.md §2 rule 5 — no new package without an ADR) and would couple Infrastructure to the
/// Web project's file layout. If design-time ever needs to target a non-default database, that
/// is a `--connection` argument on the command, not a change here.
/// </summary>
public sealed class FlowOpsDbContextFactory : IDesignTimeDbContextFactory<FlowOpsDbContext>
{
    public FlowOpsDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<FlowOpsDbContext>();
        optionsBuilder
            .UseNpgsql("Host=localhost;Port=5433;Database=flowops_dev;Username=flowops;Password=flowops_dev_password")
            .UseSnakeCaseNamingConvention();

        return new FlowOpsDbContext(optionsBuilder.Options);
    }
}
