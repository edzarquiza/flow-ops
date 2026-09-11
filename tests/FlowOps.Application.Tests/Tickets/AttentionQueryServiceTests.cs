using System.Text;
using System.Text.RegularExpressions;
using FlowOps.Application.Tests.Persistence;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Attention;
using FlowOps.Domain.Sla;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FlowOps.Application.Tests.Tickets;

/// <summary>
/// Phase 8 at-risk read path against real PostgreSQL. The centrepiece is the ATTN-RULE-06 superset
/// gate: the SQL prefilter is the one place attention logic is restated outside
/// <see cref="AttentionPolicy"/>, so it is the one place that gets an explicit test proving the
/// restatement never loses a ticket the policy would flag.
/// </summary>
[Collection("Postgres")]
public sealed partial class AttentionQueryServiceTests
{
    /// <summary>The single instant every scenario is evaluated at; tickets are seeded relative to it.</summary>
    private static readonly DateTimeOffset Now = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly AttentionOptions DefaultOptions = new();

    private readonly PostgresFixture _fixture;

    public AttentionQueryServiceTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>
    /// ATTN-RULE-06. Seeds a true positive and a near-miss for each of the eight signals, then
    /// derives the expected set empirically — by running <see cref="AttentionPolicy"/> itself over
    /// every seeded ticket — rather than trusting a hand-written list of which ones "should" fire.
    /// The assertion is containment, not equality: the prefilter may over-include.
    /// </summary>
    [Fact]
    public async Task Prefilter_IsAProvableSupersetOfEverySignalThePolicyFires()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedEverySignalAsync(context);

        // What the authoritative policy says about every seeded ticket, with real event history.
        var allSeeded = await context.Tickets
            .AsNoTracking()
            .Include(t => t.Events)
            .Where(t => t.TeamId == world.TeamId)
            .ToListAsync();

        var configurations = await context.SlaConfigurations.AsNoTracking().ToListAsync();

        var trulyFlagged = allSeeded
            .Where(t => AttentionPolicy.Evaluate(
                t,
                Now,
                DefaultOptions,
                SlaPolicy.ResolveConfiguration(configurations, t.WorkType, t.Priority).RiskThresholdPercent).Count > 0)
            .Select(t => t.Id)
            .ToHashSet();

        // What the SQL prefilter admits.
        var service = new AttentionQueryService(context, new TicketTestData.FixedTimeProvider(Now), DefaultOptions);
        var candidateIds = await service.BuildCandidateQuery(world.Agent, Now, configurations)
            .AsNoTracking()
            .Select(t => t.Id)
            .ToListAsync();
        var candidates = candidateIds.ToHashSet();

        // The seeding must actually exercise all eight signals, or this proves very little.
        var firedCodes = allSeeded
            .SelectMany(t => AttentionPolicy.Evaluate(
                t,
                Now,
                DefaultOptions,
                SlaPolicy.ResolveConfiguration(configurations, t.WorkType, t.Priority).RiskThresholdPercent))
            .Select(s => s.Code)
            .Distinct()
            .ToList();

        Assert.Equal(Enum.GetValues<AttentionSignalCode>().Length, firedCodes.Count);
        Assert.NotEmpty(trulyFlagged);

