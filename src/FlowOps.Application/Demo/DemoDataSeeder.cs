using FlowOps.Application.Planning;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Catalog;
using FlowOps.Domain.Directory;
using FlowOps.Domain.Organizations;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Identity;
using FlowOps.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FlowOps.Application.Demo;

/// <summary>
/// CLAUDE.md §14: the public-portfolio demo dataset — a small, curated, deterministic story that
/// shows the current product (roles, teams, projects, sprint planning and history, workflow, SLA and
/// attention signals) rather than a large volume of generated rows. Runs at startup only when
/// <see cref="DemoOptions.Enabled"/> is true (see <c>Program.cs</c>, after migrations).
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotent, keyed on the demo organization.</b> If an organization named
/// <see cref="OrganizationName"/> already exists the seeder does nothing — it never adds to, edits, or
/// removes an existing database, so a database seeded by an older definition keeps what it has. There
/// is deliberately no reset or cleanup here (no destructive startup); see <c>docs/deployment.md</c>
/// for the explicit, manual reset procedure.
/// </para>
/// <para>
/// <b>Deterministic.</b> No randomness: every title, status, and offset below is fixed, and every
/// timestamp is an offset from the single seed moment, so a fresh database always tells the same
/// story. Nothing in the dataset sets a flag directly — the SLA and attention signals shown are the
/// genuine result of backdated timestamps (see the table in <see cref="SeedWorkAsync"/>).
/// </para>
/// <para>
/// Every ticket, transition, comment, sprint action, and carry-forward goes through
/// <see cref="TicketService"/> / <see cref="SprintService"/> — the same authorization, audit, and
/// invariant path a real request takes. Only invariant-free reference data (teams, categories,
/// projects, users, memberships) is inserted directly. If a state cannot be reached through legal
/// transitions the seeder is right to fail.
/// </para>
/// </remarks>
public sealed class DemoDataSeeder
{
    public const string OrganizationName = "Demo Organization";

    private readonly FlowOpsDbContext _dbContext;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly DemoOptions _options;
    private readonly TimeProvider _realTimeProvider;
    private readonly ILogger<DemoDataSeeder> _logger;

