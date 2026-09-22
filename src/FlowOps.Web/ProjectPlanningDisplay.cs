using FlowOps.Application.Planning;
using FlowOps.Domain.Planning;

namespace FlowOps.Web;

/// <summary>Model for the shared project header + Overview/Board/Tickets tabs.</summary>
public sealed record ProjectHeaderViewModel(ProjectHeader Header, string ActiveTab);

/// <summary>Presentation-only helpers for sprints — no rules (those live in Domain/Application).</summary>
public static class SprintDisplay
{
    /// <summary>"Sep 21–27, 2026" within a month, "Sep 28 – Oct 4, 2026" across months,
    /// full dates across years.</summary>
    public static string Range(DateOnly start, DateOnly end)
    {
        if (start.Year != end.Year)
        {
            return $"{start:MMM d, yyyy} – {end:MMM d, yyyy}";
        }

        return start.Month == end.Month
            ? $"{start:MMM d}–{end:%d}, {end:yyyy}"
            : $"{start:MMM d} – {end:MMM d}, {end:yyyy}";
    }

    public static string StatusLabel(SprintStatus status) => status switch
    {
        SprintStatus.Active => "Active",
        SprintStatus.Planned => "Planned",
        SprintStatus.Cancelled => "Cancelled",
        _ => "Completed",
    };

    /// <summary>How a ticket relates to its sprint, in words: "Current sprint" (the active one),
    /// "Current sprint · not started" (planned for it but not yet moved onto the board), "Planned
    /// sprint" (a future one), "Completed" or "Cancelled".</summary>
    public static string TicketSprintLabel(SprintStatus status, bool notStarted) => status switch
    {
        SprintStatus.Active => notStarted ? "Current sprint · not started" : "Current sprint",
        SprintStatus.Planned => "Planned sprint",
        SprintStatus.Cancelled => "Cancelled",
        _ => "Completed",
    };

    public static string StatusBadgeClass(SprintStatus status) => status switch
    {
        SprintStatus.Active => "status-badge--teal",
        SprintStatus.Planned or SprintStatus.Cancelled => "status-badge--muted",
        _ => "status-badge--ok",
    };

    public static string ColumnLabel(BoardColumnKey key) => key switch
    {
        BoardColumnKey.Backlog => "Planned",
        BoardColumnKey.Open => "Open",
        BoardColumnKey.InProgress => "In Progress",
        BoardColumnKey.Pending => "Pending",
        _ => "Done",
    };
}

/// <summary>Everything the shared row/card action menu needs. The authorization inputs
/// (<see cref="TeamId"/>, <see cref="RequesterId"/>, <see cref="AssigneeId"/>) are what
/// <c>TicketAccessPolicy</c> asks for — the menu only decides which items to <em>offer</em>; every
/// handler re-authorizes server-side.</summary>
public sealed record PlanningMenuViewModel(
    int ProjectId,
    int TicketId,
    int TeamId,
    Guid RequesterId,
    Guid? AssigneeId,
    FlowOps.Domain.Tickets.Status Status,
    int? SprintId,
    bool SprintBacklog,
    string ReturnUrl,
    IReadOnlyList<SprintSummary> MoveTargets,
    FlowOps.Domain.Tickets.CurrentUser Caller);

/// <summary>What the shared sprint action buttons (start / complete / cancel) need.</summary>
public sealed record SprintActionsViewModel(
    int ProjectId,
    int SprintId,
    FlowOps.Domain.Planning.SprintStatus Status,
    bool CanManage,
    bool HasActiveSprint,
    string ReturnUrl);
