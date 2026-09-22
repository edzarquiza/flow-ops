using FlowOps.Domain.Planning;
using FlowOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FlowOps.Application.Planning;

/// <summary>
/// Shared read helpers over sprint history. "Carried from Sprint 1" is derived from the existing
/// completion snapshots (ADR-0030) — no new table, no copy of history: a ticket was carried when a
/// <em>completed</em> sprint's snapshot recorded it unfinished and it now sits in a different sprint.
/// It stays true after further movement (the snapshot never changes), and disappears if the ticket is
/// taken out of every sprint, because then it was not carried anywhere.
/// </summary>
internal static class SprintHistoryQueries
{
    /// <summary>For each ticket id, the name of the most recently completed sprint it was carried
    /// from. Callers pass ids they already loaded through an authorization-scoped query, so nothing
    /// here widens visibility.</summary>
    public static async Task<Dictionary<int, string>> LoadCarriedFromAsync(
        FlowOpsDbContext dbContext,
        IReadOnlyCollection<int> ticketIds,
        CancellationToken cancellationToken)
    {
        if (ticketIds.Count == 0)
        {
            return [];
        }

        var rows = await (
            from snapshot in dbContext.SprintTicketSnapshots.AsNoTracking()
            join sprint in dbContext.Sprints.AsNoTracking() on snapshot.SprintId equals sprint.Id
            join ticket in dbContext.Tickets.AsNoTracking() on snapshot.TicketId equals ticket.Id
            where ticketIds.Contains(snapshot.TicketId)
                && !snapshot.WasDone
                && sprint.Status == SprintStatus.Completed
                && ticket.SprintId != null
                && ticket.SprintId != snapshot.SprintId
            select new { snapshot.TicketId, sprint.Name, sprint.CompletedAt, SprintId = sprint.Id })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(r => r.TicketId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(r => r.CompletedAt).ThenByDescending(r => r.SprintId).First().Name);
    }
}

/// <summary>
/// Suggested values for the Create Sprint form: the first free day after every planned or active
/// sprint (only those block overlap — SPRINT-INV-05; completed and cancelled ones are history), two
/// weeks long. The user can edit all of it; the server still validates overlap on submit.
/// </summary>
public static class SprintDefaults
{
    public const int DefaultLengthDays = 14;

    public sealed record Suggestion(string Name, DateOnly Start, DateOnly End);

    public static Suggestion Suggest(IReadOnlyCollection<SprintSummary> sprints, DateOnly today)
    {
        var start = today;
        foreach (var sprint in sprints)
        {
            if (sprint.Status is SprintStatus.Planned or SprintStatus.Active && sprint.EndDate >= start)
            {
                start = sprint.EndDate.AddDays(1);
            }
        }

        return new Suggestion($"Sprint {sprints.Count + 1}", start, start.AddDays(DefaultLengthDays - 1));
    }
}
