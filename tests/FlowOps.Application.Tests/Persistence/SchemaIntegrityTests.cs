using FlowOps.Domain.Catalog;
using FlowOps.Domain.Directory;
using FlowOps.Domain.Organizations;
using FlowOps.Domain.Sla;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Identity;
using FlowOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace FlowOps.Application.Tests.Persistence;

/// <summary>
/// Proves the database is a real, independent guardian of the invariants it reinforces
/// (docs/domain-model.md's PERSIST-RULE family; CLAUDE.md §15: "database CHECK constraints
/// actually reject invalid rows"). Every insert here goes through raw SQL or a deliberately
/// invalid EF write to bypass the Domain's own validation — the point is to prove the database
/// itself rejects the row, independent of application code.
/// </summary>
[Collection("Postgres")]
public sealed class SchemaIntegrityTests
{
    private readonly PostgresFixture _fixture;

    public SchemaIntegrityTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact] // migration sanity check — every documented table exists
    public async Task Migration_CreatesAllDocumentedTables()
    {
        await using var context = _fixture.CreateContext();
        var tableNames = await context.Database
            .SqlQuery<string>($"SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema = 'public'")
            .ToListAsync();

        string[] expected =
        [
            "organizations", "organization_memberships",
            "teams", "team_members", "categories", "projects",
            "sla_configurations", "tickets", "ticket_comments", "ticket_events",
        ];

        foreach (var table in expected)
        {
            Assert.Contains(table, tableNames);
        }
    }

    [Fact] // PERSIST-RULE-02
    public async Task CheckConstraint_RejectsInvalidStatusValue()
    {
        await using var context = _fixture.CreateContext();
        var (teamId, categoryId) = await SeedTeamAndCategoryAsync(context);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO tickets (title, description, work_type, priority, status, requester_id,
                                  team_id, category_id, created_at, updated_at,
                                  sla_target_minutes, sla_started_at, sla_due_at)
            VALUES ('Test ticket title', 'Test description long enough.', 'Incident', 'Medium',
                    'NotARealStatus', gen_random_uuid(), {teamId}, {categoryId}, now(), now(),
                    60, now(), now() + interval '1 hour')
            """));

        Assert.Equal("23514", ex.SqlState); // check_violation
    }

    [Fact] // PERSIST-RULE-03
    public async Task CheckConstraint_RejectsClosedTicketWithoutClosedAt()
    {
        await using var context = _fixture.CreateContext();
        var (teamId, categoryId) = await SeedTeamAndCategoryAsync(context);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO tickets (title, description, work_type, priority, status, requester_id,
                                  team_id, category_id, created_at, updated_at,
                                  sla_target_minutes, sla_started_at, sla_due_at,
                                  resolved_at, resolution_code, resolution_notes)
            VALUES ('Test ticket title', 'Test description long enough.', 'Incident', 'Medium',
                    'Closed', gen_random_uuid(), {teamId}, {categoryId}, now(), now(),
                    60, now(), now() + interval '1 hour',
                    now(), 'Fixed', 'Replaced the part.')
            """));

        Assert.Equal("23514", ex.SqlState);
    }

    [Fact] // PERSIST-RULE-04
    public async Task CheckConstraint_RejectsSlaDueAtBeforeSlaStartedAt()
    {
        await using var context = _fixture.CreateContext();
        var (teamId, categoryId) = await SeedTeamAndCategoryAsync(context);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO tickets (title, description, work_type, priority, status, requester_id,
                                  team_id, category_id, created_at, updated_at,
                                  sla_target_minutes, sla_started_at, sla_due_at)
            VALUES ('Test ticket title', 'Test description long enough.', 'Incident', 'Medium',
                    'Open', gen_random_uuid(), {teamId}, {categoryId}, now(), now(),
                    60, now(), now() - interval '1 hour')
            """));

        Assert.Equal("23514", ex.SqlState);
    }

    [Fact] // PERSIST-RULE-01 — Restrict: a team referenced by a category cannot be deleted
    public async Task ForeignKey_RestrictsDeletingATeamStillReferencedByACategory()
    {
        await using var context = _fixture.CreateContext();
        var (teamId, _) = await SeedTeamAndCategoryAsync(context);

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlAsync($"DELETE FROM teams WHERE id = {teamId}"));

        Assert.Equal("23503", ex.SqlState); // foreign_key_violation
    }

    [Fact] // PERSIST-RULE-01 — Cascade: deleting a ticket deletes its comments and events
    public async Task ForeignKey_CascadesTicketDeletionToCommentsAndEvents()
    {
        await using var context = _fixture.CreateContext();
        var ticket = await SeedTicketAsync(context);
        ticket.AddComment(ticket.RequesterId, "Any update?", isInternal: false, DateTimeOffset.UtcNow);
        await context.SaveChangesAsync();
        var ticketId = ticket.Id;

        await context.Database.ExecuteSqlAsync($"DELETE FROM tickets WHERE id = {ticketId}");

        var remainingComments = await context.TicketComments.CountAsync(c => c.TicketId == ticketId);
        var remainingEvents = await context.TicketEvents.CountAsync(e => e.TicketId == ticketId);
        Assert.Equal(0, remainingComments);
        Assert.Equal(0, remainingEvents);
    }

    [Fact] // PERSIST-RULE-05 — unique ticket reference
    public async Task UniqueConstraint_RejectsDuplicateTicketReference()
    {
        await using var context = _fixture.CreateContext();
        var first = await SeedTicketAsync(context);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO tickets (reference, title, description, work_type, priority, status, requester_id,
                                  team_id, category_id, created_at, updated_at,
                                  sla_target_minutes, sla_started_at, sla_due_at)
            VALUES ({first.Reference}, 'Another ticket title', 'Another description long enough.',
                    'Incident', 'Medium', 'Open', gen_random_uuid(), {first.TeamId}, {first.CategoryId},
                    now(), now(), 60, now(), now() + interval '1 hour')
            """));

        Assert.Equal("23505", ex.SqlState); // unique_violation
    }

    [Fact] // PERSIST-RULE-06 / ADR-0011 — concurrent edits conflict instead of silently overwriting
    public async Task Concurrency_ConflictingUpdatesThrowDbUpdateConcurrencyException()
    {
        var ticketId = (await SeedTicketAsync(_fixture.CreateContext())).Id;
        var now = DateTimeOffset.UtcNow;

        await using var firstContext = _fixture.CreateContext();
        await using var secondContext = _fixture.CreateContext();

        var firstTicket = await firstContext.Tickets.SingleAsync(t => t.Id == ticketId);
        var secondTicket = await secondContext.Tickets.SingleAsync(t => t.Id == ticketId);

        firstTicket.Assign(firstTicket.RequesterId, assigneeIsActiveTeamMember: true, firstTicket.RequesterId, now);
        await firstContext.SaveChangesAsync();

        secondTicket.Assign(secondTicket.RequesterId, assigneeIsActiveTeamMember: true, secondTicket.RequesterId, now);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondContext.SaveChangesAsync());
    }

    private static async Task<(int TeamId, int CategoryId)> SeedTeamAndCategoryAsync(FlowOpsDbContext context)
    {
        var organization = new Organization(0, $"Org-{Guid.NewGuid():N}", DateTimeOffset.UtcNow);
        context.Add(organization);
        await context.SaveChangesAsync();

        var team = new Team(0, organization.Id, $"Team-{Guid.NewGuid():N}", DateTimeOffset.UtcNow);
        context.Add(team);
        await context.SaveChangesAsync();

        var category = new Category(0, team.Id, $"Category-{Guid.NewGuid():N}", WorkType.Incident, DateTimeOffset.UtcNow);
        context.Add(category);
        await context.SaveChangesAsync();

        return (team.Id, category.Id);
    }

    /// <summary>
    /// A ticket's <c>requester_id</c>/<c>assignee_id</c> are real FKs to <c>AspNetUsers</c>
    /// (docs/database.md §7, PERSIST-RULE-01), so a persisted ticket needs a persisted user — a
    /// bare <c>Guid.NewGuid()</c> is rejected by <c>fk_tickets_asp_net_users_requester_id</c>.
    /// Inserted directly rather than through <c>UserManager</c>: these tests exercise the schema,
    /// not Identity's password/role pipeline.
    /// </summary>
    private static async Task<Guid> SeedUserAsync(FlowOpsDbContext context)
    {
        var user = new ApplicationUser
        {
            UserName = $"{Guid.NewGuid():N}@test.local",
            Email = $"{Guid.NewGuid():N}@test.local",
            DisplayName = "Schema Test User",
            IsActive = true,
        };

        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<Ticket> SeedTicketAsync(FlowOpsDbContext context)
    {
        var (teamId, categoryId) = await SeedTeamAndCategoryAsync(context);
        var requesterId = await SeedUserAsync(context);

        var ticket = Ticket.Create(
            title: "Printer on 3rd floor is jammed",
            description: "The printer near the east stairwell is jammed.",
            workType: WorkType.Incident,
            priority: Priority.Medium,
            requesterId: requesterId,
            teamId: teamId,
            categoryId: categoryId,
            categoryTeamId: teamId,
            projectId: null,
            slaTargetMinutes: 1440,
            now: DateTimeOffset.UtcNow);

        context.Tickets.Add(ticket);
        await context.SaveChangesAsync();
        return ticket;
    }
}
