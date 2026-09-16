using System.Threading.RateLimiting;
using FlowOps.Application.Accounts;
using FlowOps.Application.Demo;
using FlowOps.Application.Organizations;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Attention;
using FlowOps.Infrastructure.Identity;
using FlowOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Phase 4: identity, cookie authentication, roles, and the coarse authorization gate. No ticket
// endpoints, no Razor Pages beyond the authentication boundary itself — those are later phases
// (CLAUDE.md §19.2).
var builder = WebApplication.CreateBuilder(args);

// Phase 15 / CLAUDE.md §17: "Listens on ${PORT} (Render supplies it)". Render injects a dynamic
// PORT environment variable at runtime that cannot be known ahead of time; only overriding the
// binding when it is actually present leaves local `dotnet run` and the Docker Compose 8080
// contract (the base image's own ASPNETCORE_HTTP_PORTS default) completely unaffected.
var renderPort = builder.Configuration["PORT"];
if (!string.IsNullOrEmpty(renderPort))
{
    builder.WebHost.UseUrls($"http://+:{renderPort}");
}

var connectionString = builder.Configuration.GetConnectionString("FlowOps")
    ?? throw new InvalidOperationException(
        "Missing ConnectionStrings:FlowOps configuration. The app must fail fast at startup on " +
        "missing configuration, not fail later at first request (CLAUDE.md §13).");

builder.Services.AddDbContext<FlowOpsDbContext>(options =>
    options.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure())
        .UseSnakeCaseNamingConvention());

// CLAUDE.md §18 / ADR-0013: Render terminates TLS at its edge proxy and forwards plain HTTP to
// this container, so Kestrel must be told to trust the X-Forwarded-Proto/X-Forwarded-For headers
// that proxy adds — otherwise Request.IsHttps is always false here, and CookieSecurePolicy.
// SameAsRequest would silently stop marking any cookie Secure. See ADR-0013 for why clearing the
// known-networks/proxies allowlist is the correct choice for this specific hosting topology, not
// a blanket "trust everything."
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// CLAUDE.md §14: seeding runs at startup when FlowOps__Demo__Enabled=true (config path
// "FlowOps:Demo:Enabled" — the same "FlowOps:" root FlowOps:Database:ApplyMigrationsOnStartup
// already uses). Bound and validated here so a misconfigured demo deployment (enabled with no
// persona password) fails fast at startup rather than seeding accounts nobody can log into.
builder.Services
    .AddOptions<DemoOptions>()
    .Bind(builder.Configuration.GetSection("FlowOps:Demo"))
    .Validate(
        o => !o.Enabled || !string.IsNullOrWhiteSpace(o.PersonaPassword),
        "FlowOps:Demo:PersonaPassword is required when FlowOps:Demo:Enabled is true.")
    .ValidateOnStart();

builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<DemoOptions>>().Value);
builder.Services.AddScoped<DemoDataSeeder>();

// CLAUDE.md §12: without this, every Render restart invalidates every cookie and antiforgery
// token in Production. Scoped to Production only (an environment-composition choice, not
// business-code branching — CLAUDE.md §13): every antiforgery-protected page, including the
// public login form, needs the key ring to be available, so persisting it to a database that
// may not be reachable in Development/Testing (no Docker, no local Postgres) would make the
// login page itself fail to render. Development/Testing fall back to Identity's default
// file-system key ring, which needs no database at all.
if (builder.Environment.IsProduction())
{
    builder.Services.AddDataProtection().PersistKeysToDbContext<FlowOpsDbContext>();
}

