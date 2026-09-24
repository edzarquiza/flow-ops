using FlowOps.Application.Accounts;
using FlowOps.Application.Tests.Persistence;
using FlowOps.Application.Tests.Tickets;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Organizations;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Identity;
using FlowOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FlowOps.Application.Tests.Organizations;

/// <summary>
/// Phase 19 (ADR-0018): explicit organization context — selection, persistence, fallback, and
/// stale-membership recovery — against real PostgreSQL. A real <see cref="IDataProtectionProvider"/>
/// (<see cref="EphemeralDataProtectionProvider"/>) and a real <see cref="DefaultHttpContext"/> are
/// used throughout rather than mocks, since the whole point under test is how the cookie is
/// actually protected/read, not an interface contract.
/// </summary>
[Collection("Postgres")]
public sealed class OrganizationContextTests
{
    private const string CookieName = "FlowOps.CurrentOrganization";
    private const string Password = "A-Genuinely-Str0ng-Pw!";
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;

    public OrganizationContextTests(PostgresFixture fixture) => _fixture = fixture;

    private static IDataProtectionProvider NewProtectionProvider() => new EphemeralDataProtectionProvider();

    private static (IHttpContextAccessor Accessor, DefaultHttpContext Context) NewHttpContext(string? incomingCookieValue = null)
    {
        var context = new DefaultHttpContext();
        if (incomingCookieValue is not null)
        {
            context.Request.Headers.Append("Cookie", $"{CookieName}={Uri.EscapeDataString(incomingCookieValue)}");
        }

        return (new HttpContextAccessor { HttpContext = context }, context);
    }

    /// <summary>Reads back whatever this request's response actually set the cookie to — the exact
    /// value a browser would carry into the next request.</summary>
    private static string? ExtractSetCookieValue(DefaultHttpContext context)
    {
        // The LAST matching Set-Cookie header wins — a context can accumulate more than one
        // (e.g. an initial deterministic-fallback write, then a later explicit switch), exactly
        // as a real browser's cookie jar would end up holding only the most recent value.
        string? result = null;
        foreach (var header in context.Response.Headers.SetCookie)
        {
            if (header is not null && header.StartsWith($"{CookieName}=", StringComparison.Ordinal))
            {
                var afterName = header[(CookieName.Length + 1)..];
                var value = afterName.Split(';')[0];
                result = Uri.UnescapeDataString(value);
            }
        }

        return result;
    }

    private static UserManager<ApplicationUser> CreateUserManager(FlowOpsDbContext context)
    {
        var services = new ServiceCollection();
        services.AddSingleton(context);
        services.AddLogging();
        services.AddIdentityCore<ApplicationUser>().AddRoles<ApplicationRole>().AddEntityFrameworkStores<FlowOpsDbContext>();
        return services.BuildServiceProvider().GetRequiredService<UserManager<ApplicationUser>>();
    }

    private static string UniqueEmail() => $"{Guid.NewGuid():N}@orgcontext.test.local";

    // ---------------------------------------------------------------------------------------
    // Single / multiple membership resolution
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task SingleMembership_IsAutomaticallySelected_NoCookieNeeded()
    {
        await using var context = _fixture.CreateContext();
        var userManager = CreateUserManager(context);
        var accounts = new AccountService(context, userManager, new TicketTestData.FixedTimeProvider(Now));
        var registration = await accounts.RegisterAsync(new RegisterRequest("Solo", UniqueEmail(), Password, "Solo Org"));

        var provider = NewProtectionProvider();
        var (httpAccessor, _) = NewHttpContext();
        var accessor = new CurrentUserAccessor(context, userManager, provider, httpAccessor);

        var currentUser = await accessor.GetCurrentUserAsync(registration.UserId!.Value);

        Assert.Equal(registration.OrganizationId, currentUser!.OrganizationId);
    }