    public DemoDataSeeder(
        FlowOpsDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        DemoOptions options,
        TimeProvider realTimeProvider,
        ILogger<DemoDataSeeder> logger)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _options = options;
        _realTimeProvider = realTimeProvider;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        if (await _dbContext.Organizations.AnyAsync(o => o.Name == OrganizationName, cancellationToken))
        {
            _logger.LogInformation("Demo seeding skipped: the demo organization already exists.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.PersonaPassword))
        {
            // Defensive: Program.cs already validates this on start via ValidateOnStart.
            throw new InvalidOperationException("FlowOps:Demo:PersonaPassword must be set to seed demo data.");
        }

        // CLAUDE.md §14: "inside a transaction". EnableRetryOnFailure requires a user-initiated
        // transaction to be wrapped in an execution strategy; a retried attempt starts this whole
        // delegate over, so a partial failure never leaves partial state behind.
        var strategy = _dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

            var seedTime = _realTimeProvider.GetUtcNow();
            var organization = await CreateOrganizationAsync(seedTime, cancellationToken);
            var teams = await CreateTeamsAsync(organization.Id, seedTime, cancellationToken);
            var categories = await CreateCategoriesAsync(teams, seedTime, cancellationToken);
            var projects = await CreateProjectsAsync(organization.Id, seedTime, cancellationToken);
            var users = await CreateUsersAsync(organization.Id, teams, seedTime, cancellationToken);

            var clock = new SeederClock(seedTime);
            // Phase 30 (ADR-0035): seeded assignments/comments must never send a real email,
            // regardless of the deployment's configured provider — a fresh `docker compose up` or
            // first-ever Render deploy must not blast the demo personas' inboxes. A LogEmailSender
            // is constructed directly here (never DI-resolved) for exactly that reason, the same
            // way this method already uses its own SeederClock instead of the DI-resolved
            // TimeProvider.
            var noOpEmailSender = new FlowOps.Infrastructure.Email.LogEmailSender(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<FlowOps.Infrastructure.Email.LogEmailSender>.Instance);
            var script = new Script(
                new TicketService(_dbContext, clock, noOpEmailSender, new FlowOps.Infrastructure.Email.EmailOptions()),
                new SprintService(_dbContext, clock),
                clock,
                cancellationToken);

            await SeedWorkAsync(script, teams, categories, projects, users, seedTime);

            await transaction.CommitAsync(cancellationToken);
            _logger.LogInformation("Demo seeding completed.");
        });
    }

    // ---- Reference data -----------------------------------------------------------------------

    private sealed record DemoTeams(Team ServiceDesk, Team ApplicationSupport);

    private sealed record DemoCategories(Category Hardware, Category Access, Category Incidents, Category Changes);

    private sealed record DemoProjects(Project LaptopRefresh, Project BillingPortal);

    private async Task<Organization> CreateOrganizationAsync(DateTimeOffset seedTime, CancellationToken cancellationToken)
    {
        var organization = new Organization(0, OrganizationName, seedTime);
        _dbContext.Organizations.Add(organization);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return organization;
    }

    private async Task<DemoTeams> CreateTeamsAsync(int organizationId, DateTimeOffset seedTime, CancellationToken cancellationToken)
    {
        var teams = new DemoTeams(
            new Team(0, organizationId, "Service Desk", seedTime),
            new Team(0, organizationId, "Application Support", seedTime));
        _dbContext.Teams.AddRange(teams.ServiceDesk, teams.ApplicationSupport);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return teams;
    }

    private async Task<DemoCategories> CreateCategoriesAsync(DemoTeams teams, DateTimeOffset seedTime, CancellationToken cancellationToken)
    {
        var categories = new DemoCategories(
            new Category(0, teams.ServiceDesk.Id, "Hardware & Devices", WorkType.ServiceRequest, seedTime),
            new Category(0, teams.ServiceDesk.Id, "Access & Accounts", WorkType.ServiceRequest, seedTime),
            new Category(0, teams.ApplicationSupport.Id, "Application Incidents", WorkType.Incident, seedTime),
            new Category(0, teams.ApplicationSupport.Id, "Application Changes", WorkType.Task, seedTime));
        _dbContext.Categories.AddRange(categories.Hardware, categories.Access, categories.Incidents, categories.Changes);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return categories;
    }

    private async Task<DemoProjects> CreateProjectsAsync(int organizationId, DateTimeOffset seedTime, CancellationToken cancellationToken)
    {
        var projects = new DemoProjects(
            new Project(0, organizationId, "Laptop Refresh 2026", seedTime),
            new Project(0, organizationId, "Billing Portal Stabilization", seedTime));
        _dbContext.Projects.AddRange(projects.LaptopRefresh, projects.BillingPortal);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return projects;
    }

    /// <summary>One seeded account plus the <see cref="CurrentUser"/> shape every service call needs.</summary>
    private sealed record SeededUser(Guid Id, CurrentUser AsCurrentUser);

    private sealed record DemoUsers(SeededUser Admin, SeededUser Manager, SeededUser Agent, SeededUser Viewer);

    /// <summary>
    /// The four <see cref="DemoPersonas"/>, each a member of both teams (the Manager manages both;
    /// <c>TicketAccessPolicy.CanView</c> needs team membership for every non-Admin role, and an
    /// Admin needs it so an assignment to them satisfies TICKET-INV-03). All are
    /// <c>IsDemoProtected</c> and pre-approved.
    /// </summary>
    private async Task<DemoUsers> CreateUsersAsync(int organizationId, DemoTeams teams, DateTimeOffset seedTime, CancellationToken cancellationToken)
    {
        var teamIds = new HashSet<int> { teams.ServiceDesk.Id, teams.ApplicationSupport.Id };
        var byRole = new Dictionary<UserRole, SeededUser>();

        foreach (var persona in DemoPersonas.All)
        {
            var user = await CreateUserAsync(persona.DisplayName, persona.Email, persona.Role, seedTime, cancellationToken);
            _dbContext.OrganizationMemberships.Add(new OrganizationMembership(0, organizationId, user.Id, persona.Role, seedTime));

            var isManager = persona.Role == UserRole.Manager;
            foreach (var team in new[] { teams.ServiceDesk, teams.ApplicationSupport })
            {
                _dbContext.TeamMembers.Add(new TeamMember(team.Id, user.Id, isManager, seedTime));
            }

            byRole[persona.Role] = new SeededUser(
                user.Id,
                new CurrentUser(user.Id, organizationId, persona.Role, new HashSet<int>(teamIds), isManager ? new HashSet<int>(teamIds) : new HashSet<int>()));
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return new DemoUsers(byRole[UserRole.Admin], byRole[UserRole.Manager], byRole[UserRole.Agent], byRole[UserRole.Viewer]);
    }

    private async Task<ApplicationUser> CreateUserAsync(string displayName, string email, UserRole role, DateTimeOffset seedTime, CancellationToken cancellationToken)
    {
        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = displayName,
            IsActive = true,
            IsDemoProtected = true,
            // ADR-0024: demo personas are pre-approved — a visitor must be able to sign in with the
            // published demo credentials immediately, never land in a Pending state.
            RegistrationApprovedAt = seedTime,
        };

        var createResult = await _userManager.CreateAsync(user, _options.PersonaPassword!);
        if (!createResult.Succeeded)
        {
            throw new InvalidOperationException(
                $"Failed to seed demo user {email}: {string.Join("; ", createResult.Errors.Select(e => e.Description))}");
        }

        var roleResult = await _userManager.AddToRoleAsync(user, role.ToString());
        if (!roleResult.Succeeded)
        {
            throw new InvalidOperationException(
                $"Failed to assign role {role} to demo user {email}: {string.Join("; ", roleResult.Errors.Select(e => e.Description))}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return user;
    }

    // ---- Work: sprints and tickets ------------------------------------------------------------

    /// <summary>
    /// 22 tickets, 3 sprints, in one project-management story plus a small support queue. Times are
    /// offsets from <c>T</c> (the seed moment).
    /// <code>
    /// Laptop Refresh 2026 (Service Desk)
    ///   Sprint 1  Completed  6 tickets: 4 done, 2 unfinished  → snapshots frozen at completion
    ///        └─ explicit carry-forward of the 2 unfinished ─┐
    ///   Sprint 2  Active     2 carried + 7 new = 9 tickets   ← board: Backlog 2 · Open 2 · In progress 2 · Pending 1 · Done 2
    ///   Sprint 3  Planned    3 tickets
    /// Billing Portal Stabilization (Application Support): 4 tickets, no sprints
    /// No project: 2 tickets
    /// </code>
    /// Persistent attention examples (they only get older, so they stay true at any later time):
    /// SLA breached (most open tickets older than their target), Overdue (past DueDate), Unassigned
    /// urgent (Billing invoice export), Stalled in progress / Pending, Aging (the 40-day-old Low
    /// ticket), Reopened (the payment-webhook ticket). The single "SLA at risk" example (the Critical
    /// BIOS ticket) is a short-lived bonus and is not relied on.
    /// </summary>
    private static async Task SeedWorkAsync(
        Script s,
        DemoTeams teams,
        DemoCategories cats,
        DemoProjects projects,
        DemoUsers users,
        DateTimeOffset t)
    {
        var admin = users.Admin;
        var manager = users.Manager;
        var agent = users.Agent;
        var today = DateOnly.FromDateTime(t.UtcDateTime);
        var laptop = projects.LaptopRefresh.Id;
        var billing = projects.BillingPortal.Id;
        var sd = teams.ServiceDesk;
        var app = teams.ApplicationSupport;

        // ---- Laptop Refresh: Sprint 1 (completed) ---------------------------------------------
        var t0 = t.AddDays(-22);
        var sprint1 = await s.CreateSprintAsync(manager, laptop, "Sprint 1 - Inventory and imaging", today.AddDays(-22), today.AddDays(-9), t0);

        var a1 = await s.CreateAsync(t0.AddMinutes(10), new TicketSeed(
            "Audit laptop inventory against the asset register", "Reconcile the 120 laptops on the register with what is actually deployed, so the refresh order matches reality.",
            Priority.Medium, WorkType.Task, sd, cats.Hardware, laptop, manager));
        var a2 = await s.CreateAsync(t0.AddMinutes(20), new TicketSeed(
            "Build Windows 11 standard image for Latitude 5450", "Standard image with drivers, BitLocker, and the baseline application set, tested before the first delivery.",
            Priority.High, WorkType.Task, sd, cats.Hardware, laptop, manager));
        var a3 = await s.CreateAsync(t0.AddMinutes(30), new TicketSeed(
            "Order 40 replacement laptops for Q4 refresh", "Raise the purchase order for the first refresh wave against the approved budget.",
            Priority.Medium, WorkType.ServiceRequest, sd, cats.Hardware, laptop, admin));
        var a4 = await s.CreateAsync(t0.AddMinutes(40), new TicketSeed(
            "Agree handover slots with Finance and HR", "Book 30-minute handover slots so each person swaps devices without losing a working day.",
            Priority.Low, WorkType.Task, sd, cats.Hardware, laptop, manager));
        var a5 = await s.CreateAsync(t0.AddMinutes(50), new TicketSeed(
            "Migrate user profiles for the Finance pilot group", "Move profiles, mapped drives, and browser data for the five-person pilot group to the new image.",
            Priority.Medium, WorkType.Task, sd, cats.Hardware, laptop, manager));
        var a6 = await s.CreateAsync(t0.AddMinutes(60), new TicketSeed(
            "Collect returned laptops from the Sales floor", "Sales have replaced their devices; collect the old ones for wiping and disposal.",
            Priority.Medium, WorkType.ServiceRequest, sd, cats.Hardware, laptop, manager, Due: t.AddDays(-6)));

        foreach (var id in new[] { a1, a2, a3, a4, a5, a6 })
        {
            await s.PlanAsync(id, sprint1, t0.AddHours(2), manager);
            await s.PullAsync(id, t0.AddHours(2), manager);
        }

        await s.StartSprintAsync(sprint1, t0.AddHours(3), manager);

        var c1 = t0.AddMinutes(10);
        await s.AssignAsync(a1, c1.AddHours(4), manager, agent);
        await s.StartAsync(a1, c1.AddHours(5), agent);
        await s.ResolveAsync(a1, c1.AddHours(30), agent, Resolution.Completed, "Inventory reconciled; 12 unrecorded devices added to the register.");
        await s.CloseAsync(a1, c1.AddDays(2), manager);

        var c2 = t0.AddMinutes(20);
        await s.AssignAsync(a2, c2.AddHours(3), manager, agent);
        await s.StartAsync(a2, c2.AddHours(3.5), agent);
        await s.ResolveAsync(a2, c2.AddHours(7), agent, Resolution.Completed, "Image built, tested on two hardware revisions, and published to the deployment server.");
        await s.CloseAsync(a2, c2.AddDays(1), manager);

        var c3 = t0.AddMinutes(30);
        await s.AssignAsync(a3, c3.AddHours(4), admin, manager);
        await s.StartAsync(a3, c3.AddHours(6), manager);
        await s.ResolveAsync(a3, c3.AddHours(20), manager, Resolution.Completed, "Purchase order approved and placed with the supplier; delivery in two weeks.");

        var c4 = t0.AddMinutes(40);
        await s.AssignAsync(a4, c4.AddDays(1), manager, agent);
        await s.StartAsync(a4, c4.AddDays(1).AddHours(1), agent);
        await s.ResolveAsync(a4, c4.AddHours(50), agent, Resolution.Completed, "Handover slots agreed with both departments.");

        var c5 = t0.AddMinutes(50);
        await s.AssignAsync(a5, c5.AddHours(5), manager, agent);
        await s.StartAsync(a5, c5.AddDays(1), agent);
        await s.CommentAsync(a5, c5.AddDays(3), agent, "Two of five profiles migrated; the rest need sign-off from the Finance owner.");

        await s.AssignAsync(a6, t0.AddMinutes(60).AddDays(1), manager, manager);

        await s.CompleteSprintAsync(sprint1, t.AddDays(-9), manager);

        // ---- Laptop Refresh: Sprint 2 (active) and Sprint 3 (planned) -------------------------
        var sprint2 = await s.CreateSprintAsync(manager, laptop, "Sprint 2 - Pilot devices", today.AddDays(-8), today.AddDays(5), t.AddDays(-10));
        var sprint3 = await s.CreateSprintAsync(manager, laptop, "Sprint 3 - Finance rollout", today.AddDays(6), today.AddDays(19), t.AddDays(-10));
        await s.StartSprintAsync(sprint2, t.AddDays(-8), manager);

        // Explicit carry-forward: the two unfinished Sprint 1 tickets move to Sprint 2. Moving a
        // ticket into a sprint puts it in that sprint's backlog, so the two in-flight tickets are then
        // pulled onto the board — the same two steps a manager takes in the UI.
        await s.CarryForwardAsync(sprint1, t.AddDays(-8).AddHours(1), manager, expectedMoved: 2);
        await s.PullAsync(a5, t.AddDays(-8).AddHours(1), manager);
        await s.PullAsync(a6, t.AddDays(-8).AddHours(1), manager);

        // Pending, stalled: waiting on a vendor for four days.
        var w1Created = t.AddHours(-100);
        var w1 = await s.CreateAsync(w1Created, new TicketSeed(
            "Confirm Windows licence entitlement with the vendor", "The new devices ship with OEM licences; confirm the volume entitlement covers the full refresh.",
            Priority.Medium, WorkType.Task, sd, cats.Hardware, laptop, manager));
        await s.PlanAsync(w1, sprint2, w1Created.AddMinutes(10), manager);
        await s.PullAsync(w1, w1Created.AddMinutes(20), manager);
        await s.AssignAsync(w1, w1Created.AddMinutes(30), manager, agent);
        await s.StartAsync(w1, w1Created.AddHours(1), agent);
        await s.CommentAsync(w1, w1Created.AddHours(2), agent, "Vendor asked for the tenant ID; waiting on their entitlement report.");
        await s.HoldAsync(w1, w1Created.AddHours(3), agent, "Waiting for the vendor to confirm the licence entitlement.");

        // Done: resolved inside the sprint.
        var d2Created = t.AddHours(-40);
        var d2 = await s.CreateAsync(d2Created, new TicketSeed(
            "Publish the laptop handover guide on the intranet", "One page: what to back up, what happens on handover day, who to call.",
            Priority.Low, WorkType.Task, sd, cats.Hardware, laptop, manager));
        await s.PlanAsync(d2, sprint2, d2Created.AddMinutes(5), manager);
        await s.PullAsync(d2, d2Created.AddMinutes(10), manager);
        await s.AssignAsync(d2, d2Created.AddHours(1), manager, agent);
        await s.StartAsync(d2, d2Created.AddHours(2), agent);
        await s.ResolveAsync(d2, d2Created.AddHours(10), agent, Resolution.Completed, "Guide published and linked from the IT help page.");

        var d1Created = t.AddHours(-22);
        var d1 = await s.CreateAsync(d1Created, new TicketSeed(
            "Unbox and asset-tag the first delivery of 10 laptops", "Check serial numbers against the order, tag, and shelve for imaging.",
            Priority.Medium, WorkType.ServiceRequest, sd, cats.Hardware, laptop, manager));
        await s.PlanAsync(d1, sprint2, d1Created.AddMinutes(5), manager);
        await s.PullAsync(d1, d1Created.AddMinutes(10), manager);
        await s.AssignAsync(d1, d1Created.AddMinutes(30), manager, agent);
        await s.StartAsync(d1, d1Created.AddHours(1), agent);
        await s.ResolveAsync(d1, d1Created.AddHours(3), agent, Resolution.Completed, "All 10 devices matched the order, tagged, and shelved.");

        // In progress, Critical, opened about 3.4 hours ago: inside its SLA "at risk" window for a
        // short time after seeding (a bonus example, not one the dataset depends on).
        var p1Created = t.AddMinutes(-204);
        var p1 = await s.CreateAsync(p1Created, new TicketSeed(
            "Executive laptop will not boot after BIOS update", "A pilot device stops at the boot logo after the firmware update. The user needs a working machine today.",
            Priority.Critical, WorkType.Incident, sd, cats.Hardware, laptop, agent));
        await s.PlanAsync(p1, sprint2, p1Created.AddMinutes(5), manager);
        await s.PullAsync(p1, p1Created.AddMinutes(5), manager);
        await s.AssignAsync(p1, p1Created.AddMinutes(8), manager, agent);
        await s.StartAsync(p1, p1Created.AddMinutes(10), agent);
        await s.CommentAsync(p1, p1Created.AddMinutes(25), agent, "Loaner laptop issued. Rolling the BIOS back to the previous version.");

        // Open and assigned to the manager.
        var o1Created = t.AddHours(-9);
        var o1 = await s.CreateAsync(o1Created, new TicketSeed(
            "Prepare BitLocker recovery key escrow for new devices", "Make sure recovery keys for the new laptops are escrowed before they are handed over.",
            Priority.Medium, WorkType.ServiceRequest, sd, cats.Hardware, laptop, manager));
        await s.PlanAsync(o1, sprint2, o1Created.AddMinutes(5), manager);
        await s.PullAsync(o1, o1Created.AddMinutes(10), manager);
        await s.AssignAsync(o1, o1Created.AddHours(1), manager, manager);

        // Sprint 2 backlog: planned into the sprint, not yet pulled onto the board.
        var b1Created = t.AddHours(-30);
        var b1 = await s.CreateAsync(b1Created, new TicketSeed(
            "Decide on a docking station model for hot-desk areas", "Compare two docking stations against the new laptop model and pick one for the hot-desk zones.",
            Priority.Low, WorkType.Task, sd, cats.Hardware, laptop, admin));
        await s.PlanAsync(b1, sprint2, b1Created.AddMinutes(5), manager);

        var b2Created = t.AddHours(-10);
        var b2 = await s.CreateAsync(b2Created, new TicketSeed(
            "Arrange secure disposal of retired laptops", "Book a certified disposal service and agree the certificate of destruction.",
            Priority.Medium, WorkType.Task, sd, cats.Hardware, laptop, manager));
        await s.PlanAsync(b2, sprint2, b2Created.AddMinutes(5), manager);

        // Sprint 3 (planned).
        var n1Created = t.AddHours(-6);
        var n1 = await s.CreateAsync(n1Created, new TicketSeed(
            "Roll out laptops to the Finance pilot group", "Hand over the first five refreshed laptops to Finance during their agreed slots.",
            Priority.Medium, WorkType.Task, sd, cats.Hardware, laptop, manager));
        await s.PlanAsync(n1, sprint3, n1Created.AddMinutes(5), manager);

        var n2Created = t.AddHours(-5);
        var n2 = await s.CreateAsync(n2Created, new TicketSeed(
            "Collect and wipe the legacy Finance laptops", "Collect the replaced devices and wipe them to the disposal standard.",
            Priority.Medium, WorkType.Task, sd, cats.Hardware, laptop, manager));
        await s.PlanAsync(n2, sprint3, n2Created.AddMinutes(5), manager);

        var n3Created = t.AddHours(-4);
        var n3 = await s.CreateAsync(n3Created, new TicketSeed(
            "Run the post-rollout satisfaction survey", "Send a short survey to the pilot group after two weeks on the new laptops.",
            Priority.Low, WorkType.Task, sd, cats.Hardware, laptop, admin,
            PlannedStart: t.AddDays(7), Due: t.AddDays(18)));
        await s.PlanAsync(n3, sprint3, n3Created.AddMinutes(5), manager);

        // ---- Billing Portal Stabilization: ordinary project tickets, no sprints ----------------
        // Critical and unassigned for three days: SLA breached and unassigned-urgent.
        await s.CreateAsync(t.AddDays(-3), new TicketSeed(
            "Invoice PDF export returns a 500 error for multi-page invoices", "Finance cannot export invoices longer than one page. The error started after the last release.",
            Priority.Critical, WorkType.Incident, app, cats.Incidents, billing, agent));

        // In progress and past its due date: Overdue and SLA breached.
        var bi2Created = t.AddHours(-30);
        var bi2 = await s.CreateAsync(bi2Created, new TicketSeed(
            "Customers see duplicate charges on monthly statements", "Several customers report the same charge appearing twice on their statement.",
            Priority.High, WorkType.Incident, app, cats.Incidents, billing, agent, Due: t.AddHours(-6)));
        await s.AssignAsync(bi2, bi2Created.AddMinutes(30), manager, agent);
        await s.StartAsync(bi2, bi2Created.AddHours(1), agent);
        await s.CommentAsync(bi2, bi2Created.AddHours(2), agent, "Confirmed: the retry job posts the charge twice when the gateway times out. Drafting a fix.", isInternal: true);

        // Resolved, then reopened: Reopened, with the full comment trail (investigation, resolution, escalation).
        var bi3Created = t.AddDays(-9);
        var bi3 = await s.CreateAsync(bi3Created, new TicketSeed(
            "Payment webhook retries failing after the gateway upgrade", "Payment confirmations are not reaching the portal, so orders stay unpaid.",
            Priority.High, WorkType.Incident, app, cats.Incidents, billing, manager));
        await s.AssignAsync(bi3, bi3Created.AddMinutes(30), manager, agent);
        await s.StartAsync(bi3, bi3Created.AddHours(1), agent);
        await s.CommentAsync(bi3, bi3Created.AddHours(5), agent, "The gateway shortened its timeout; our retry interval is now too long.");
        await s.ResolveAsync(bi3, t.AddDays(-8), agent, Resolution.Fixed, "Raised the retry timeout to match the gateway's new limit.");
        await s.ReopenAsync(bi3, t.AddDays(-2), manager, "Failed again after the weekend batch run.");
        await s.CommentAsync(bi3, t.AddDays(-2).AddHours(1), agent, "Reproduced on the batch path. Escalated to the gateway vendor.");

        // A healthy, planned piece of work: assigned, with future planned start and due dates.
        var bi4Created = t.AddHours(-8);
        var bi4 = await s.CreateAsync(bi4Created, new TicketSeed(
            "Add the tax registration number to the invoice template", "Invoices for EU customers must show the company's tax registration number.",
            Priority.Medium, WorkType.Task, app, cats.Changes, billing, admin,
            PlannedStart: t.AddDays(1), Due: t.AddDays(7)));
        await s.AssignAsync(bi4, bi4Created.AddHours(1), admin, admin);

        // ---- No project ---------------------------------------------------------------------
        // Low priority, untouched for 40 days: Aging.
        await s.CreateAsync(t.AddDays(-40), new TicketSeed(
            "Retire the legacy shared printer queue on floor 3", "The old print server is being decommissioned; remove the queue and point staff to the new printers.",
            Priority.Low, WorkType.ServiceRequest, sd, cats.Hardware, null, agent));

        // A routine request resolved well inside its target.
        var u2Created = t.AddDays(-2);
        var u2 = await s.CreateAsync(u2Created, new TicketSeed(
            "Reset MFA for a new finance starter", "The new starter lost their phone before enrolling; reset their authenticator.",
            Priority.Medium, WorkType.ServiceRequest, sd, cats.Access, null, agent));
        await s.AssignAsync(u2, u2Created.AddMinutes(5), manager, agent);
        await s.StartAsync(u2, u2Created.AddMinutes(10), agent);
        await s.ResolveAsync(u2, u2Created.AddMinutes(45), agent, Resolution.Fixed, "MFA reset and the starter re-enrolled their authenticator.");
    }

    private sealed record TicketSeed(
        string Title,
        string Description,
        Priority Priority,
        WorkType WorkType,
        Team Team,
        Category Category,
        int? ProjectId,
        SeededUser Requester,
        DateTimeOffset? PlannedStart = null,
        DateTimeOffset? Due = null);

    /// <summary>Thin timeline helper: each step sets the seeder clock, then calls the real service.</summary>
    private sealed class Script(TicketService tickets, SprintService sprints, SeederClock clock, CancellationToken ct)
    {
        public async Task<int> CreateAsync(DateTimeOffset at, TicketSeed t)
        {
            clock.Set(at);
            var request = new CreateTicketRequest(t.Title, t.Description, t.WorkType, t.Priority, t.Team.Id, t.Category.Id, t.ProjectId, t.PlannedStart, t.Due);
            var (id, _) = await tickets.CreateAsync(request, t.Requester.AsCurrentUser, ct);
            return id;
        }

        public Task AssignAsync(int id, DateTimeOffset at, SeededUser by, SeededUser to) =>
            At(at, () => tickets.AssignAsync(id, to.Id, by.AsCurrentUser, ct));

        public Task StartAsync(int id, DateTimeOffset at, SeededUser by) =>
            At(at, () => tickets.StartWorkAsync(id, by.AsCurrentUser, ct));

        public Task HoldAsync(int id, DateTimeOffset at, SeededUser by, string reason) =>
            At(at, () => tickets.PutOnHoldAsync(id, reason, by.AsCurrentUser, ct));

        public Task ResolveAsync(int id, DateTimeOffset at, SeededUser by, Resolution resolution, string notes) =>
            At(at, () => tickets.ResolveAsync(id, resolution, notes, by.AsCurrentUser, ct));

        public Task CloseAsync(int id, DateTimeOffset at, SeededUser by) =>
            At(at, () => tickets.CloseAsync(id, by.AsCurrentUser, ct));

        public Task ReopenAsync(int id, DateTimeOffset at, SeededUser by, string reason) =>
            At(at, () => tickets.ReopenAsync(id, reason, by.AsCurrentUser, ct));

        public Task CommentAsync(int id, DateTimeOffset at, SeededUser by, string body, bool isInternal = false) =>
            At(at, () => tickets.AddCommentAsync(id, body, isInternal, by.AsCurrentUser, ct));

        public Task PlanAsync(int id, int sprintId, DateTimeOffset at, SeededUser by) =>
            At(at, () => tickets.MoveToSprintAsync(id, sprintId, by.AsCurrentUser, ct));

        public Task PullAsync(int id, DateTimeOffset at, SeededUser by) =>
            At(at, () => tickets.PullFromSprintBacklogAsync(id, by.AsCurrentUser, ct));

        public async Task<int> CreateSprintAsync(SeededUser by, int projectId, string name, DateOnly start, DateOnly end, DateTimeOffset at)
        {
            clock.Set(at);
            var result = await sprints.CreateSprintAsync(by.AsCurrentUser, projectId, name, start, end, ct);
            return Require(result, "create sprint").SprintId!.Value;
        }

        public async Task StartSprintAsync(int sprintId, DateTimeOffset at, SeededUser by)
        {
            clock.Set(at);
            Require(await sprints.StartSprintAsync(by.AsCurrentUser, sprintId, ct), "start sprint");
        }

        public async Task CompleteSprintAsync(int sprintId, DateTimeOffset at, SeededUser by)
        {
            clock.Set(at);
            Require(await sprints.CompleteSprintAsync(by.AsCurrentUser, sprintId, ct), "complete sprint");
        }

        public async Task CarryForwardAsync(int completedSprintId, DateTimeOffset at, SeededUser by, int expectedMoved)
        {
            clock.Set(at);
            var result = await sprints.CarryForwardAsync(by.AsCurrentUser, completedSprintId, ct);
            if (!result.Succeeded || result.Moved != expectedMoved)
            {
                throw new InvalidOperationException($"Demo seed carry-forward moved {result.Moved} tickets (expected {expectedMoved}): {result.Error}");
            }
        }

        private async Task At(DateTimeOffset at, Func<Task> action)
        {
            clock.Set(at);
            await action();
        }

        private static SprintMutationResult Require(SprintMutationResult result, string what) =>
            result.Succeeded ? result : throw new InvalidOperationException($"Demo seed could not {what}: {result.Error}");
    }

    /// <summary>A settable clock so the seeder can back- and forward-date each step of a ticket's or
    /// sprint's history deterministically through the real services.</summary>
    private sealed class SeederClock(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _now = initial;

        public void Set(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