builder.Services
    .AddIdentity<ApplicationUser, ApplicationRole>(options =>
    {
        // CLAUDE.md §12: "Identity lockout after 5 failures for 15 minutes."
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Lockout.AllowedForNewUsers = true;

        // ASP.NET Core Identity's own default password-hashing mechanism (PBKDF2) is used as-is
        // (CLAUDE.md §12: "Never hand-roll hashing") — nothing here changes the hasher.
        options.User.RequireUniqueEmail = true;

        // Phase 22 (SEC-1): explicit rather than left to Identity's own defaults (a 6-character
        // minimum), which is weaker than a reasonable modern baseline for an operations platform.
        // This only governs *new* passwords (registration, invitation acceptance, password
        // change) — Identity validates a password against the current options only when it is
        // set, never re-validates an already-hashed password on login, so no existing user is
        // silently locked out by this becoming stricter.
        options.Password.RequiredLength = 12;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
    })
    .AddEntityFrameworkStores<FlowOpsDbContext>()
    .AddDefaultTokenProviders();

// CLAUDE.md §12: HttpOnly, Secure, SameSite=Strict, sliding expiration 8h, /Account/Login path.
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.HttpOnly = true;
    // SameAsRequest (the Identity default), not Always: the cookie is marked Secure whenever the
    // request itself was HTTPS, which is every real request once CLAUDE.md §12's "HTTPS enforced
    // in Production" lands. Forcing Always here would silently stop the cookie from ever being
    // resent over plain HTTP — breaking local `dotnet run` dev and every WebApplicationFactory
    // test in this repository, since neither uses TLS.
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";
});

// Phase 15 / ADR-0013: the antiforgery cookie's own SecurePolicy is a *separate* setting from the
// application cookie's above — ASP.NET Core does not default it to SameAsRequest, so without this
// it stays non-Secure even once UseForwardedHeaders correctly reports Request.IsHttps as true
// (confirmed directly: a forwarded-HTTPS request's antiforgery Set-Cookie carried no Secure
// attribute until this was added). Matches the application cookie's own policy for the same
// forwarded-proxy reason.
builder.Services.Configure<Microsoft.AspNetCore.Antiforgery.AntiforgeryOptions>(options =>
{
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

// CLAUDE.md §12: "Rate limiting (built-in AddRateLimiter): login and password endpoints
// (5/min/IP)." Applied to the Login page via [EnableRateLimiting("login")].
builder.Services.AddRateLimiter(options =>
{
    // CLAUDE.md §11.3 (correct status codes): a throttled caller is 429 Too Many Requests. The
    // framework default is 503 Service Unavailable, which claims the service is broken when in
    // fact the caller was rate limited.
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // CLAUDE.md §13: "Log at Warning: ... rate-limit rejections." Resolved per-rejection rather
    // than captured at configuration time, since the logger factory isn't available yet here.
    options.OnRejected = (context, cancellationToken) =>
    {
        var logger = context.HttpContext.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("FlowOps.RateLimiting");

        logger.LogWarning(
            "Rate limit rejected for {RemoteIp} on {Path}.",
            context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown-ip",
            context.HttpContext.Request.Path);

        return ValueTask.CompletedTask;
    };

    // CLAUDE.md §12's "5/min/IP" is the real production value and the default here — configurable
    // only so FlowOpsWebApplicationFactory (Phase 24A) can raise it for its own in-process test
    // host via UseSetting. Every request a WebApplicationFactory TestServer handles shares one
    // synthetic remote IP, so once account approval requires an extra, genuine login POST per
    // registered test fixture (no more auto-sign-in — see AccountService.RegisterAsync), the
    // existing per-class "stay at or under five sign-ins" budget many Web.Tests classes already
    // documented would need rewriting around a production security control instead of around test
    // volume. No appsettings.*.json file sets this key, so every real deployment keeps exactly 5.
    var loginPermitLimit = builder.Configuration.GetValue<int?>("RateLimiting:Login:PermitLimitPerMinute") ?? 5;

    options.AddPolicy("login", httpContext =>
    {
        // A Razor Page is a single endpoint for every verb, so [EnableRateLimiting] on LoginModel
        // covers its GET as well as its POST. Only credential submission is throttled here:
        // limiting the GET would lock a legitimate user out of *seeing* the sign-in form after
        // five reloads while adding nothing to the protection of the password check itself.
        if (!HttpMethods.IsPost(httpContext.Request.Method))
        {
            return RateLimitPartition.GetNoLimiter("login:non-post");
        }

        // Partitioned per caller IP, per §12's "5/min/IP". An unpartitioned window would be one
        // global bucket, letting a single caller exhaust every other user's login attempts.
        var partitionKey = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown-ip";

        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = loginPermitLimit,
            Window = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0,
        });
    });
});

