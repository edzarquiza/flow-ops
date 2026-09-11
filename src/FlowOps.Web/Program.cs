using System.Threading.RateLimiting;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Attention;
using FlowOps.Infrastructure.Identity;
using FlowOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// Phase 4: identity, cookie authentication, roles, and the coarse authorization gate. No ticket
// endpoints, no Razor Pages beyond the authentication boundary itself — those are later phases
// (CLAUDE.md §19.2).
var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("FlowOps")
    ?? throw new InvalidOperationException(
        "Missing ConnectionStrings:FlowOps configuration. The app must fail fast at startup on " +
        "missing configuration, not fail later at first request (CLAUDE.md §13).");

builder.Services.AddDbContext<FlowOpsDbContext>(options =>
    options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());

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
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0,
        });
    });
});

// AUTH-RULE-04: resolves the Domain's CurrentUser from the authoritative persistence model —
// see CurrentUserAccessor's own doc comment for why this is not sourced from cookie claims.
builder.Services.AddScoped<CurrentUserAccessor>();

// TICKET-INV-10: the Domain never calls DateTime.UtcNow — every timestamp is passed in by the
// Application layer from this clock. Registered here because the composition root is the one
// place CLAUDE.md §4.4 permits the real clock to be named. Tests substitute a fake.
builder.Services.AddSingleton(TimeProvider.System);

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
    options.Conventions.AllowAnonymousToPage("/Account/AccessDenied");
    // Phase 12 (§11.3): the exception-handler landing page — an unhandled exception can happen
    // before authentication even runs, so this must be reachable regardless of session state.
    options.Conventions.AllowAnonymousToPage("/Error");

    // AUTH-RULE-01 / §6.1: the coarse gate — "Manage users/teams/categories/SLA" is Admin-only.
    // This is deliberately the *only* role-restricted folder added in Phase 4; it exists to prove
    // the coarse-gate mechanism, not to implement admin features (later phases).
    options.Conventions.AuthorizeFolder("/Admin", "AdminOnly");
});

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("AdminOnly", policy => policy.RequireRole(WellKnownRoles.Admin));

builder.Services
    .AddHealthChecks()
    .AddDbContextCheck<FlowOpsDbContext>("database", tags: ["ready"]);

var app = builder.Build();

// CLAUDE.md §7.3 / ADR-0007: flag-gated startup migration. Runs before any middleware —
// including Data Protection, which persists its keys to this same FlowOpsDbContext in Production
// (see the registration above) and would otherwise be the first thing to touch a table that does
// not exist yet on a freshly created database. Disabled (absent = false) by default, so a plain
// `dotnet run` against an already-migrated developer database is unaffected. A migration failure
// is never caught here: it propagates and stops the host from starting, rather than letting the
// process come up and serve requests against an unknown schema.
if (builder.Configuration.GetValue<bool>("FlowOps:Database:ApplyMigrationsOnStartup"))
{
    using var migrationScope = app.Services.CreateScope();
    var migrationDbContext = migrationScope.ServiceProvider.GetRequiredService<FlowOpsDbContext>();
    await migrationDbContext.Database.MigrateAsync();
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
        // Written for the application's actual current surface (CLAUDE.md §11.1): no JavaScript
        // anywhere, no external CDN, local CSS only, no iframes. Tighten further only if that
        // changes; loosen only with a documented reason — not for hypothetical future features.
        headers["Content-Security-Policy"] =
            "default-src 'none'; style-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
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

// Makes the implicit top-level Program class visible to WebApplicationFactory<Program> in
// FlowOps.Web.Tests (CLAUDE.md §15) — a standard ASP.NET Core testing convention, not new
// behavior.
public partial class Program;
