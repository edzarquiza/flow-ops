using System.Text;
using System.Text.RegularExpressions;
using FlowOps.Application.Tests.Persistence;
using FlowOps.Application.Tickets;
using FlowOps.Domain;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FlowOps.Application.Tests.Tickets;

/// <summary>
/// Phase 9 comments and the merged activity timeline (AUDIT-RULE-06) against real PostgreSQL:
/// atomic comment/event persistence, authorization, internal-comment exclusion at the query
/// boundary, deterministic merge ordering, and a bounded query count.
/// </summary>
[Collection("Postgres")]
public sealed partial class TicketCommentServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;

    public TicketCommentServiceTests(PostgresFixture fixture) => _fixture = fixture;

    [Theory] // AUTH-RULE-02 "Comment (public)": Admin, Manager and Agent may comment.
    [InlineData(UserRole.Admin)]
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.Agent)]
    public async Task AddCommentAsync_AllowedRole_PersistsCommentAndExactlyOneEvent(UserRole role)
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        var actor = ActorFor(world, role);

        await world.Service.AddCommentAsync(world.TicketId, "Any update on this?", isInternal: false, actor);

        // Verify the actual persisted rows in a fresh context/connection — not in-memory state.
        await using var verify = _fixture.CreateContext();
        var comment = await verify.TicketComments.AsNoTracking().SingleAsync(c => c.TicketId == world.TicketId);
        // ThenBy(Id): Created and CommentAdded share the same fixed `Start` timestamp in this
        // test, and OccurredAt alone gives Postgres no tiebreaker to order ties by — Id (the
        // events' own insertion-order serial column) is the deterministic secondary key, the same
        // one TicketQueryService.GetHistoryAsync's production ordering already uses.
        var events = await verify.TicketEvents.AsNoTracking()
            .Where(e => e.TicketId == world.TicketId)
            .OrderBy(e => e.OccurredAt)
            .ThenBy(e => e.Id)
            .ToListAsync();

        Assert.Equal("Any update on this?", comment.Body);
        Assert.False(comment.IsInternal);
        Assert.Equal(actor.UserId, comment.AuthorId);
        Assert.Equal(Start, comment.CreatedAt);

        // Created (from ticket setup) + exactly one CommentAdded — the comment and its audit
        // event persisted atomically through the one SaveChangesAsync in MutateAsync.
        Assert.Equal([TicketEventType.Created, TicketEventType.CommentAdded], events.Select(e => e.EventType));
        var commentAdded = events[1];
        Assert.Equal(actor.UserId, commentAdded.ActorUserId);
        Assert.Equal(Start, commentAdded.OccurredAt);
        Assert.Null(commentAdded.Field);
        Assert.Null(commentAdded.OldValue);
        Assert.Null(commentAdded.NewValue);
        Assert.Null(commentAdded.Note);
    }

    [Fact] // AUTH-RULE-02: Viewer may not comment.
    public async Task AddCommentAsync_Viewer_IsDeniedAndPersistsNothing()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        var viewer = TicketTestData.User(await TicketTestData.AddUserAsync(context), UserRole.Viewer, world.TeamId);

        await Assert.ThrowsAsync<TicketAccessDeniedException>(() =>
            world.Service.AddCommentAsync(world.TicketId, "Trying to comment", isInternal: false, viewer));

        await using var verify = _fixture.CreateContext();
        Assert.Equal(0, await verify.TicketComments.CountAsync(c => c.TicketId == world.TicketId));
        Assert.Single(await verify.TicketEvents.Where(e => e.TicketId == world.TicketId).ToListAsync()); // Created only
    }

    [Fact] // A caller outside the ticket's team is refused identically to a nonexistent ticket.
    public async Task AddCommentAsync_OutsideTeam_IsDeniedBeforeMutating()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        var outsider = TicketTestData.User(await TicketTestData.AddUserAsync(context), UserRole.Agent, world.OtherTeamId);

        await Assert.ThrowsAsync<TicketAccessDeniedException>(() =>
            world.Service.AddCommentAsync(world.TicketId, "Should not persist", isInternal: false, outsider));

        await using var verify = _fixture.CreateContext();
        Assert.Equal(0, await verify.TicketComments.CountAsync(c => c.TicketId == world.TicketId));
    }

    [Fact] // TICKET-ENT-05: comment body is required, and rejection persists nothing.
    public async Task AddCommentAsync_EmptyBody_IsRejectedAndPersistsNothing()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            world.Service.AddCommentAsync(world.TicketId, "   ", isInternal: false, world.Agent));

        Assert.Equal("TICKET-ENT-05", ex.RuleCode);

        await using var verify = _fixture.CreateContext();
        Assert.Equal(0, await verify.TicketComments.CountAsync(c => c.TicketId == world.TicketId));
        Assert.Single(await verify.TicketEvents.Where(e => e.TicketId == world.TicketId).ToListAsync());
    }

    [Fact] // TICKET-ENT-05: an internal comment persists with IsInternal set.
    public async Task AddCommentAsync_Internal_PersistsIsInternalTrue()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        await world.Service.AddCommentAsync(world.TicketId, "Escalate to vendor.", isInternal: true, world.Agent);

        await using var verify = _fixture.CreateContext();
        var comment = await verify.TicketComments.AsNoTracking().SingleAsync(c => c.TicketId == world.TicketId);
        Assert.True(comment.IsInternal);
    }

    // ---- merged timeline ----

    [Fact] // AUDIT-RULE-06: events and comments appear together, newest first.
    public async Task GetHistoryAsync_MergesEventsAndCommentsChronologically()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        // Created (Start), Assigned (Start) -> Comment + its own CommentAdded event (Start+10m).
        // AddComment appends both a TicketComment row and a CommentAdded TicketEvent (TICKET-INV-09
        // / AUDIT-RULE-04): the timeline therefore carries four rows for two user actions, which is
        // AUDIT-RULE-06's point — the machine record and the human content are distinct facts.
        await world.Service.AssignAsync(world.TicketId, world.AgentId, world.Admin);
        world.Clock.Advance(TimeSpan.FromMinutes(10));
        await world.Service.AddCommentAsync(world.TicketId, "Looking into it now.", isInternal: false, world.Agent);

        var timeline = await world.Query.GetHistoryAsync(world.TicketId, world.Agent);

        Assert.Equal(4, timeline.Count);
        // Timestamp DESC: the Start+10m pair first, then the two Start entries.
        // Within Start+10m, Kind ascending (Event < Comment) puts CommentAdded before the comment.
        Assert.Equal(
            [
                TicketTimelineEntryKind.Event,   // CommentAdded, Start+10m
                TicketTimelineEntryKind.Comment, // the comment itself, Start+10m
                TicketTimelineEntryKind.Event,   // Assigned, Start
                TicketTimelineEntryKind.Event,   // Created, Start
            ],
            timeline.Select(e => e.Kind));

        var commentAddedEvent = timeline[0];
        Assert.Equal(TicketEventType.CommentAdded, commentAddedEvent.EventType);
        Assert.Null(commentAddedEvent.Body);

        var comment = timeline[1];
        Assert.Equal(TicketTimelineEntryKind.Comment, comment.Kind);
        Assert.Equal("Looking into it now.", comment.Body);
        Assert.False(comment.IsInternal);
        Assert.Null(comment.EventType);

        Assert.Equal(TicketEventType.Assigned, timeline[2].EventType);
        Assert.Equal(TicketEventType.Created, timeline[3].EventType);
    }

    [Fact] // Deterministic ordering: Timestamp DESC, Kind, Id DESC (decision 3) at a shared instant.
    public async Task GetHistoryAsync_OrdersByTimestampThenKindThenIdDescending_AtIdenticalTimestamps()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        // Two comments at the exact same instant: the tiebreak must be Id descending within Kind.
        await world.Service.AddCommentAsync(world.TicketId, "First comment", isInternal: false, world.Agent);
        await world.Service.AddCommentAsync(world.TicketId, "Second comment", isInternal: false, world.Agent);

        var timeline = await world.Query.GetHistoryAsync(world.TicketId, world.Agent);

        var comments = timeline.Where(e => e.Kind == TicketTimelineEntryKind.Comment).ToList();
        Assert.Equal(2, comments.Count);
        Assert.Equal("Second comment", comments[0].Body); // higher id, same timestamp, sorts first
        Assert.Equal("First comment", comments[1].Body);
        Assert.True(comments[0].Id > comments[1].Id);

        // Whole-list invariant: never out of (timestamp desc, then id desc within a kind) order.
        for (var i = 1; i < timeline.Count; i++)
        {
            Assert.True(timeline[i - 1].OccurredAt >= timeline[i].OccurredAt);
        }
    }

    [Fact] // TICKET-ENT-05 / AUTH-RULE-04: a Viewer's timeline excludes internal comments.
    public async Task GetHistoryAsync_Viewer_ExcludesInternalComments()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        var viewer = TicketTestData.User(await TicketTestData.AddUserAsync(context), UserRole.Viewer, world.TeamId);

        await world.Service.AddCommentAsync(world.TicketId, "Visible to everyone", isInternal: false, world.Agent);
        await world.Service.AddCommentAsync(world.TicketId, "Internal only", isInternal: true, world.Agent);

        var viewerTimeline = await world.Query.GetHistoryAsync(world.TicketId, viewer);
        var agentTimeline = await world.Query.GetHistoryAsync(world.TicketId, world.Agent);

        Assert.DoesNotContain(viewerTimeline, e => e.Body == "Internal only");
        Assert.Contains(viewerTimeline, e => e.Body == "Visible to everyone");

        // Confirms the exclusion is real, not coincidental: the same ticket, same instant, an
        // Agent's timeline does contain it.
        Assert.Contains(agentTimeline, e => e.Body == "Internal only");
    }

    [Theory] // TICKET-ENT-05: Manager and Admin also see internal comments, like Agent.
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.Admin)]
    public async Task GetHistoryAsync_ManagerAndAdmin_SeeInternalComments(UserRole role)
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        await world.Service.AddCommentAsync(world.TicketId, "Internal note", isInternal: true, world.Agent);

        var timeline = await world.Query.GetHistoryAsync(world.TicketId, ActorFor(world, role));

        Assert.Contains(timeline, e => e.Body == "Internal note");
    }

    [Fact] // AUTH-RULE-05: timeline access never discloses a ticket outside the caller's scope.
    public async Task GetHistoryAsync_OutsideTeam_ReturnsEmpty()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        await world.Service.AddCommentAsync(world.TicketId, "Team-only content", isInternal: false, world.Agent);
        var outsider = TicketTestData.User(await TicketTestData.AddUserAsync(context), UserRole.Agent, world.OtherTeamId);

        var timeline = await world.Query.GetHistoryAsync(world.TicketId, outsider);

        Assert.Empty(timeline);
    }

    [Fact] // Admin sees the full merged timeline across teams they do not belong to.
    public async Task GetHistoryAsync_Admin_SeesTimelineAcrossTeams()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);
        await world.Service.AddCommentAsync(world.TicketId, "Cross-team visible to admin", isInternal: false, world.Agent);

        var timeline = await world.Query.GetHistoryAsync(world.TicketId, world.Admin);

        Assert.Contains(timeline, e => e.Body == "Cross-team visible to admin");
    }

    [Fact] // Empty timeline is only the Created event — never truly empty for a real ticket.
    public async Task GetHistoryAsync_NoCommentsYet_ReturnsOnlyCreatedEvent()
    {
        await using var context = _fixture.CreateContext();
        var world = await SeedAsync(context);

        var timeline = await world.Query.GetHistoryAsync(world.TicketId, world.Agent);

        var only = Assert.Single(timeline);
        Assert.Equal(TicketTimelineEntryKind.Event, only.Kind);
        Assert.Equal(TicketEventType.Created, only.EventType);
    }

    /// <summary>
    /// The merged timeline must cost a small, constant number of round trips: the events query and
    /// the comments query, however many rows either returns — never a query per row.
    /// </summary>
    [Fact]
    public async Task GetHistoryAsync_IssuesAConstantNumberOfQueries_WithNoNPlusOne()
    {
        var sql = new StringBuilder();
        await using var context = _fixture.CreateContext(line => sql.AppendLine(line));
        var world = await SeedAsync(context);

        for (var i = 0; i < 15; i++)
        {
            await world.Service.AddCommentAsync(world.TicketId, $"Comment {i}", isInternal: i % 2 == 0, world.Agent);
        }

        sql.Clear();
        var timeline = await world.Query.GetHistoryAsync(world.TicketId, world.Agent);
        var emitted = sql.ToString();

        Assert.True(timeline.Count >= 16, "the scenario must produce enough rows to expose an N+1"); // Created + 15 comments' CommentAdded... wait, comments also each add an event

        // Counts round trips (one "Executed DbCommand" log line per statement actually sent to
        // Postgres), not "SELECT" keyword occurrences: Phase 16's organization-boundary check
        // (TicketQueryService.ApplyViewScope) adds a legitimate EXISTS(...) subquery — its own
        // nested SELECT — inside the SAME single round trip, which a keyword count would wrongly
        // flag as extra.
        var executed = CommandExecutedPattern().Matches(emitted).Count;

        // Two source queries (events, comments), each with its own actor/author join folded into
        // the same statement — a constant count regardless of how many comments exist.
        Assert.True(executed <= 2, $"expected exactly two queries (events, comments), saw {executed}:{Environment.NewLine}{emitted}");
        Assert.Contains("ticket_comments", emitted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ticket_events", emitted, StringComparison.OrdinalIgnoreCase);
    }

    private static CurrentUser ActorFor(World world, UserRole role) => role switch
    {
        UserRole.Admin => world.Admin,
        UserRole.Manager => world.Manager,
        UserRole.Agent => world.Agent,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    private sealed record World(
        int TicketId,
        int TeamId,
        int OtherTeamId,
        Guid AgentId,
        CurrentUser Agent,
        CurrentUser Manager,
        CurrentUser Admin,
        TicketService Service,
        TicketQueryService Query,
        TicketTestData.FixedTimeProvider Clock);

    private static async Task<World> SeedAsync(FlowOpsDbContext context)
    {
        var teamId = await TicketTestData.AddTeamAsync(context);
        var otherTeamId = await TicketTestData.AddTeamAsync(context);
        var categoryId = await TicketTestData.AddCategoryAsync(context, teamId);

        var agentId = await TicketTestData.AddUserAsync(context);
        var managerId = await TicketTestData.AddUserAsync(context);
        var adminId = await TicketTestData.AddUserAsync(context);

        await TicketTestData.AddTeamMembershipAsync(context, teamId, agentId);
        await TicketTestData.AddTeamMembershipAsync(context, teamId, managerId, isTeamManager: true);

        var clock = new TicketTestData.FixedTimeProvider(Start);
        var service = new TicketService(context, clock, TestEmail.Sender, TestEmail.Options);
        var agent = TicketTestData.User(agentId, UserRole.Agent, teamId);

        var (ticketId, _) = await service.CreateAsync(
            new CreateTicketRequest(
                Title: "Printer on 3rd floor is jammed",
                Description: "The printer near the east stairwell is jammed and needs a technician.",
                WorkType: WorkType.Incident,
                Priority: Priority.Medium,
                TeamId: teamId,
                CategoryId: categoryId,
                ProjectId: null),
            agent);

        return new World(
            ticketId,
            teamId,
            otherTeamId,
            agentId,
            agent,
            TicketTestData.Manager(managerId, teamId),
            TicketTestData.User(adminId, UserRole.Admin),
            service,
            new TicketQueryService(context, clock),
            clock);
    }

    [GeneratedRegex(@"Executed DbCommand", RegexOptions.IgnoreCase)]
    private static partial Regex CommandExecutedPattern();
}