// Phase 19 / ADR-0018: CurrentUserAccessor reads/writes the (Data-Protection-protected) current-
// organization cookie via IHttpContextAccessor — the standard ASP.NET Core service for this,
// not a new abstraction. Required so the same class works both from a real request (organization
// switching) and from Application.Tests' direct construction with no HTTP context at all.
builder.Services.AddHttpContextAccessor();

// AUTH-RULE-04: resolves the Domain's CurrentUser from the authoritative persistence model —
// see CurrentUserAccessor's own doc comment for why this is not sourced from cookie claims.
builder.Services.AddScoped<CurrentUserAccessor>();

// TICKET-INV-10: the Domain never calls DateTime.UtcNow — every timestamp is passed in by the
// Application layer from this clock. Registered here because the composition root is the one
// place CLAUDE.md §4.4 permits the real clock to be named. Tests substitute a fake.
builder.Services.AddSingleton(TimeProvider.System);

// Phase 17: registration, profile/settings, and account deletion.
builder.Services.AddScoped<AccountService>();

// Phase 18: organization invitations and member management.
builder.Services.AddScoped<InvitationService>();
builder.Services.AddScoped<MembershipService>();

// ADR-0021: Directory/Catalog modules' first real public surface — team and category creation
// for the Workspace Setup checklist's "set up your first team" step.
builder.Services.AddScoped<FlowOps.Application.Directory.TeamService>();
builder.Services.AddScoped<FlowOps.Application.Catalog.CatalogService>();

// Phase 24 (ADR-0023): platform administration — deliberately separate DI registrations from
// every tenant-scoped service above, since none of them are organization-scoped.
builder.Services.AddScoped<FlowOps.Application.Platform.PlatformUserAccessor>();
builder.Services.AddScoped<FlowOps.Application.Platform.PlatformOrganizationService>();
builder.Services.AddScoped<FlowOps.Application.Platform.PlatformUserService>();

// Phase 5 ticket use cases.
builder.Services.AddScoped<TicketService>();
builder.Services.AddScoped<TicketQueryService>();

// ATTN-RULE-03 / CLAUDE.md §13: every attention threshold comes from configuration, bound once
// here and validated at startup rather than failing at first request. AttentionOptions itself is
// a plain Domain POCO with no binding attributes (docs/architecture.md §2) — the binding lives in
// the composition root, and an absent "Attention" section simply leaves the ATTN-RULE-02 defaults
// in place. The bound instance is registered directly so Application injects the Domain type and
// needs no options package of its own.
builder.Services
    .AddOptions<AttentionOptions>()
    .Bind(builder.Configuration.GetSection("Attention"))
    .Validate(
        o => o.UnassignedUrgentMinutes > 0
            && o.StalledPendingDays > 0
            && o.StalledInProgressDays > 0
            && o.ChurnAssignmentChangeThreshold > 0
            && o.AgingThresholdDays.Count > 0
            && o.AgingThresholdDays.Values.All(days => days > 0),
        "Attention thresholds must all be greater than zero, and every priority needs an aging threshold.")
    .ValidateOnStart();

builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<AttentionOptions>>().Value);

// Phase 8 attention/at-risk read path.
builder.Services.AddScoped<AttentionQueryService>();

// Phase 10 dashboard/analytics read path.
builder.Services.AddScoped<AnalyticsQueryService>();