    [Fact] // Step 4: multiple memberships, no stored selection -> deterministic safe default.
    public async Task MultipleMemberships_NoSelection_FallsBackToLowestOrganizationId()
    {
        await using var context = _fixture.CreateContext();
        var userManager = CreateUserManager(context);
        var accounts = new AccountService(context, userManager, new TicketTestData.FixedTimeProvider(Now));
        var first = await accounts.RegisterAsync(new RegisterRequest("Multi", UniqueEmail(), Password, "Org One"));
        var userId = first.UserId!.Value;

        var secondOrg = new Organization(0, "Org Two", Now);
        context.Organizations.Add(secondOrg);
        await context.SaveChangesAsync();
        context.OrganizationMemberships.Add(new OrganizationMembership(0, secondOrg.Id, userId, UserRole.Viewer, Now));
        await context.SaveChangesAsync();

        var provider = NewProtectionProvider();
        var (httpAccessor, _) = NewHttpContext();
        var accessor = new CurrentUserAccessor(context, userManager, provider, httpAccessor);

        var currentUser = await accessor.GetCurrentUserAsync(userId);

        var expectedLowest = Math.Min(first.OrganizationId!.Value, secondOrg.Id);
        Assert.Equal(expectedLowest, currentUser!.OrganizationId);
    }

    [Fact]
    public async Task ZeroMemberships_ReturnsNull_NoFabricatedOrganization()
    {
        await using var context = _fixture.CreateContext();
        var userManager = CreateUserManager(context);
        var userId = await TicketTestData.AddUserAsync(context);

        var accessor = new CurrentUserAccessor(context, userManager, NewProtectionProvider(), NewHttpContext().Accessor);

        Assert.Null(await accessor.GetCurrentUserAsync(userId));
    }

    // ---------------------------------------------------------------------------------------
    // Explicit switch + persistence across "requests"
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task TrySwitchOrganizationAsync_ValidMembership_PersistsAcrossNextRequest()
    {
        await using var context = _fixture.CreateContext();
        var userManager = CreateUserManager(context);
        var (userId, orgAId, orgBId) = await TwoOrgUserAsync(context, userManager, "SwitchA", "SwitchB");

        var provider = NewProtectionProvider();

        // Request 1: switch to Org B.
        var (accessor1, context1) = NewHttpContext();
        var service1 = new CurrentUserAccessor(context, userManager, provider, accessor1);
        var switched = await service1.TrySwitchOrganizationAsync(userId, orgBId);
        Assert.True(switched);
        var cookieAfterSwitch = ExtractSetCookieValue(context1);
        Assert.NotNull(cookieAfterSwitch);

        // Request 2: a brand-new HttpContext carrying forward exactly what request 1 set —
        // simulates the browser sending the cookie back on the next request.
        var (accessor2, _) = NewHttpContext(cookieAfterSwitch);
        var service2 = new CurrentUserAccessor(context, userManager, provider, accessor2);
        var currentUser = await service2.GetCurrentUserAsync(userId);

        Assert.Equal(orgBId, currentUser!.OrganizationId);
        _ = orgAId;
    }

    [Fact] // Step 6: switching is allowed only among the caller's own real memberships.
    public async Task TrySwitchOrganizationAsync_NonMember_Fails_AndDoesNotChangeContext()
    {
        await using var context = _fixture.CreateContext();
        var userManager = CreateUserManager(context);
        var (userId, orgAId, _) = await TwoOrgUserAsync(context, userManager, "NonMemberA", "NonMemberB");

        var otherOrg = new Organization(0, "Not A Member Org", Now);
        context.Organizations.Add(otherOrg);
        await context.SaveChangesAsync();

        var (accessor, httpContext) = NewHttpContext();
        var service = new CurrentUserAccessor(context, userManager, NewProtectionProvider(), accessor);

        var switched = await service.TrySwitchOrganizationAsync(userId, otherOrg.Id);

        Assert.False(switched);
        Assert.Null(ExtractSetCookieValue(httpContext)); // nothing persisted for the failed attempt

        var currentUser = await service.GetCurrentUserAsync(userId);
        Assert.Equal(orgAId, currentUser!.OrganizationId); // unaffected — still the deterministic default
    }