        // THE superset property: everything the policy flags is a candidate.
        var missed = trulyFlagged.Except(candidates).ToList();
        Assert.True(
            missed.Count == 0,
            $"The prefilter excluded ticket(s) AttentionPolicy flags: {string.Join(", ", missed)}. "
                + "The candidate query is no longer a superset of the policy.");
    }

    [Fact] // Terminal tickets can never become candidates, whatever else is true of them.
    public async Task Prefilter_NeverAdmitsResolvedOrClosedTickets()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedEverySignalAsync(context);
        var configurations = await context.SlaConfigurations.AsNoTracking().ToListAsync();

        var service = new AttentionQueryService(context, new TicketTestData.FixedTimeProvider(Now), DefaultOptions);
        var candidates = await service.BuildCandidateQuery(world.Agent, Now, configurations)
            .AsNoTracking()
            .Select(t => new { t.Id, t.Status })
            .ToListAsync();

        Assert.NotEmpty(candidates);
        Assert.DoesNotContain(candidates, c => c.Status is Status.Resolved or Status.Closed);
    }

    /// <summary>
    /// The aging branch widens to the *shortest* configured window — one day by default — so a
    /// two-day-old ticket is admitted even though its own Low-priority window is thirty days.
    /// Raising every window past its age removes it, proving the boundary is read from
    /// <see cref="AttentionOptions"/> rather than baked into the query.
    /// </summary>
    [Fact]
    public async Task Prefilter_AgingBoundary_FollowsConfiguredThresholds()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        var configurations = await context.SlaConfigurations.AsNoTracking().ToListAsync();

        // Two days old, assigned, Low priority: its 4320-minute target puts the at-risk branch
        // nine hours in the future, and no other branch applies — so only aging can admit it.
        var ticketId = await CreateTicketAsync(context, world, Now.AddDays(-2), Priority.Low);
        await AssignAsync(context, world, ticketId, Now.AddDays(-2));

        var clock = new TicketTestData.FixedTimeProvider(Now);

        var withDefaults = new AttentionQueryService(context, clock, DefaultOptions);
        Assert.Contains(
            await withDefaults.BuildCandidateQuery(world.Agent, Now, configurations).Select(t => t.Id).ToListAsync(),
            id => id == ticketId);

        var patient = new AttentionOptions
        {
            AgingThresholdDays = new Dictionary<Priority, int>
            {
                [Priority.Critical] = 5,
                [Priority.High] = 5,
                [Priority.Medium] = 10,
                [Priority.Low] = 30,
            },
        };

        var withPatient = new AttentionQueryService(context, clock, patient);
        Assert.DoesNotContain(
            await withPatient.BuildCandidateQuery(world.Agent, Now, configurations).Select(t => t.Id).ToListAsync(),
            id => id == ticketId);
    }

    /// <summary>
    /// The at-risk branch must widen using the *smallest* persisted risk threshold, so lowering one
    /// configuration row's threshold pulls more tickets into the candidate set.
    /// </summary>
    [Fact]
    public async Task Prefilter_UsesTheSmallestPersistedRiskThreshold()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        // 30% elapsed against a 1440-minute target: not at risk at 80%, but at risk at 10%.
        var ticketId = await CreateTicketAsync(context, world, Now.AddMinutes(-432), Priority.Medium);
        await AssignAsync(context, world, ticketId, Now.AddMinutes(-432));

        var clock = new TicketTestData.FixedTimeProvider(Now);
        var configurations = await context.SlaConfigurations.AsNoTracking().ToListAsync();
        var service = new AttentionQueryService(context, clock, DefaultOptions);

        Assert.DoesNotContain(
            await service.BuildCandidateQuery(world.Agent, Now, configurations).Select(t => t.Id).ToListAsync(),
            id => id == ticketId);

        // Same rows, one threshold lowered — read from the list the caller supplies, not the DB.
        var lowered = configurations
            .Select(c => new SlaConfiguration(c.Id, c.WorkType, c.Priority, c.TargetMinutes, c.Priority == Priority.Medium ? 10 : c.RiskThresholdPercent))
            .ToList();

        Assert.Contains(
            await service.BuildCandidateQuery(world.Agent, Now, lowered).Select(t => t.Id).ToListAsync(),
            id => id == ticketId);
    }

    [Fact] // Tickets with no signal never reach the caller, even if the prefilter admitted them.
    public async Task GetAtRiskAsync_ExcludesTicketsWithNoSignals()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        var healthyId = await CreateTicketAsync(context, world, Now.AddMinutes(-5), Priority.Medium);
        await AssignAsync(context, world, healthyId, Now.AddMinutes(-5));
        var breachedId = await CreateTicketAsync(context, world, Now.AddDays(-5), Priority.Medium);

        var page = await NewService(context).GetAtRiskAsync(world.Agent, 1);

        Assert.Contains(page.Items, i => i.Id == breachedId);
        Assert.DoesNotContain(page.Items, i => i.Id == healthyId);
        Assert.All(page.Items, i => Assert.NotEmpty(i.Signals));
    }

    [Fact] // ATTN-RULE-04: the page order is exactly what Rank produced.
    public async Task GetAtRiskAsync_ReturnsRankedOrder()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedEverySignalAsync(context);

        var page = await NewService(context).GetAtRiskAsync(world.Agent, 1);

        // Severity is non-increasing down the page — the first ordering key of ATTN-RULE-04.
        var severities = page.Items.Select(i => i.HighestSeverity).ToList();
        Assert.Equal(severities.OrderByDescending(s => s), severities);

        // And it matches Rank applied to the same evaluated set, key for key.
        var configurations = await context.SlaConfigurations.AsNoTracking().ToListAsync();
        var evaluated = (await context.Tickets.AsNoTracking().Include(t => t.Events)
                .Where(t => t.TeamId == world.TeamId).ToListAsync())
            .Select(t => new TicketAttentionResult(
                t,
                AttentionPolicy.Evaluate(t, Now, DefaultOptions,
                    SlaPolicy.ResolveConfiguration(configurations, t.WorkType, t.Priority).RiskThresholdPercent)))
            .ToList();

        var expectedOrder = AttentionPolicy.Rank(evaluated).Select(r => r.Ticket.Id).ToList();
        Assert.Equal(expectedOrder, page.Items.Select(i => i.Id));
    }

    /// <summary>
    /// Ranking is global, not per-page: with more candidates than fit on one page, page 1 must be
    /// the first slice of the fully ranked list and page 2 the next. Asserted against the ranking
    /// computed independently here, so a service that paged in SQL and then sorted each page would
    /// fail even if every individual page looked plausibly ordered.
    /// </summary>
    [Fact]
    public async Task GetAtRiskAsync_RanksGloballyBeforePaging()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedEverySignalAsync(context);

        // Top up past a full page with a mix of severities: breached tickets rank Critical,
        // freshly reopened ones only Medium.
        for (var i = 0; i < 10; i++)
        {
            await CreateTicketAsync(context, world, Now.AddDays(-4), Priority.Medium);

            var reopenedId = await CreateTicketAsync(context, world, Now.AddMinutes(-90), Priority.Medium);
            await AssignAsync(context, world, reopenedId, Now.AddMinutes(-90));
            await StartWorkAsync(context, world, reopenedId, Now.AddMinutes(-85));
            await ResolveAsync(context, world, reopenedId, Now.AddMinutes(-80));
            await ReopenAsync(context, world, reopenedId, Now.AddMinutes(-70));
        }

        var expected = await ExpectedRankingAsync(context, world);
        Assert.True(expected.Count > AttentionQueryService.PageSize, "the scenario must span more than one page");

        var service = NewService(context);
        var firstPage = await service.GetAtRiskAsync(world.Agent, 1);
        var secondPage = await service.GetAtRiskAsync(world.Agent, 2);

        Assert.Equal(AttentionQueryService.PageSize, firstPage.Items.Count);
        Assert.Equal(expected.Take(AttentionQueryService.PageSize), firstPage.Items.Select(i => i.Id));
        Assert.Equal(
            expected.Skip(AttentionQueryService.PageSize).Take(AttentionQueryService.PageSize),
            secondPage.Items.Select(i => i.Id));
        Assert.Equal(expected.Count, firstPage.TotalCount);
    }

    /// <summary>The ranking, computed here from the authoritative policy, for comparison.</summary>
    private static async Task<IReadOnlyList<int>> ExpectedRankingAsync(FlowOpsDbContext context, World world)
    {
        var configurations = await context.SlaConfigurations.AsNoTracking().ToListAsync();

        var evaluated = (await context.Tickets
                .AsNoTracking()
                .Include(t => t.Events)
                .Where(t => t.TeamId == world.TeamId)
                .ToListAsync())
            .Select(t => new TicketAttentionResult(
                t,
                AttentionPolicy.Evaluate(
                    t,
                    Now,
                    DefaultOptions,
                    SlaPolicy.ResolveConfiguration(configurations, t.WorkType, t.Priority).RiskThresholdPercent)))
            .ToList();

        return AttentionPolicy.Rank(evaluated).Select(r => r.Ticket.Id).ToList();
    }

    [Fact] // The Stalled/InProgress branch is driven by real persisted event history.
    public async Task GetAtRiskAsync_StalledInProgress_UsesPersistedEventHistory()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        var startedLongAgo = await CreateTicketAsync(context, world, Now.AddDays(-20), Priority.Low);
        await AssignAsync(context, world, startedLongAgo, Now.AddDays(-20));
        await StartWorkAsync(context, world, startedLongAgo, Now.AddDays(-20));

        var startedRecently = await CreateTicketAsync(context, world, Now.AddDays(-20), Priority.Low);
        await AssignAsync(context, world, startedRecently, Now.AddDays(-20));
        // Latest event only a day ago: not stalled, though the ticket itself is old.
        await StartWorkAsync(context, world, startedRecently, Now.AddDays(-1));

        var page = await NewService(context).GetAtRiskAsync(world.Agent, 1);

        var stalled = page.Items.SingleOrDefault(i => i.Id == startedLongAgo);
        Assert.NotNull(stalled);
        Assert.Contains(stalled.Signals, s => s.Code == AttentionSignalCode.Stalled);

        var active = page.Items.SingleOrDefault(i => i.Id == startedRecently);
        Assert.True(
            active is null || active.Signals.All(s => s.Code != AttentionSignalCode.Stalled),
            "a ticket whose last event was yesterday must not be reported as stalled");
    }

    [Fact] // AUTH-RULE-05: attention never discloses a ticket the caller could not already view.
    public async Task GetAtRiskAsync_IsTeamScoped()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        var breachedId = await CreateTicketAsync(context, world, Now.AddDays(-5), Priority.Medium);

        var outsider = TicketTestData.User(await TicketTestData.AddUserAsync(context), UserRole.Agent, world.OtherTeamId);

        var mine = await NewService(context).GetAtRiskAsync(world.Agent, 1);
        var theirs = await NewService(context).GetAtRiskAsync(outsider, 1);

        Assert.Contains(mine.Items, i => i.Id == breachedId);
        Assert.DoesNotContain(theirs.Items, i => i.Id == breachedId);
    }

    [Fact] // Admin sees at-risk work across teams they do not belong to.
    public async Task GetAtRiskAsync_AdminSeesAcrossTeams()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        var breachedId = await CreateTicketAsync(context, world, Now.AddDays(-5), Priority.Medium);

        var admin = TicketTestData.User(await TicketTestData.AddUserAsync(context), UserRole.Admin);
        var page = await NewService(context).GetAtRiskAsync(admin, 1);

        Assert.Contains(page.Items, i => i.Id == breachedId);
    }

    /// <summary>
    /// The whole request must cost a constant number of round trips regardless of how many
    /// candidates come back — in particular, events must arrive with the candidates rather than
    /// through a lookup per ticket.
    /// </summary>
    [Fact]
    public async Task GetAtRiskAsync_IssuesAConstantNumberOfQueries_WithNoNPlusOne()
    {
        var sql = new StringBuilder();
        await using var context = _fixture.CreateContext(line => sql.AppendLine(line));
        var world = await SeedAsync(context);

        for (var i = 0; i < 12; i++)
        {
            var id = await CreateTicketAsync(context, world, Now.AddDays(-6), Priority.Medium);
            await AssignAsync(context, world, id, Now.AddDays(-6));
            await StartWorkAsync(context, world, id, Now.AddDays(-6));
        }

        sql.Clear();
        var page = await NewService(context).GetAtRiskAsync(world.Agent, 1);

        Assert.True(page.Items.Count >= 12, "the scenario must produce enough at-risk tickets to expose an N+1");

        var executed = SelectStatementPattern().Matches(sql.ToString()).Count;

        // SLA configuration, candidates (+ their events, same statement), team names, assignee
        // names. Four is the ceiling; the point is that it does not scale with candidate count.
        Assert.True(
            executed <= 4,
            $"expected a constant handful of queries, saw {executed}:{Environment.NewLine}{sql}");

        // Events genuinely arrived with the candidates.
        Assert.Contains("ticket_events", sql.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // Empty state: a caller with nothing at risk gets an empty, well-formed page.
    public async Task GetAtRiskAsync_NothingAtRisk_ReturnsEmptyPage()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        var healthyId = await CreateTicketAsync(context, world, Now.AddMinutes(-5), Priority.Medium);
        await AssignAsync(context, world, healthyId, Now.AddMinutes(-5));

        var page = await NewService(context).GetAtRiskAsync(world.Agent, 1);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
        Assert.Equal(1, page.TotalPages);
    }

    // ---------- seeding ----------

    private AttentionQueryService NewService(FlowOpsDbContext context) =>
        new(context, new TicketTestData.FixedTimeProvider(Now), DefaultOptions);

    /// <summary>
    /// One true positive and one near-miss for each of the eight signals. Near-misses sit just
    /// inside the threshold so they exercise the boundary rather than being trivially healthy.
    /// </summary>
    private static async Task<World> SeedEverySignalAsync(FlowOpsDbContext context)
    {
        var world = await SeedAsync(context);

        // SlaBreached / near-miss (Medium target is 1440 minutes).
        await CreateTicketAsync(context, world, Now.AddMinutes(-1500), Priority.Medium);
        var withinId = await CreateTicketAsync(context, world, Now.AddMinutes(-10), Priority.Medium);
        await AssignAsync(context, world, withinId, Now.AddMinutes(-10));

        // SlaAtRisk (80% of 1440 = 1152) / just below.
        var atRiskId = await CreateTicketAsync(context, world, Now.AddMinutes(-1200), Priority.Medium);
        await AssignAsync(context, world, atRiskId, Now.AddMinutes(-1200));
        var nearRiskId = await CreateTicketAsync(context, world, Now.AddMinutes(-1100), Priority.Medium);
        await AssignAsync(context, world, nearRiskId, Now.AddMinutes(-1100));

        // Overdue / not yet due — DueDate is set through the aggregate directly, since
        // ChangeDueDate is deliberately not exposed by TicketService (Phase 6 scope decision).
        var overdueId = await CreateTicketAsync(context, world, Now.AddMinutes(-30), Priority.Medium);
        await SetDueDateAsync(context, world, overdueId, Now.AddMinutes(-5));
        var notOverdueId = await CreateTicketAsync(context, world, Now.AddMinutes(-30), Priority.Medium);
        await SetDueDateAsync(context, world, notOverdueId, Now.AddDays(3));

        // UnassignedUrgent (>15 min) / 10 minutes old.
        await CreateTicketAsync(context, world, Now.AddMinutes(-40), Priority.Critical);
        await CreateTicketAsync(context, world, Now.AddMinutes(-10), Priority.High);

        // Aging (Low threshold is 30 days) / 20 days old.
        var agedId = await CreateTicketAsync(context, world, Now.AddDays(-40), Priority.Low);
        await AssignAsync(context, world, agedId, Now.AddDays(-40));
        var youngerId = await CreateTicketAsync(context, world, Now.AddDays(-20), Priority.Low);
        await AssignAsync(context, world, youngerId, Now.AddDays(-20));

        // Stalled, Pending branch (>3 days) / 2 days pending.
        var stalledPendingId = await CreateTicketAsync(context, world, Now.AddDays(-10), Priority.Low);
        await AssignAsync(context, world, stalledPendingId, Now.AddDays(-10));
        await PutOnHoldAsync(context, world, stalledPendingId, Now.AddDays(-9));
        var freshPendingId = await CreateTicketAsync(context, world, Now.AddDays(-10), Priority.Low);
        await AssignAsync(context, world, freshPendingId, Now.AddDays(-10));
        await PutOnHoldAsync(context, world, freshPendingId, Now.AddDays(-2));

        // Stalled, InProgress branch (>5 days without an event) / 4 days.
        var stalledInProgressId = await CreateTicketAsync(context, world, Now.AddDays(-15), Priority.Low);
        await AssignAsync(context, world, stalledInProgressId, Now.AddDays(-15));
        await StartWorkAsync(context, world, stalledInProgressId, Now.AddDays(-15));
        var activeId = await CreateTicketAsync(context, world, Now.AddDays(-15), Priority.Low);
        await AssignAsync(context, world, activeId, Now.AddDays(-15));
        await StartWorkAsync(context, world, activeId, Now.AddDays(-4));

        // Churn (3 reassignments) / 2 reassignments.
        var churnId = await CreateTicketAsync(context, world, Now.AddMinutes(-60), Priority.Medium);
        await AssignAsync(context, world, churnId, Now.AddMinutes(-60));
        await ReassignAsync(context, world, churnId, world.SecondAgentId, Now.AddMinutes(-50));
        await ReassignAsync(context, world, churnId, world.ThirdAgentId, Now.AddMinutes(-40));
        await ReassignAsync(context, world, churnId, world.AgentId, Now.AddMinutes(-30));
        var someChurnId = await CreateTicketAsync(context, world, Now.AddMinutes(-60), Priority.Medium);
        await AssignAsync(context, world, someChurnId, Now.AddMinutes(-60));
        await ReassignAsync(context, world, someChurnId, world.SecondAgentId, Now.AddMinutes(-50));
        await ReassignAsync(context, world, someChurnId, world.ThirdAgentId, Now.AddMinutes(-40));

        // Reopened / resolved-and-left-closed (terminal, so it must never be a candidate).
        var reopenedId = await CreateTicketAsync(context, world, Now.AddMinutes(-90), Priority.Medium);
        await AssignAsync(context, world, reopenedId, Now.AddMinutes(-90));
        await StartWorkAsync(context, world, reopenedId, Now.AddMinutes(-85));
        await ResolveAsync(context, world, reopenedId, Now.AddMinutes(-80));
        await ReopenAsync(context, world, reopenedId, Now.AddMinutes(-70));

        var closedId = await CreateTicketAsync(context, world, Now.AddMinutes(-90), Priority.Medium);
        await AssignAsync(context, world, closedId, Now.AddMinutes(-90));
        await StartWorkAsync(context, world, closedId, Now.AddMinutes(-85));
        await ResolveAsync(context, world, closedId, Now.AddMinutes(-80));

        return world;
    }

    private static TicketService ServiceAt(FlowOpsDbContext context, DateTimeOffset instant) =>
        new(context, new TicketTestData.FixedTimeProvider(instant));

    private static async Task<int> CreateTicketAsync(FlowOpsDbContext context, World world, DateTimeOffset instant, Priority priority)
    {
        var (id, _) = await ServiceAt(context, instant).CreateAsync(
            new CreateTicketRequest(
                Title: "Printer on 3rd floor is jammed",
                Description: "The printer near the east stairwell is jammed and needs a technician.",
                WorkType: WorkType.Incident,
                Priority: priority,
                TeamId: world.TeamId,
                CategoryId: world.CategoryId,
                ProjectId: null),
            world.Requester);

        return id;
    }

    private static Task AssignAsync(FlowOpsDbContext context, World world, int ticketId, DateTimeOffset instant) =>
        ServiceAt(context, instant).AssignAsync(ticketId, world.AgentId, world.Admin);

    private static Task ReassignAsync(FlowOpsDbContext context, World world, int ticketId, Guid assigneeId, DateTimeOffset instant) =>
        ServiceAt(context, instant).ReassignAsync(ticketId, assigneeId, world.Admin);

    private static Task StartWorkAsync(FlowOpsDbContext context, World world, int ticketId, DateTimeOffset instant) =>
        ServiceAt(context, instant).StartWorkAsync(ticketId, world.Admin);

    private static Task PutOnHoldAsync(FlowOpsDbContext context, World world, int ticketId, DateTimeOffset instant) =>
        ServiceAt(context, instant).PutOnHoldAsync(ticketId, "Waiting on the requester", world.Admin);

    private static Task ResolveAsync(FlowOpsDbContext context, World world, int ticketId, DateTimeOffset instant) =>
        ServiceAt(context, instant).ResolveAsync(ticketId, Resolution.Fixed, "Replaced the toner cartridge.", world.Admin);

    private static Task ReopenAsync(FlowOpsDbContext context, World world, int ticketId, DateTimeOffset instant) =>
        ServiceAt(context, instant).ReopenAsync(ticketId, "The fault recurred", world.Admin);

    private static async Task SetDueDateAsync(FlowOpsDbContext context, World world, int ticketId, DateTimeOffset dueDate)
    {
        var ticket = await context.Tickets.SingleAsync(t => t.Id == ticketId);
        ticket.ChangeDueDate(dueDate, world.Admin.UserId, Now.AddMinutes(-29));
        await context.SaveChangesAsync();
    }

    private sealed record World(
        int TeamId,
        int OtherTeamId,
        int CategoryId,
        Guid AgentId,
        Guid SecondAgentId,
        Guid ThirdAgentId,
        CurrentUser Agent,
        CurrentUser Requester,
        CurrentUser Admin);

    private static async Task<World> SeedAsync(FlowOpsDbContext context)
    {
        var teamId = await TicketTestData.AddTeamAsync(context);
        var otherTeamId = await TicketTestData.AddTeamAsync(context);
        var categoryId = await TicketTestData.AddCategoryAsync(context, teamId);

        var agentId = await TicketTestData.AddUserAsync(context);
        var secondAgentId = await TicketTestData.AddUserAsync(context);
        var thirdAgentId = await TicketTestData.AddUserAsync(context);
        var requesterId = await TicketTestData.AddUserAsync(context);
        var adminId = await TicketTestData.AddUserAsync(context);

        foreach (var memberId in new[] { agentId, secondAgentId, thirdAgentId, requesterId })
        {
            await TicketTestData.AddTeamMembershipAsync(context, teamId, memberId);
        }

        return new World(
            teamId,
            otherTeamId,
            categoryId,
            agentId,
            secondAgentId,
            thirdAgentId,
            TicketTestData.User(agentId, UserRole.Agent, teamId),
            TicketTestData.User(requesterId, UserRole.Agent, teamId),
            TicketTestData.User(adminId, UserRole.Admin));
    }

    [GeneratedRegex(@"SELECT\s", RegexOptions.IgnoreCase)]
    private static partial Regex SelectStatementPattern();
}