builder.Services.AddRazorPages(options =>
{
    // Everything requires authentication by default; only the login/access-denied pages (and
    // health checks, mapped separately below) are reachable anonymously.
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Account/Login");
    options.Conventions.AllowAnonymousToPage("/Account/Register");
    // Phase 24A: reached immediately after registration, before any session exists — must be
    // reachable exactly like Register/Login themselves.
    options.Conventions.AllowAnonymousToPage("/Account/PendingApproval");
    options.Conventions.AllowAnonymousToPage("/Account/AcceptInvitation");
    options.Conventions.AllowAnonymousToPage("/Account/AccessDenied");
    // Phase 12 (§11.3): the exception-handler landing page — an unhandled exception can happen
    // before authentication even runs, so this must be reachable regardless of session state.
    options.Conventions.AllowAnonymousToPage("/Error");

    // AUTH-RULE-01 / §6.1: the coarse gate — "Manage users/teams/categories/SLA" is Admin-only.
    // ADR-0021: no longer a folder-level RequireRole policy — see that ADR for why the Phase 4
    // Identity-role-claim gate silently stopped working for every real user once Phase 16 made
    // OrganizationMembership.Role the sole authority. /Admin now just needs authentication (via
    // AuthorizeFolder("/") above); Pages/Admin/Index.cshtml.cs does the actual Admin check itself,
    // the same CurrentUserAccessor + CurrentUser.Role pattern every other role-gated page uses.
});

builder.Services
    .AddHealthChecks()
    .AddDbContextCheck<FlowOpsDbContext>("database", tags: ["ready"]);

var app = builder.Build();

// Phase 15 / ADR-0014: Render's Pre-Deploy Command runs `dotnet FlowOps.Web.dll init-database`
// using this same built image, before the new web instance starts — see ADR-0014 for why. This
// must be checked before any middleware is configured and before Kestrel could ever bind a port:
// build the host, run the same migrate/seed sequence the normal path below also runs, then exit.
// The normal (no-args) path immediately below is unaffected and remains the fallback for local
// `dotnet run` and the Docker Compose `app` service, where there is no separate pre-deploy step.
if (args.Contains("init-database"))
{
    var initLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("FlowOps.Startup");
    try
    {
        await InitializeDatabaseAsync(app.Services, app.Configuration, initLogger);
        initLogger.LogInformation("Database initialization completed successfully.");
        await app.DisposeAsync();
        return 0;
    }
    catch (Exception ex)
    {
        // Logged explicitly (rather than left to an unhandled-exception crash dump) so a Render
        // Pre-Deploy Command failure is legible in its own log output; still a hard failure — the
        // web instance must not start on top of a failed initialization.
        initLogger.LogCritical(ex, "Database initialization failed.");
        await app.DisposeAsync();
        return 1;
    }
}

// Phase 24 (ADR-0023): the ONLY way ApplicationUser.IsPlatformAdmin is ever set — no tenant-facing
// UI, no self-service path, no seeded credential. Requires the same trust tier as running a
// deployment command or a database migration (Render shell, `docker compose exec`, or local
// `dotnet run -- grant-platform-admin <email>`), deliberately: platform authority is a
// high-value boundary (CLAUDE.md's security posture), so granting it must never be reachable from
// an authenticated HTTP session. Symmetric `revoke-platform-admin` exists so a mistaken grant is
// never a one-way door recoverable only via raw SQL.
if (args.Length >= 2 && (args[0] == "grant-platform-admin" || args[0] == "revoke-platform-admin"))
{
    var grant = args[0] == "grant-platform-admin";
    var email = args[1];
    var cliLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("FlowOps.Startup");
    using var scope = app.Services.CreateScope();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var user = await userManager.FindByEmailAsync(email);
    if (user is null)
    {
        cliLogger.LogCritical("No user found with email {Email}.", email);
        await app.DisposeAsync();
        return 1;
    }

    user.IsPlatformAdmin = grant;
    var result = await userManager.UpdateAsync(user);
    if (!result.Succeeded)
    {
        cliLogger.LogCritical("Failed to update platform-admin status for {Email}: {Errors}", email, string.Join("; ", result.Errors.Select(e => e.Description)));
        await app.DisposeAsync();
        return 1;
    }

    cliLogger.LogInformation("{Email} platform-admin status is now: {IsPlatformAdmin}.", email, grant);
    await app.DisposeAsync();
    return 0;
}