    [Fact] // Step 6/29: a nonexistent organization id fails identically to a real one the caller
           // isn't a member of — never a different error shape that could enumerate organizations.
    public async Task TrySwitchOrganizationAsync_NonexistentOrganizationId_FailsSameAsNonMember()
    {
        await using var context = _fixture.CreateContext();
        var userManager = CreateUserManager(context);
        var (userId, _, _) = await TwoOrgUserAsync(context, userManager, "GhostA", "GhostB");

        var service = new CurrentUserAccessor(context, userManager, NewProtectionProvider(), NewHttpContext().Accessor);

        var switched = await service.TrySwitchOrganizationAsync(userId, 999_999);

        Assert.False(switched);
    }

    // ---------------------------------------------------------------------------------------
    // Multi-org role resolution
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task RoleResolvesFromTheSelectedMembership_NotAGlobalUserRole()
    {
        await using var context = _fixture.CreateContext();
        var userManager = CreateUserManager(context);
        var accounts = new AccountService(context, userManager, new TicketTestData.FixedTimeProvider(Now));
        var orgA = await accounts.RegisterAsync(new RegisterRequest("RoleTest", UniqueEmail(), Password, "Role Org A"));
        var userId = orgA.UserId!.Value;

        var orgB = new Organization(0, "Role Org B", Now);
        context.Organizations.Add(orgB);
        await context.SaveChangesAsync();
        context.OrganizationMemberships.Add(new OrganizationMembership(0, orgB.Id, userId, UserRole.Agent, Now));
        await context.SaveChangesAsync();

        var provider = NewProtectionProvider();
        var (accessorA, contextA) = NewHttpContext();
        var serviceA = new CurrentUserAccessor(context, userManager, provider, accessorA);
        var asOrgA = await serviceA.GetCurrentUserAsync(userId);
        Assert.Equal(UserRole.Admin, asOrgA!.Role); // registration always makes the creator Admin

        await serviceA.TrySwitchOrganizationAsync(userId, orgB.Id);
        var cookie = ExtractSetCookieValue(contextA);
        var (accessorB, _) = NewHttpContext(cookie);
        var serviceB = new CurrentUserAccessor(context, userManager, provider, accessorB);
        var asOrgB = await serviceB.GetCurrentUserAsync(userId);

        Assert.Equal(UserRole.Agent, asOrgB!.Role);

        // Switching must never have altered either membership row.
        var membershipA = await context.OrganizationMemberships.AsNoTracking().SingleAsync(m => m.OrganizationId == orgA.OrganizationId && m.UserId == userId);
        Assert.Equal(UserRole.Admin, membershipA.Role);
    }

    // ---------------------------------------------------------------------------------------
    // Stale-membership recovery (Step 7)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task StaleSelectedOrganization_MembershipRemoved_FallsBackToAnotherRealMembership()
    {
        await using var context = _fixture.CreateContext();
        var userManager = CreateUserManager(context);
        var (userId, orgAId, orgBId) = await TwoOrgUserAsync(context, userManager, "StaleA", "StaleB");

        var provider = NewProtectionProvider();
        var (accessor1, context1) = NewHttpContext();
        var service1 = new CurrentUserAccessor(context, userManager, provider, accessor1);
        await service1.TrySwitchOrganizationAsync(userId, orgBId);
        var cookie = ExtractSetCookieValue(context1);

        // An Admin elsewhere removes this exact membership — the cookie still says Org B.
        var membershipB = await context.OrganizationMemberships.SingleAsync(m => m.OrganizationId == orgBId && m.UserId == userId);
        context.OrganizationMemberships.Remove(membershipB);
        await context.SaveChangesAsync();

        var (accessor2, context2) = NewHttpContext(cookie);
        var service2 = new CurrentUserAccessor(context, userManager, provider, accessor2);
        var currentUser = await service2.GetCurrentUserAsync(userId);

        Assert.NotNull(currentUser);
        Assert.Equal(orgAId, currentUser!.OrganizationId); // recovered to the remaining real membership
        Assert.NotEqual(orgBId, currentUser.OrganizationId);
        Assert.NotNull(ExtractSetCookieValue(context2)); // self-healed: the recovered choice is persisted
    }

