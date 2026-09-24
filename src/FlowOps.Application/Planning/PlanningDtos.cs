using FlowOps.Application.Tickets;
using FlowOps.Domain.Planning;
using FlowOps.Domain.Tickets;

namespace FlowOps.Application.Planning;

/// <summary>Read-side shapes for project planning (ADR-0029). Like every other read shape, EF
/// entities never cross into Web (CLAUDE.md §11.2).</summary>
public sealed record ProjectHeader(int ProjectId, string Name, bool IsActive);

/// <param name="TicketCount">Tickets in this sprint <em>that the caller may see</em> — the same
/// visibility scope as every ticket list, so a count can never disclose a hidden ticket.</param>
public sealed record SprintSummary(int SprintId, string Name, DateOnly StartDate, DateOnly EndDate, SprintStatus Status, int TicketCount);

/// <summary>One row of the Projects list: the project plus its current sprint, if any.</summary>
public sealed record ProjectListEntry(
    int ProjectId,
    string Name,
    SprintSummary? ActiveSprint,
    int VisibleTicketCount);

/// <summary>Phase 29D: the sidebar's quick-navigation project list — just enough to render a link
/// and a name, deliberately without <see cref="ProjectListEntry"/>'s sprint/ticket-count
/// enrichment. The sidebar renders on every authenticated page, so its own query stays the
/// smallest one that satisfies it rather than reusing the heavier Projects-page shape.</summary>
public sealed record ProjectNavOption(int ProjectId, string Name);

/// <summary>Where the active sprint's tickets sit — derived from the existing status model plus the
/// backlog flag, never a second workflow.</summary>
public sealed record SprintProgress(int Total, int Backlog, int InProgress, int Pending, int Done);

public sealed record ProjectOverview(
    ProjectHeader Header,
    SprintSummary? ActiveSprint,
    SprintProgress? Progress,
    IReadOnlyList<SprintSummary> Sprints,
    int UnplannedTickets = 0);

/// <summary>The board's columns (Assigned is deliberately not its own column: an assigned ticket sits in Open and shows its assignee). <see cref="Done"/> is <c>Resolved</c> + <c>Closed</c> — the two
/// terminal statuses the whole codebase already treats as "finished".</summary>
public enum BoardColumnKey
{
    Backlog,
    Open,
    InProgress,
    Pending,
    Done,
}

/// <summary>The one place that maps the existing <see cref="Status"/> model (plus the backlog flag)
/// onto board columns.</summary>
public static class BoardColumns
{
    public static BoardColumnKey For(Status status, bool sprintBacklog) =>
        status switch
        {
            Status.Open or Status.Assigned when sprintBacklog => BoardColumnKey.Backlog,
            Status.Open or Status.Assigned => BoardColumnKey.Open,
            Status.InProgress => BoardColumnKey.InProgress,
            Status.Pending => BoardColumnKey.Pending,
            _ => BoardColumnKey.Done,
        };
}

/// <summary>Carries the authorization inputs (<see cref="TeamId"/>, <see cref="RequesterId"/>,
/// <see cref="AssigneeId"/>) so the page can ask <c>TicketAccessPolicy</c> which actions to offer —
/// the same reason <see cref="TicketDetail"/> does.</summary>
public sealed record BoardCard(
    int TicketId,
    string Reference,
    string Title,
    Priority Priority,
    Status Status,
    bool SprintBacklog,
    int TeamId,
    Guid RequesterId,
    Guid? AssigneeId,
    string? AssigneeName,
    DateTimeOffset? DueDate,
    TicketSlaView Sla,
    string? CarriedFromSprint = null);

public sealed record BoardColumn(BoardColumnKey Key, IReadOnlyList<BoardCard> Cards);

public sealed record ProjectBoard(
    ProjectHeader Header,
    SprintSummary? ActiveSprint,
    IReadOnlyList<SprintSummary> PlannedSprints,
    IReadOnlyList<BoardColumn> Columns);

/// <summary>Which sprint bucket the Tickets list is narrowed to.</summary>
public enum SprintScope
{
    All,
    Current,
    NoSprint,
    Future,
    Completed,
}

public sealed record ProjectTicketFilter(
    SprintScope Sprint = SprintScope.All,
    Status? Status = null,
    Priority? Priority = null,
    Guid? AssigneeId = null,
    bool Unassigned = false);

public sealed record ProjectTicketRow(
    int TicketId,
    string Reference,
    string Title,
    Priority Priority,
    Status Status,
    int TeamId,
    Guid RequesterId,
    Guid? AssigneeId,
    string? AssigneeName,
    DateTimeOffset? DueDate,
    int? SprintId,
    string? SprintName,
    DateOnly? SprintStart,
    DateOnly? SprintEnd,
    SprintStatus? SprintStatus,
    bool SprintBacklog,
    string? CarriedFromSprint = null);

public sealed record ProjectAssigneeOption(Guid UserId, string DisplayName);

public sealed record ProjectTicketsView(
    ProjectHeader Header,
    PagedResult<ProjectTicketRow> Tickets,
    IReadOnlyList<SprintSummary> MoveTargets,
    IReadOnlyList<ProjectAssigneeOption> Assignees);

public sealed record SprintMutationResult(bool Succeeded, int? SprintId, string? Error)
{
    public static SprintMutationResult Success(int sprintId) => new(true, sprintId, null);

    public static SprintMutationResult Failed(string error) => new(false, null, error);
}

/// <summary>One line of the Sprints (archive) list. For a completed sprint the counts are the frozen
/// completion snapshot (ADR-0030), so they stay true after unfinished tickets are carried forward;
/// for a planned/active sprint they are live. Always limited to tickets the caller may see.</summary>
public sealed record SprintArchiveEntry(
    int SprintId,
    string Name,
    DateOnly StartDate,
    DateOnly EndDate,
    SprintStatus Status,
    int TotalTickets,
    int DoneTickets)
{
    public int UnfinishedTickets => TotalTickets - DoneTickets;
}

/// <summary>A ticket as it relates to one sprint. <see cref="WasDone"/> is the result <em>in that
/// sprint</em> (frozen for a completed sprint); the <c>Current*</c> fields say where the ticket is now,
/// which differs once it has been carried forward.</summary>
public sealed record SprintTicketRow(
    int TicketId,
    string Reference,
    string Title,
    Priority Priority,
    Status StatusInSprint,
    Status CurrentStatus,
    bool WasDone,
    int TeamId,
    Guid RequesterId,
    Guid? AssigneeId,
    string? AssigneeName,
    DateTimeOffset? DueDate,
    int? CurrentSprintId,
    string? CurrentSprintName);

public sealed record SprintDetailView(
    ProjectHeader Header,
    SprintArchiveEntry Sprint,
    IReadOnlyList<SprintTicketRow> Done,
    IReadOnlyList<SprintTicketRow> Unfinished,
    SprintSummary? ActiveSprint,
    IReadOnlyList<SprintSummary> MoveTargets,
    bool IsSnapshot);

public sealed record SprintCarryForwardResult(bool Succeeded, int Moved, int Skipped, string? Error)
{
    public static SprintCarryForwardResult Failed(string error) => new(false, 0, 0, error);
}

public sealed record SprintArchiveView(ProjectHeader Header, IReadOnlyList<SprintArchiveEntry> Sprints);