// There is deliberately no self-service "forgot password" flow (ASP.NET Core Identity stores
// only a one-way PBKDF2 hash — the original password is never recoverable, by this command,
// Render's dashboard, or anyone else). Recovery is therefore always a reset, at the same
// deploy/shell trust tier as `grant-platform-admin` above — never an HTTP endpoint.
if (args.Length >= 3 && args[0] == "reset-password")
{
    var email = args[1];
    var newPassword = args[2];
    var cliLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("FlowOps.Startup");
    using var scope = app.Services.CreateScope();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var user = await userManager.FindByEmailAsync(email);
    if (user is null)
    {
        cliLogger.LogCritical("No user found with email {Email}.", email);
        await app.DisposeAsync();
        return 1;
    }

    await userManager.RemovePasswordAsync(user);
    var result = await userManager.AddPasswordAsync(user, newPassword);
    if (!result.Succeeded)
    {
        cliLogger.LogCritical("Failed to reset password for {Email}: {Errors}", email, string.Join("; ", result.Errors.Select(e => e.Description)));
        await app.DisposeAsync();
        return 1;
    }

    cliLogger.LogInformation("Password for {Email} has been reset.", email);
    await app.DisposeAsync();
    return 0;
}

// Phase 24A (ADR-0024): closes the bootstrap gap `grant-platform-admin` alone would otherwise
// leave — the very first Platform Admin is themselves a self-registered account, which starts
// Pending exactly like any other (ADR-0024's whole point is that platform authority never implies
// approval, and vice versa). With no Platform Admin yet able to sign in, nothing could ever approve
// that first account through the ordinary /Platform/Users workflow — the same trust tier as
// grant-platform-admin itself (a deployment/shell command, never an HTTP endpoint) is the correct
// place to break that circularity.
if (args.Length >= 2 && args[0] == "approve-account")
{
    var email = args[1];
    var cliLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("FlowOps.Startup");
    using var scope = app.Services.CreateScope();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var user = await userManager.FindByEmailAsync(email);
    if (user is null)
    {
        cliLogger.LogCritical("No user found with email {Email}.", email);
        await app.DisposeAsync();
        return 1;
    }

    user.RegistrationApprovedAt = TimeProvider.System.GetUtcNow();
    user.LockoutEnabled = false;
    user.LockoutEnd = null;
    var result = await userManager.UpdateAsync(user);
    if (!result.Succeeded)
    {
        cliLogger.LogCritical("Failed to approve account for {Email}: {Errors}", email, string.Join("; ", result.Errors.Select(e => e.Description)));
        await app.DisposeAsync();
        return 1;
    }

    cliLogger.LogInformation("{Email} account is now approved.", email);
    await app.DisposeAsync();
    return 0;
}

