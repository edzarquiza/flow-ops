using FlowOps.Application.Tests.Persistence;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Sla;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FlowOps.Application.Tests.Tickets;

/// <summary>
/// Phase 7: SLA status surfaced through the read side against real PostgreSQL. The arithmetic
/// itself is Domain-tested; these prove the orchestration — that the threshold comes from the
/// persisted configuration, that the clock is the injected one, and that all five statuses reach
/// a caller who is allowed to see them.
/// </summary>
[Collection("Postgres")]
public sealed class TicketSlaQueryTests
{
    private static readonly DateTimeOffset Start = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Medium priority resolves to the seeded 1440-minute / 80% default row.</summary>
    private const int MediumTargetMinutes = 1440;

    private readonly PostgresFixture _fixture;

    public TicketSlaQueryTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact] // A fresh ticket is comfortably Within, counting down to its stored deadline.
    public async Task Detail_NewTicket_IsWithin_WithRemainingTime()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        var detail = await world.Query.GetDetailAsync(world.TicketId, world.Agent);

        Assert.NotNull(detail);
        Assert.Equal(SlaStatus.Within, detail.Sla.Status);
        Assert.Equal(MediumTargetMinutes, detail.Sla.TargetMinutes);
        Assert.Equal(0, detail.Sla.PausedMinutes);
        Assert.Equal(Start.AddMinutes(MediumTargetMinutes), detail.Sla.DueAt);
        Assert.Equal(TimeSpan.FromMinutes(MediumTargetMinutes), detail.Sla.Remaining);
    }

    [Theory] // SLA-RULE-10's live branches, driven purely by advancing the injected clock.
    [InlineData(0, SlaStatus.Within)]      // 0% elapsed
    [InlineData(1151, SlaStatus.Within)]   // 79.9% — just below the 80% threshold
    [InlineData(1152, SlaStatus.AtRisk)]   // exactly 80% of 1440
    [InlineData(1440, SlaStatus.Breached)] // exactly at the deadline: inclusive
    [InlineData(1500, SlaStatus.Breached)]
    public async Task Detail_StatusFollowsTheInjectedClock(int minutesElapsed, SlaStatus expected)
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        world.Clock.Advance(TimeSpan.FromMinutes(minutesElapsed));
        var detail = await world.Query.GetDetailAsync(world.TicketId, world.Agent);

        Assert.Equal(expected, detail!.Sla.Status);
    }

    [Fact] // Remaining is signed: positive before the deadline, negative after it.
    public async Task Detail_RemainingIsNegativeOnceOverdue()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        world.Clock.Advance(TimeSpan.FromMinutes(MediumTargetMinutes + 90));
        var detail = await world.Query.GetDetailAsync(world.TicketId, world.Agent);

        Assert.Equal(SlaStatus.Breached, detail!.Sla.Status);
        Assert.Equal(TimeSpan.FromMinutes(-90), detail.Sla.Remaining);
    }

    [Fact] // SLA-RULE-07: a Pending ticket reports Paused, even past its deadline.
    public async Task Detail_PendingTicket_IsPaused_EvenAfterTheDeadline()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        await world.Service.AssignAsync(world.TicketId, world.AgentId, world.Agent);
        await world.Service.StartWorkAsync(world.TicketId, world.Agent);
        await world.Service.PutOnHoldAsync(world.TicketId, "Waiting on the requester", world.Agent);

        world.Clock.Advance(TimeSpan.FromMinutes(MediumTargetMinutes + 60));
        var detail = await world.Query.GetDetailAsync(world.TicketId, world.Agent);

        Assert.Equal(SlaStatus.Paused, detail!.Sla.Status);
    }

    [Fact] // SLA-RULE-07: accrued pause is reflected in both the deadline and the paused total.
    public async Task Detail_AccruedPauseMovesTheDeadlineOut()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        await world.Service.AssignAsync(world.TicketId, world.AgentId, world.Agent);
        await world.Service.StartWorkAsync(world.TicketId, world.Agent);
        await world.Service.PutOnHoldAsync(world.TicketId, "Waiting on parts", world.Agent);
        world.Clock.Advance(TimeSpan.FromMinutes(120));
        await world.Service.ResumeAsync(world.TicketId, world.Agent);

        var detail = await world.Query.GetDetailAsync(world.TicketId, world.Agent);

        Assert.Equal(120, detail!.Sla.PausedMinutes);
        Assert.Equal(Start.AddMinutes(MediumTargetMinutes + 120), detail.Sla.DueAt);
        Assert.Equal(SlaStatus.Within, detail.Sla.Status);
    }

    [Theory] // SLA-RULE-08: a terminal ticket reports its persisted outcome, and never a countdown.
    [InlineData(60, SlaStatus.Met)]
    [InlineData(MediumTargetMinutes + 60, SlaStatus.Breached)]
    public async Task Detail_ResolvedTicket_ReportsPersistedOutcome_WithNoRemaining(int minutesBeforeResolving, SlaStatus expected)
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        await world.Service.AssignAsync(world.TicketId, world.AgentId, world.Agent);
        await world.Service.StartWorkAsync(world.TicketId, world.Agent);
        world.Clock.Advance(TimeSpan.FromMinutes(minutesBeforeResolving));
        await world.Service.ResolveAsync(world.TicketId, Resolution.Fixed, "Replaced the toner cartridge.", world.Agent);

        var detail = await world.Query.GetDetailAsync(world.TicketId, world.Agent);

        Assert.Equal(expected, detail!.Sla.Status);
        Assert.Null(detail.Sla.Remaining);
    }

    [Fact] // SLA-RULE-09: a reopened ticket counts down against its new cycle.
    public async Task Detail_ReopenedTicket_UsesTheNewSlaCycle()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        await world.Service.AssignAsync(world.TicketId, world.AgentId, world.Agent);
        await world.Service.StartWorkAsync(world.TicketId, world.Agent);
        await world.Service.ResolveAsync(world.TicketId, Resolution.Fixed, "Replaced the toner cartridge.", world.Agent);

        world.Clock.Advance(TimeSpan.FromHours(3));
        var reopenedAt = world.Clock.GetUtcNow();
        await world.Service.ReopenAsync(world.TicketId, "The fault recurred", world.Requester);

        var detail = await world.Query.GetDetailAsync(world.TicketId, world.Agent);

        Assert.Equal(SlaStatus.Within, detail!.Sla.Status);
        Assert.Equal(0, detail.Sla.PausedMinutes);
        Assert.Equal(reopenedAt.AddMinutes(MediumTargetMinutes), detail.Sla.DueAt);
        Assert.Equal(TimeSpan.FromMinutes(MediumTargetMinutes), detail.Sla.Remaining);
    }

    /// <summary>
    /// The threshold must come from the persisted configuration row, not a constant. Lowering the
    /// stored threshold to 10% flips a ticket that was Within into AtRisk with no other change.
    /// </summary>
    [Fact]
    public async Task Detail_RiskThresholdComesFromPersistedConfiguration()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        world.Clock.Advance(TimeSpan.FromMinutes(300)); // ~21% of 1440
        Assert.Equal(SlaStatus.Within, (await world.Query.GetDetailAsync(world.TicketId, world.Agent))!.Sla.Status);

        await context.Database.ExecuteSqlAsync(
            $"UPDATE sla_configurations SET risk_threshold_percent = 10 WHERE work_type IS NULL AND priority = 'Medium'");

        await using var fresh = _fixture.CreateContext();
        var query = new TicketQueryService(fresh, world.Clock);

        Assert.Equal(SlaStatus.AtRisk, (await query.GetDetailAsync(world.TicketId, world.Agent))!.Sla.Status);

        // Restore so the shared container database is left as the migration seeded it.
        await context.Database.ExecuteSqlAsync(
            $"UPDATE sla_configurations SET risk_threshold_percent = 80 WHERE work_type IS NULL AND priority = 'Medium'");
    }

    /// <summary>
    /// SLA-RULE-03: editing configuration never rewrites an existing ticket's captured target,
    /// but a new ticket picks the new value up.
    /// </summary>
    [Fact]
    public async Task ConfigurationChange_DoesNotRewriteExistingTickets_ButAppliesToNewOnes()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        await context.Database.ExecuteSqlAsync(
            $"UPDATE sla_configurations SET target_minutes = 60 WHERE work_type IS NULL AND priority = 'Medium'");

        try
        {
            await using var fresh = _fixture.CreateContext();
            var service = new TicketService(fresh, world.Clock);
            var query = new TicketQueryService(fresh, world.Clock);

            var (newTicketId, _) = await service.CreateAsync(
                Request(world.TeamId, world.CategoryId),
                world.Requester);

            var existing = await query.GetDetailAsync(world.TicketId, world.Agent);
            var created = await query.GetDetailAsync(newTicketId, world.Agent);

            Assert.Equal(MediumTargetMinutes, existing!.Sla.TargetMinutes); // snapshotted, untouched
            Assert.Equal(60, created!.Sla.TargetMinutes);                   // resolved fresh
        }
        finally
        {
            await context.Database.ExecuteSqlAsync(
                $"UPDATE sla_configurations SET target_minutes = 1440 WHERE work_type IS NULL AND priority = 'Medium'");
        }
    }

    [Fact] // The queue carries the same derived SLA view as the detail page.
    public async Task Queue_CarriesSlaStatusForEveryRow()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        world.Clock.Advance(TimeSpan.FromMinutes(MediumTargetMinutes + 30));
        var page = await world.Query.GetQueueAsync(world.Agent, 1);

        var item = Assert.Single(page.Items, i => i.Id == world.TicketId);
        Assert.Equal(SlaStatus.Breached, item.Sla.Status);
        Assert.Equal(TimeSpan.FromMinutes(-30), item.Sla.Remaining);
        Assert.Equal(MediumTargetMinutes, item.Sla.TargetMinutes);
    }

    [Fact] // SLA rides along with the ticket's visibility scope — no ticket, no SLA.
    public async Task SlaInformation_IsViewScoped()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        var outsider = TicketTestData.User(await TicketTestData.AddUserAsync(context), UserRole.Agent, world.OtherTeamId);

        Assert.Null(await world.Query.GetDetailAsync(world.TicketId, outsider));
        Assert.DoesNotContain(
            (await world.Query.GetQueueAsync(outsider, 1)).Items,
            i => i.Id == world.TicketId);
    }

    private static CreateTicketRequest Request(int teamId, int categoryId) =>
        new(
            Title: "Printer on 3rd floor is jammed",
            Description: "The printer near the east stairwell is jammed and needs a technician.",
            WorkType: WorkType.Incident,
            Priority: Priority.Medium,
            TeamId: teamId,
            CategoryId: categoryId,
            ProjectId: null);

    private sealed record World(
        int TicketId,
        int TeamId,
        int CategoryId,
        int OtherTeamId,
        Guid AgentId,
        CurrentUser Agent,
        CurrentUser Requester,
        TicketService Service,
        TicketQueryService Query,
        TicketTestData.FixedTimeProvider Clock);

    private static async Task<World> SeedAsync(FlowOpsDbContext context)
    {
        var teamId = await TicketTestData.AddTeamAsync(context);
        var otherTeamId = await TicketTestData.AddTeamAsync(context);
        var categoryId = await TicketTestData.AddCategoryAsync(context, teamId);

        var agentId = await TicketTestData.AddUserAsync(context);
        var requesterId = await TicketTestData.AddUserAsync(context);
        await TicketTestData.AddTeamMembershipAsync(context, teamId, agentId);
        await TicketTestData.AddTeamMembershipAsync(context, teamId, requesterId);

        var clock = new TicketTestData.FixedTimeProvider(Start);
        var service = new TicketService(context, clock);
        var requester = TicketTestData.User(requesterId, UserRole.Agent, teamId);

        var (ticketId, _) = await service.CreateAsync(Request(teamId, categoryId), requester);

        return new World(
            ticketId,
            teamId,
            categoryId,
            otherTeamId,
            agentId,
            TicketTestData.User(agentId, UserRole.Agent, teamId),
            requester,
            service,
            new TicketQueryService(context, clock),
            clock);
    }
}