    [Fact]
    public async Task StaleSelectedOrganization_NoMembershipsRemain_ReturnsNull_NeverFabricated()
    {
        await using var context = _fixture.CreateContext();
        var userManager = CreateUserManager(context);
        var accounts = new AccountService(context, userManager, new TicketTestData.FixedTimeProvider(Now));
        var registration = await accounts.RegisterAsync(new RegisterRequest("LoneOrg", UniqueEmail(), Password, "Lone Org"));
        var userId = registration.UserId!.Value;

        var provider = NewProtectionProvider();
        var (accessor1, context1) = NewHttpContext();
        var service1 = new CurrentUserAccessor(context, userManager, provider, accessor1);
        await service1.GetCurrentUserAsync(userId); // establishes/persists the only membership as current
        var cookie = ExtractSetCookieValue(context1);

        var onlyMembership = await context.OrganizationMemberships.SingleAsync(m => m.UserId == userId);
        context.OrganizationMemberships.Remove(onlyMembership);
        await context.SaveChangesAsync();

        var (accessor2, _) = NewHttpContext(cookie);
        var service2 = new CurrentUserAccessor(context, userManager, provider, accessor2);

        Assert.Null(await service2.GetCurrentUserAsync(userId));
    }

    // ---------------------------------------------------------------------------------------
    // Tampered / forged cookie value
    // ---------------------------------------------------------------------------------------

    [Fact] // Step 29 #1/#2: a forged context value can never bypass membership validation — it is
           // simply discarded and the safe fallback applies, exactly like no cookie at all.
    public async Task TamperedCookieValue_IsDiscarded_FallsBackSafely_NeverThrows()
    {
        await using var context = _fixture.CreateContext();
        var userManager = CreateUserManager(context);
        var (userId, orgAId, _) = await TwoOrgUserAsync(context, userManager, "TamperA", "TamperB");

        var (accessor, _) = NewHttpContext("not-a-real-protected-value-at-all");
        var service = new CurrentUserAccessor(context, userManager, NewProtectionProvider(), accessor);

        var currentUser = await service.GetCurrentUserAsync(userId);

        Assert.NotNull(currentUser);
        Assert.Equal(orgAId, currentUser!.OrganizationId); // safe deterministic fallback, not a crash
    }

    [Fact] // A value protected by a DIFFERENT key ring (a different provider instance/app) must
           // never be trusted, even though it is well-formed Data-Protection ciphertext.
    public async Task CookieProtectedByADifferentKeyRing_IsDiscarded()
    {
        await using var context = _fixture.CreateContext();
        var userManager = CreateUserManager(context);
        var (userId, orgAId, orgBId) = await TwoOrgUserAsync(context, userManager, "ForeignA", "ForeignB");

        var foreignProvider = new EphemeralDataProtectionProvider(); // an unrelated key ring
        var foreignProtector = foreignProvider.CreateProtector("FlowOps.CurrentOrganization");
        var foreignCookieValue = foreignProtector.Protect(orgBId.ToString());

        var (accessor, _) = NewHttpContext(foreignCookieValue);
        var service = new CurrentUserAccessor(context, userManager, NewProtectionProvider(), accessor); // this test's own, different provider

        var currentUser = await service.GetCurrentUserAsync(userId);

        Assert.Equal(orgAId, currentUser!.OrganizationId); // never trusts the foreign-signed value
    }