// A Platform Admin is meant to be a purely instance-wide identity (ADR-0023) — but self-
// registration (the only account-creation path there is) always creates an organization and
// makes the registrant its Admin (see AccountService.RegisterAsync), so a bootstrapped Platform
// Admin inherits tenant-scoped membership as a side effect, and the sidebar shows Dashboard/Work
// Queue/etc. for them exactly as it would for any ordinary org Admin — not the "admin-only"
// account the operator actually wants. This strips that membership directly (bypassing
// MembershipService/SoleAdminGuard deliberately: those exist to stop an org from ending up with
// zero admins through its own UI, not to block a deploy-tier operator decision to make a specific
// identity platform-only). The organization itself is left untouched and orphaned-admin-safe —
// still visible and manageable from /Platform/Organizations if it needs cleanup.
if (args.Length >= 2 && args[0] == "remove-organization-membership")
{
    var email = args[1];
    var cliLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("FlowOps.Startup");
    using var scope = app.Services.CreateScope();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var dbContext = scope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
    var user = await userManager.FindByEmailAsync(email);
    if (user is null)
    {
        cliLogger.LogCritical("No user found with email {Email}.", email);
        await app.DisposeAsync();
        return 1;
    }

    var memberships = await dbContext.OrganizationMemberships
        .Where(m => m.UserId == user.Id)
        .ToListAsync();
    dbContext.OrganizationMemberships.RemoveRange(memberships);
    await dbContext.SaveChangesAsync();

    cliLogger.LogInformation("Removed {Count} organization membership(s) for {Email}.", memberships.Count, email);
    await app.DisposeAsync();
    return 0;
}

// Phase 15 / ADR-0013: must run before anything that reads Request.Scheme/IsHttps — including the
// security-headers middleware below (harmless either way there) and, much more importantly, every
// cookie issued once authentication/antiforgery middleware runs. First middleware in the pipeline,
// full stop.
app.UseForwardedHeaders();

// CLAUDE.md §7.3 / ADR-0007 / ADR-0014: the same migrate-then-seed sequence the standalone
// `init-database` mode above runs explicitly, kept here as a fallback for environments with no
// separate pre-deploy step (local `dotnet run`, the Docker Compose `app` service). On Render,
// where Pre-Deploy already did this work, both checks below resolve as fast no-ops (nothing
// pending to migrate, tickets already exist) rather than the multi-minute cost of a full demo
// seed — this is what actually fixes the health-check timeout ADR-0014 documents. Exceptions are
// never caught here either: a failure still stops the host from starting, exactly as before.
await InitializeDatabaseAsync(app.Services, app.Configuration, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("FlowOps.Startup"));

// CLAUDE.md §12: HSTS in Production only — an environment-composition choice (CLAUDE.md §13), not
// a business-code branch. Must run after UseForwardedHeaders so Request.IsHttps already reflects
// the original client scheme by the time this checks it.
if (app.Environment.IsProduction())
{
    app.UseHsts();
}

// CLAUDE.md §12: security response headers on every response, including error pages, health
// checks, and rate-limited rejections. Registered via OnStarting rather than set directly here,
// because UseExceptionHandler's Response.Clear() on an unhandled exception would otherwise wipe
// headers set earlier in the pipeline; an OnStarting callback survives that clear and still fires
// exactly once, right before the first byte of the actual response goes out.
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["X-Frame-Options"] = "DENY";
        headers["Permissions-Policy"] =
            "accelerometer=(), camera=(), geolocation=(), gyroscope=(), microphone=(), payment=(), usb=()";
        // Written for the application's actual current surface (CLAUDE.md §11.1): minimal
        // JavaScript, no external CDN, local CSS only, no iframes. Tighten further only if that
        // changes; loosen only with a documented reason — not for hypothetical future features.
        // font-src 'self' added for the self-hosted Geist/Geist Mono @font-face files under
        // wwwroot/fonts — no CDN, so this stays same-origin only, consistent with the rule above.
        // script-src 'self' (Phase 24A-Extension, ADR-0025): permits loading the one self-hosted
        // script (/js/pending-approval.js) that polls account-approval status — still no inline
        // script (no 'unsafe-inline'), no CDN, no third-party origin of any kind.
        headers["Content-Security-Policy"] =
            "default-src 'none'; script-src 'self'; style-src 'self'; font-src 'self'; connect-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
        return Task.CompletedTask;
    });

    await next();
});

