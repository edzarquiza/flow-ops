using FlowOps.Application.Accounts;
using FlowOps.Application.Tests.Persistence;
using FlowOps.Application.Tests.Tickets;
using FlowOps.Domain.Accounts;
using FlowOps.Infrastructure.Identity;
using FlowOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace FlowOps.Application.Tests.Accounts;

/// <summary>
/// Phase 29C against real PostgreSQL: the per-user appearance preference — its default, persistence
/// as text, isolation between users, database-level validity, and the migration that adds it.
/// </summary>
[Collection("Postgres")]
public sealed class AppearancePreferenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 21, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;

    public AppearancePreferenceTests(PostgresFixture fixture) => _fixture = fixture;

    private static AccountService Service(FlowOpsDbContext context) =>
        new(context, CreateUserManager(context), new TicketTestData.FixedTimeProvider(Now));

    private static Task<string> StoredAsync(FlowOpsDbContext context, Guid userId) =>
        context.Database.SqlQuery<string>($"SELECT appearance AS \"Value\" FROM \"AspNetUsers\" WHERE id = {userId}").SingleAsync();

    [Fact]
    public async Task NewUser_DefaultsToDark()
    {
        await using var context = _fixture.CreateContext();
        var userId = await TicketTestData.AddUserAsync(context);

        Assert.Equal(AppearancePreference.Dark, await Service(context).GetAppearanceAsync(userId));
    }

    [Theory]
    [InlineData(AppearancePreference.Light)]
    [InlineData(AppearancePreference.System)]
    [InlineData(AppearancePreference.Dark)]
    public async Task SavedChoice_PersistsAcrossContexts_AsItsOwnNameText(AppearancePreference choice)
    {
        await using var context = _fixture.CreateContext();
        var userId = await TicketTestData.AddUserAsync(context);

        Assert.True(await Service(context).SetAppearanceAsync(userId, choice));

        await using var fresh = _fixture.CreateContext();
        Assert.Equal(choice, await Service(fresh).GetAppearanceAsync(userId));

        // "System" is stored as System — never as a resolved Light/Dark.
        Assert.Equal(choice.ToString(), await StoredAsync(fresh, userId));
    }

    [Fact]
    public async Task OneUsersChoice_NeverChangesAnotherUsers()
    {
        await using var context = _fixture.CreateContext();
        var alice = await TicketTestData.AddUserAsync(context);
        var bob = await TicketTestData.AddUserAsync(context);

        await Service(context).SetAppearanceAsync(alice, AppearancePreference.Light);

        await using var fresh = _fixture.CreateContext();
        Assert.Equal(AppearancePreference.Light, await Service(fresh).GetAppearanceAsync(alice));
        Assert.Equal(AppearancePreference.Dark, await Service(fresh).GetAppearanceAsync(bob));
    }

    [Fact]
    public async Task UndefinedValueOrUnknownUser_ChangesNothing()
    {
        await using var context = _fixture.CreateContext();
        var userId = await TicketTestData.AddUserAsync(context);
        var service = Service(context);
        await service.SetAppearanceAsync(userId, AppearancePreference.Light);

        Assert.False(await service.SetAppearanceAsync(userId, (AppearancePreference)99));
        Assert.False(await service.SetAppearanceAsync(Guid.NewGuid(), AppearancePreference.Light));

        Assert.Equal(AppearancePreference.Light, await service.GetAppearanceAsync(userId));
        Assert.Equal(AppearancePreference.Dark, await service.GetAppearanceAsync(Guid.NewGuid())); // unknown user: the default
    }

    [Fact] // Demo personas may try both themes: appearance is not covered by the demo protection policy.
    public async Task DemoProtectedAccount_CanSaveLightAndDark()
    {
        await using var context = _fixture.CreateContext();
        var userId = await TicketTestData.AddUserAsync(context);
        await context.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsDemoProtected, true));
        var service = Service(context);

        Assert.True(await service.SetAppearanceAsync(userId, AppearancePreference.Light));
        Assert.Equal(AppearancePreference.Light, await service.GetAppearanceAsync(userId));
        Assert.True(await service.SetAppearanceAsync(userId, AppearancePreference.Dark));
        Assert.Equal(AppearancePreference.Dark, await service.GetAppearanceAsync(userId));
    }

    [Fact] // Validity is enforced by the database itself, not only by the application.
    public async Task DatabaseRejectsAnUnknownAppearanceValue()
    {
        await using var context = _fixture.CreateContext();
        var userId = await TicketTestData.AddUserAsync(context);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            context.Database.ExecuteSqlAsync($"UPDATE \"AspNetUsers\" SET appearance = 'Purple' WHERE id = {userId}"));

        Assert.Contains("ck_users_appearance", ex.ToString(), StringComparison.Ordinal);
    }

    [Fact] // Existing users (rows that predate the column) receive Dark on an upgraded database.
    public async Task Migration_GivesExistingUsersDark_OnAnUpgradedDatabase()
    {
        await using var container = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("flowops_appearance_migration").WithUsername("x").WithPassword("x").Build();
        await container.StartAsync();

        var options = new DbContextOptionsBuilder<FlowOpsDbContext>().UseNpgsql(container.GetConnectionString()).UseSnakeCaseNamingConvention().Options;
        await using var context = new FlowOpsDbContext(options);
        var migrator = context.GetService<IMigrator>();

        // Everything before this phase's migration, then a user who already exists.
        await migrator.MigrateAsync("20260921021451_AddSprintCancelAndSnapshots");
        var userId = Guid.NewGuid();
        await context.Database.ExecuteSqlAsync($"""
            INSERT INTO "AspNetUsers" (id, access_failed_count, display_name, email_confirmed, is_active, is_demo_protected, is_platform_admin, lockout_enabled, phone_number_confirmed, two_factor_enabled)
            VALUES ({userId}, 0, 'Existing user', true, true, false, false, false, false, false)
            """);

        await migrator.MigrateAsync();

        Assert.Equal("Dark", await StoredAsync(context, userId));
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
    }

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