    // ---------------------------------------------------------------------------------------
    // Logout / account lifecycle (Step 8/9)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ClearSelectedOrganization_RemovesTheCookie()
    {
        await using var context = _fixture.CreateContext();
        var userManager = CreateUserManager(context);
        var (userId, _, orgBId) = await TwoOrgUserAsync(context, userManager, "ClearA", "ClearB");

        var (accessor, httpContext) = NewHttpContext();
        var service = new CurrentUserAccessor(context, userManager, NewProtectionProvider(), accessor);
        await service.TrySwitchOrganizationAsync(userId, orgBId);

        service.ClearSelectedOrganization();

        // Deleting a cookie is expressed as a Set-Cookie with an expired date — assert the
        // deletion header for this cookie name is present in the final response.
        var hasDeletion = httpContext.Response.Headers.SetCookie
            .Any(h => h is not null && h.StartsWith($"{CookieName}=", StringComparison.Ordinal) && h.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
        Assert.True(hasDeletion, string.Join(" | ", httpContext.Response.Headers.SetCookie.ToArray() ?? []));
    }

    [Fact] // Step 9's core multi-user browser scenario, at the mechanism level: a fresh HttpContext
           // with no cookie (as if logout cleared it) must never resolve a stale organization.
    public async Task NoStoredSelection_NextUserNeverInheritsAnything_ResolvesTheirOwnDefault()
    {
        await using var context = _fixture.CreateContext();
        var userManager = CreateUserManager(context);
        var accounts = new AccountService(context, userManager, new TicketTestData.FixedTimeProvider(Now));
        var userB = await accounts.RegisterAsync(new RegisterRequest("Second Browser User", UniqueEmail(), Password, "Second User Org"));

        // A fresh context with no cookie at all — exactly what a cleared-on-logout cookie jar
        // looks like to the next person signing in on the same browser.
        var (accessor, _) = NewHttpContext(incomingCookieValue: null);
        var service = new CurrentUserAccessor(context, userManager, NewProtectionProvider(), accessor);

        var currentUser = await service.GetCurrentUserAsync(userB.UserId!.Value);

        Assert.Equal(userB.OrganizationId, currentUser!.OrganizationId);
    }

    [Fact] // Step 8: a deactivated account resolves to nothing, organization context or not.
    public async Task DeactivatedAccount_NeverResolvesCurrentUser_RegardlessOfCookie()
    {
        await using var context = _fixture.CreateContext();
        var userManager = CreateUserManager(context);
        var (userId, orgAId, _) = await TwoOrgUserAsync(context, userManager, "DeactA", "DeactB");

        var (accessor1, context1) = NewHttpContext();
        var service1 = new CurrentUserAccessor(context, userManager, NewProtectionProvider(), accessor1);
        await service1.GetCurrentUserAsync(userId);
        var cookie = ExtractSetCookieValue(context1);

        var user = await userManager.FindByIdAsync(userId.ToString());
        user!.IsActive = false;
        await userManager.UpdateAsync(user);

        var (accessor2, _) = NewHttpContext(cookie);
        var service2 = new CurrentUserAccessor(context, userManager, NewProtectionProvider(), accessor2);

        Assert.Null(await service2.GetCurrentUserAsync(userId));
        _ = orgAId;
    }

    // ---------------------------------------------------------------------------------------
    // Switcher's own data source (Step 17)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task GetAvailableOrganizationsAsync_ReturnsOnlyTheCallersOwnMemberships()
    {
        await using var context = _fixture.CreateContext();
        var userManager = CreateUserManager(context);
        var (userId, orgAId, orgBId) = await TwoOrgUserAsync(context, userManager, "ListA", "ListB");

        var unrelatedOrg = new Organization(0, "Unrelated Org", Now);
        context.Organizations.Add(unrelatedOrg);
        await context.SaveChangesAsync();

        var service = new CurrentUserAccessor(context, userManager, NewProtectionProvider(), NewHttpContext().Accessor);

        var options = await service.GetAvailableOrganizationsAsync(userId);

        Assert.Equal(2, options.Count);
        Assert.Contains(options, o => o.Id == orgAId);
        Assert.Contains(options, o => o.Id == orgBId);
        Assert.DoesNotContain(options, o => o.Id == unrelatedOrg.Id);
    }

    // ---------------------------------------------------------------------------------------
    // Switching does not bypass existing organization-scoped query authorization
    // ---------------------------------------------------------------------------------------

    [Fact] // Step 14: after switching, ticket visibility follows the NEW current organization only.
    public async Task AfterSwitch_TicketQueueReflectsTheNewOrganization_NotTheOld()
    {
        await using var context = _fixture.CreateContext();
        var userManager = CreateUserManager(context);
        var (userId, orgAId, orgBId) = await TwoOrgUserAsync(context, userManager, "TicketsA", "TicketsB");

        var teamA = await TicketTestData.AddTeamAsync(context); // ambient shared org for TicketTestData — not orgA/orgB
        _ = teamA;

        var teamInOrgB = new Domain.Directory.Team(0, orgBId, $"Team-{Guid.NewGuid():N}", Now);
        context.Teams.Add(teamInOrgB);
        await context.SaveChangesAsync();
        var categoryInOrgB = new Domain.Catalog.Category(0, teamInOrgB.Id, $"Category-{Guid.NewGuid():N}", WorkType.Incident, Now);
        context.Categories.Add(categoryInOrgB);
        await context.SaveChangesAsync();
        context.TeamMembers.Add(new Domain.Directory.TeamMember(teamInOrgB.Id, userId, isTeamManager: false, Now));
        await context.SaveChangesAsync();

        var ticketService = new TicketService(context, new TicketTestData.FixedTimeProvider(Now), TestEmail.Sender, TestEmail.Options);
        var orgBAdmin = new CurrentUser(userId, orgBId, UserRole.Admin, new HashSet<int> { teamInOrgB.Id }, new HashSet<int>());
        await ticketService.CreateAsync(new CreateTicketRequest("Org B only ticket", "Visible only after switching to Org B.", WorkType.Incident, Priority.Medium, teamInOrgB.Id, categoryInOrgB.Id, null), orgBAdmin);

        var provider = NewProtectionProvider();
        var (accessorA, contextA) = NewHttpContext();
        var serviceA = new CurrentUserAccessor(context, userManager, provider, accessorA);
        var currentAsOrgA = await serviceA.GetCurrentUserAsync(userId);
        var queueAsOrgA = await new TicketQueryService(context, new TicketTestData.FixedTimeProvider(Now)).GetQueueAsync(currentAsOrgA!, 1);
        Assert.Empty(queueAsOrgA.Items); // Org A has no tickets at all

        await serviceA.TrySwitchOrganizationAsync(userId, orgBId);
        var cookie = ExtractSetCookieValue(contextA);

        // A fresh HttpContext carrying the cookie the switch just set — a Response cookie never
        // feeds back into the SAME context's Request.Cookies, exactly like a real browser only
        // sends the new value on its NEXT request, not the one that set it.
        var (accessorB, _) = NewHttpContext(cookie);
        var serviceB = new CurrentUserAccessor(context, userManager, provider, accessorB);
        var currentAsOrgB = await serviceB.GetCurrentUserAsync(userId);
        var queueAsOrgB = await new TicketQueryService(context, new TicketTestData.FixedTimeProvider(Now)).GetQueueAsync(currentAsOrgB!, 1);

        Assert.Single(queueAsOrgB.Items);
        _ = orgAId;
    }

    // ---------------------------------------------------------------------------------------
    // Helper: a real user with a real Admin membership in a first organization (via registration)
    // and a real second membership in a second organization.
    // ---------------------------------------------------------------------------------------

    private async Task<(Guid UserId, int OrgAId, int OrgBId)> TwoOrgUserAsync(FlowOpsDbContext context, UserManager<ApplicationUser> userManager, string labelA, string labelB)
    {
        var accounts = new AccountService(context, userManager, new TicketTestData.FixedTimeProvider(Now));
        var orgA = await accounts.RegisterAsync(new RegisterRequest(labelA, UniqueEmail(), Password, $"{labelA} Org"));
        var userId = orgA.UserId!.Value;

        var orgB = new Organization(0, $"{labelB} Org", Now);
        context.Organizations.Add(orgB);
        await context.SaveChangesAsync();
        context.OrganizationMemberships.Add(new OrganizationMembership(0, orgB.Id, userId, UserRole.Viewer, Now));
        await context.SaveChangesAsync();

        return (userId, orgA.OrganizationId!.Value, orgB.Id);
    }
}