// CLAUDE.md §11.3: one global exception handler, not scattered try/catch. Domain-rule violations,
// authorization denials, and concurrency conflicts are all already caught explicitly inside the
// PageModels that can produce them (Tickets/Create.cshtml.cs, Tickets/Details.cshtml.cs) — they
// never reach this handler, which only ever sees a genuinely unexpected exception.
// ExceptionHandlerMiddleware logs the exception itself at Error before re-executing "/Error";
// HttpContext.TraceIdentifier is the correlation id shown to the caller and present on that same
// log entry, so an operator can match one to the other without any custom plumbing.
app.UseExceptionHandler("/Error");

// Phase 12: exists only when a test explicitly opts in via configuration — never in Development or
// Production — so Web.Tests can drive a genuine unhandled exception through the real pipeline
// configured above and prove UseExceptionHandler actually catches it, without adding a
// permanently-reachable diagnostic endpoint to the application itself.
if (builder.Configuration.GetValue<bool>("Testing:EnableDiagnosticThrowEndpoint"))
{
    app.Map("/Testing/Throw", throwApp => throwApp.Run(_ =>
        throw new InvalidOperationException("Deliberate exception for Phase 12 exception-handler verification.")));
}

// Phase 15: same test-only-opt-in shape as the endpoint above — lets Web.Tests prove
// UseForwardedHeaders actually flips Request.IsHttps/Scheme, rather than asserting it indirectly
// through a cookie's Secure flag and hoping nothing else explains the result.
if (builder.Configuration.GetValue<bool>("Testing:EnableDiagnosticSchemeEndpoint"))
{
    app.Map("/Testing/Scheme", schemeApp => schemeApp.Run(context =>
        context.Response.WriteAsync($"{context.Request.Scheme}|{context.Request.IsHttps}")));
}

app.UseRateLimiter();

// Phase 11: serves wwwroot/flowops.css, the project's one locally-hosted stylesheet (CLAUDE.md
// §11.1 — "No CDN links"). No other static assets exist yet.
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();

// Liveness: the process is up. No dependency checks run here — a database outage should not
// make the liveness probe fail and trigger an unnecessary restart.
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });

// Readiness: can this instance actually serve requests right now (i.e. reach PostgreSQL)?
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
});

app.Run();
return 0;

// Makes the implicit top-level Program class visible to WebApplicationFactory<Program> in
// FlowOps.Web.Tests (CLAUDE.md §15) — a standard ASP.NET Core testing convention, not new
// behavior. Also carries InitializeDatabaseAsync as a real, directly-testable static method
// (rather than a top-level local function, which Web.Tests could not call directly) — see that
// method's own doc comment.
public partial class Program
{
    /// <summary>
    /// CLAUDE.md §7.3 / ADR-0007 / ADR-0014: the one place the migrate-then-seed sequence is
    /// implemented — called from both the standalone `init-database` mode and the normal startup
    /// path above, so the two can never drift apart, and callable directly from tests without a
    /// subprocess. Startup migration is controlled by FlowOps:Database:ApplyMigrationsOnStartup;
    /// demo seeding by FlowOps:Demo:Enabled (validated at options-binding time, above, so
    /// DemoOptions.Enabled is never true here without a persona password already having been
    /// confirmed present). Neither failure is caught here — both callers decide what "failed"
    /// means for them (crash the host vs. exit non-zero).
    /// </summary>
    public static async Task InitializeDatabaseAsync(IServiceProvider services, IConfiguration configuration, ILogger logger)
    {
        if (configuration.GetValue<bool>("FlowOps:Database:ApplyMigrationsOnStartup"))
        {
            using var migrationScope = services.CreateScope();
            var migrationDbContext = migrationScope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
            await migrationDbContext.Database.MigrateAsync();
        }

        var demoOptions = services.GetRequiredService<DemoOptions>();
        if (demoOptions.Enabled)
        {
            logger.LogInformation("FlowOps:Demo:Enabled is true — running demo data seeder.");
            using var demoScope = services.CreateScope();
            var demoSeeder = demoScope.ServiceProvider.GetRequiredService<DemoDataSeeder>();
            await demoSeeder.SeedAsync();
        }
    }
}
