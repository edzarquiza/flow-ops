using FlowOps.Application.Tickets;
using FlowOps.Domain.Planning;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FlowOps.Application.Planning;

/// <summary>
/// Read side of project planning (ADR-0029): the Projects list, a project's overview, its sprint
/// board, and its paginated ticket planning list. Every ticket read starts from
/// <see cref="TicketQueryService.ApplyViewScope"/> — the same organization + team visibility as
/// the Work Queue — so planning views can never show a ticket the caller could not already open.
/// A project that is not in the caller's organization is indistinguishable from a missing one
/// (returns <see langword="null"/>).
/// </summary>
public sealed class ProjectPlanningQueryService
{
    public const int PageSize = 25;

    /// <summary>Safety bound for the board only: a sprint is a week of one project's work, and the
    /// board has no pager. Reaching this is a sign the sprint is mis-scoped, not a real workload.</summary>
    private const int BoardTicketLimit = 500;

    private readonly FlowOpsDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public ProjectPlanningQueryService(FlowOpsDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    /// <summary>Phase 29D: the sidebar's project quick-navigation list — the same organization
    /// boundary and active-only filter as <see cref="GetProjectsAsync"/>'s own first query, without
    /// the sprint/ticket-count enrichment that method adds for the full Projects page. Deliberately
    /// its own minimal query rather than calling <see cref="GetProjectsAsync"/> and discarding the
    /// extra fields — the sidebar renders on every authenticated page, so it must not pay for two
    /// more queries (active sprints, ticket counts) it never uses.</summary>
    public async Task<IReadOnlyList<ProjectNavOption>> GetProjectNavOptionsAsync(CurrentUser user, CancellationToken cancellationToken = default) =>
        await _dbContext.Projects
            .AsNoTracking()
            .Where(p => p.OrganizationId == user.OrganizationId && p.IsActive)
            .OrderBy(p => p.Name)
            .Select(p => new ProjectNavOption(p.Id, p.Name))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ProjectListEntry>> GetProjectsAsync(CurrentUser user, CancellationToken cancellationToken = default)
    {
        var projects = await _dbContext.Projects
            .AsNoTracking()
            .Where(p => p.OrganizationId == user.OrganizationId && p.IsActive)
            .OrderBy(p => p.Name)
            .Select(p => new { p.Id, p.Name })
            .ToListAsync(cancellationToken);

        if (projects.Count == 0)
        {
            return [];
        }

        var projectIds = projects.Select(p => p.Id).ToArray();

        var activeSprints = await _dbContext.Sprints
            .AsNoTracking()
            .Where(s => projectIds.Contains(s.ProjectId) && s.Status == SprintStatus.Active)
            .ToListAsync(cancellationToken);

        var ticketCounts = await ScopedTickets(user)
            .Where(t => t.ProjectId != null && projectIds.Contains(t.ProjectId.Value))
            .GroupBy(t => t.ProjectId!.Value)
            .Select(g => new { ProjectId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.ProjectId, g => g.Count, cancellationToken);

        var activeSprintIds = activeSprints.Select(s => s.Id).ToArray();
        var sprintCounts = await ScopedTickets(user)
            .Where(t => t.SprintId != null && activeSprintIds.Contains(t.SprintId.Value))
            .GroupBy(t => t.SprintId!.Value)
            .Select(g => new { SprintId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.SprintId, g => g.Count, cancellationToken);

        return projects
            .Select(p =>
            {
                var sprint = activeSprints.FirstOrDefault(s => s.ProjectId == p.Id);
                return new ProjectListEntry(
                    p.Id,
                    p.Name,
                    sprint is null ? null : ToSummary(sprint, sprintCounts.GetValueOrDefault(sprint.Id)),
                    ticketCounts.GetValueOrDefault(p.Id));
            })
            .ToList();
    }

    public async Task<ProjectOverview?> GetOverviewAsync(CurrentUser user, int projectId, CancellationToken cancellationToken = default)
    {
        var header = await GetHeaderAsync(user, projectId, cancellationToken);
        if (header is null)
        {
            return null;
        }

        var sprints = await LoadSprintSummariesAsync(user, projectId, cancellationToken);
        var active = sprints.FirstOrDefault(s => s.Status == SprintStatus.Active);

        SprintProgress? progress = null;
        if (active is not null)
        {
            var groups = await ScopedTickets(user)
                .Where(t => t.SprintId == active.SprintId)
                .GroupBy(t => new { t.Status, t.SprintBacklog })
                .Select(g => new { g.Key.Status, g.Key.SprintBacklog, Count = g.Count() })
                .ToListAsync(cancellationToken);

            int Sum(BoardColumnKey key) => groups.Where(g => BoardColumns.For(g.Status, g.SprintBacklog) == key).Sum(g => g.Count);

            progress = new SprintProgress(
                groups.Sum(g => g.Count),
                Sum(BoardColumnKey.Backlog),
                Sum(BoardColumnKey.InProgress),
                Sum(BoardColumnKey.Pending),
                Sum(BoardColumnKey.Done));
        }

        // Same population as the Tickets page's "No sprint" filter, so the number and the list agree.
        var unplanned = await ScopedTickets(user)
            .CountAsync(t => t.ProjectId == projectId && t.SprintId == null, cancellationToken);

        return new ProjectOverview(header, active, progress, sprints, unplanned);
    }

    public async Task<ProjectBoard?> GetBoardAsync(CurrentUser user, int projectId, CancellationToken cancellationToken = default)
    {
        var header = await GetHeaderAsync(user, projectId, cancellationToken);
        if (header is null)
        {
            return null;
        }

        var sprints = await LoadSprintSummariesAsync(user, projectId, cancellationToken);
        var active = sprints.FirstOrDefault(s => s.Status == SprintStatus.Active);
        var planned = sprints.Where(s => s.Status == SprintStatus.Planned).OrderBy(s => s.StartDate).ToList();

        var cards = new List<BoardCard>();
        if (active is not null)
        {
            var rows = await (
                from ticket in ScopedTickets(user).Where(t => t.SprintId == active.SprintId)
                join assignee in _dbContext.Users on ticket.AssigneeId equals assignee.Id into assignees
                from assignee in assignees.DefaultIfEmpty()
                orderby ticket.Id
                select new
                {
                    ticket.Id,
                    Reference = ticket.Reference!,
                    ticket.Title,
                    ticket.Priority,
                    ticket.Status,
                    ticket.SprintBacklog,
                    ticket.TeamId,
                    ticket.RequesterId,
                    ticket.AssigneeId,
                    AssigneeName = assignee == null ? null : assignee.DisplayName,
                    ticket.DueDate,
                    Sla = new TicketQueryService.SlaFacts(
                        ticket.WorkType,
                        ticket.Priority,
                        ticket.Status,
                        ticket.SlaMet,
                        ticket.SlaStartedAt,
                        ticket.SlaDueAt,
                        ticket.SlaPausedMinutes,
                        ticket.SlaTargetMinutes),
                })
                .Take(BoardTicketLimit)
                .ToListAsync(cancellationToken);

            var configurations = await _dbContext.SlaConfigurations.AsNoTracking().ToListAsync(cancellationToken);
            var now = _timeProvider.GetUtcNow();
            var carriedFrom = await SprintHistoryQueries.LoadCarriedFromAsync(_dbContext, rows.Select(r => r.Id).ToList(), cancellationToken);

            // Priority is persisted as text, so "most urgent first" is decided here, over the
            // already-bounded sprint result — not in SQL, where it would sort alphabetically.
            cards = rows
                .Select(r => new BoardCard(
                    r.Id, r.Reference, r.Title, r.Priority, r.Status, r.SprintBacklog, r.TeamId, r.RequesterId,
                    r.AssigneeId, r.AssigneeName, r.DueDate, TicketQueryService.BuildSlaView(r.Sla, configurations, now),
                    carriedFrom.GetValueOrDefault(r.Id)))
                .OrderByDescending(c => c.Priority)
                .ThenBy(c => c.TicketId)
                .ToList();
        }

        var columns = Enum.GetValues<BoardColumnKey>()
            .Select(key => new BoardColumn(key, cards.Where(c => BoardColumns.For(c.Status, c.SprintBacklog) == key).ToList()))
            .ToList();

        return new ProjectBoard(header, active, planned, columns);
    }

    public async Task<ProjectTicketsView?> GetTicketsAsync(
        CurrentUser user,
        int projectId,
        int pageNumber,
        ProjectTicketFilter filter,
        CancellationToken cancellationToken = default)
    {
        var header = await GetHeaderAsync(user, projectId, cancellationToken);
        if (header is null)
        {
            return null;
        }

        if (pageNumber < 1)
        {
            pageNumber = 1;
        }

        var query =
            from ticket in ScopedTickets(user).Where(t => t.ProjectId == projectId)
            join sprint in _dbContext.Sprints on ticket.SprintId equals sprint.Id into sprints
            from sprint in sprints.DefaultIfEmpty()
            join assignee in _dbContext.Users on ticket.AssigneeId equals assignee.Id into assignees
            from assignee in assignees.DefaultIfEmpty()
            select new { ticket, sprint, assignee };

        query = filter.Sprint switch
        {
            SprintScope.Current => query.Where(x => x.sprint != null && x.sprint.Status == SprintStatus.Active),
            SprintScope.Future => query.Where(x => x.sprint != null && x.sprint.Status == SprintStatus.Planned),
            SprintScope.Completed => query.Where(x => x.sprint != null && x.sprint.Status == SprintStatus.Completed),
            SprintScope.NoSprint => query.Where(x => x.sprint == null),
            _ => query,
        };

        if (filter.Status is { } status)
        {
            query = query.Where(x => x.ticket.Status == status);
        }

        if (filter.Priority is { } priority)
        {
            query = query.Where(x => x.ticket.Priority == priority);
        }

        if (filter.Unassigned)
        {
            query = query.Where(x => x.ticket.AssigneeId == null);
        }
        else if (filter.AssigneeId is { } assigneeId)
        {
            query = query.Where(x => x.ticket.AssigneeId == assigneeId);
        }

        var total = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(x => x.ticket.Id)
            .Skip((pageNumber - 1) * PageSize)
            .Take(PageSize)
            .Select(x => new ProjectTicketRow(
                x.ticket.Id,
                x.ticket.Reference!,
                x.ticket.Title,
                x.ticket.Priority,
                x.ticket.Status,
                x.ticket.TeamId,
                x.ticket.RequesterId,
                x.ticket.AssigneeId,
                x.assignee == null ? null : x.assignee.DisplayName,
                x.ticket.DueDate,
                x.ticket.SprintId,
                x.sprint == null ? null : x.sprint.Name,
                x.sprint == null ? null : x.sprint.StartDate,
                x.sprint == null ? null : x.sprint.EndDate,
                x.sprint == null ? null : x.sprint.Status,
                x.ticket.SprintBacklog))
            .ToListAsync(cancellationToken);

        var carriedFrom = await SprintHistoryQueries.LoadCarriedFromAsync(_dbContext, items.Select(i => i.TicketId).ToList(), cancellationToken);
        items = items.Select(i => i with { CarriedFromSprint = carriedFrom.GetValueOrDefault(i.TicketId) }).ToList();

        // Destinations: any sprint that can still accept tickets. Completed sprints are history.
        var moveTargets = (await LoadSprintSummariesAsync(user, projectId, cancellationToken))
            .Where(s => s.Status == SprintStatus.Planned || s.Status == SprintStatus.Active)
            .OrderBy(s => s.StartDate)
            .ToList();

        var assigneeOptions = await ScopedTickets(user)
            .Where(t => t.ProjectId == projectId && t.AssigneeId != null)
            .Join(_dbContext.Users, t => t.AssigneeId, u => (Guid?)u.Id, (t, u) => new { u.Id, u.DisplayName })
            .Distinct()
            .OrderBy(a => a.DisplayName)
            .Select(a => new ProjectAssigneeOption(a.Id, a.DisplayName))
            .ToListAsync(cancellationToken);

        return new ProjectTicketsView(header, new PagedResult<ProjectTicketRow>(items, pageNumber, PageSize, total), moveTargets, assigneeOptions);
    }

    /// <summary>The Sprints page (ADR-0030): every sprint of the project — planned, active, completed,
    /// cancelled — newest first, with total/done/unfinished ticket counts limited to what the caller
    /// may see. A completed sprint reports its frozen completion snapshot, so the numbers stay true
    /// after unfinished tickets are carried forward.</summary>
    public async Task<SprintArchiveView?> GetSprintArchiveAsync(CurrentUser user, int projectId, CancellationToken cancellationToken = default)
    {
        var archiveHeader = await GetHeaderAsync(user, projectId, cancellationToken);
        if (archiveHeader is null)
        {
            return null;
        }

        var sprints = await _dbContext.Sprints
            .AsNoTracking()
            .Where(s => s.ProjectId == projectId)
            .OrderByDescending(s => s.StartDate)
            .ThenByDescending(s => s.Id)
            .ToListAsync(cancellationToken);

        var snapshotCounts = await (
            from snapshot in _dbContext.SprintTicketSnapshots
            join ticket in ScopedTickets(user) on snapshot.TicketId equals ticket.Id
            join sprint in _dbContext.Sprints on snapshot.SprintId equals sprint.Id
            where sprint.ProjectId == projectId
            select new { snapshot.SprintId, snapshot.WasDone })
            .GroupBy(x => x.SprintId)
            .Select(g => new { SprintId = g.Key, Total = g.Count(), Done = g.Count(x => x.WasDone) })
            .ToDictionaryAsync(g => g.SprintId, g => (g.Total, g.Done), cancellationToken);

        var liveCounts = await ScopedTickets(user)
            .Where(t => t.ProjectId == projectId && t.SprintId != null)
            .GroupBy(t => t.SprintId!.Value)
            .Select(g => new { SprintId = g.Key, Total = g.Count(), Done = g.Count(t => t.Status == Status.Resolved || t.Status == Status.Closed) })
            .ToDictionaryAsync(g => g.SprintId, g => (g.Total, g.Done), cancellationToken);

        return new SprintArchiveView(
            archiveHeader,
            sprints
                .Select(s =>
                {
                    var (total, done) = s.Status == SprintStatus.Completed && snapshotCounts.TryGetValue(s.Id, out var frozen)
                        ? frozen
                        : liveCounts.GetValueOrDefault(s.Id);
                    return new SprintArchiveEntry(s.Id, s.Name, s.StartDate, s.EndDate, s.Status, total, done);
                })
                .ToList());
    }

    /// <summary>One sprint's ticket list, split into finished and unfinished. For a completed sprint
    /// this is the frozen snapshot (with each ticket's <em>current</em> location alongside), so the
    /// page answers both "what happened in this sprint" and "where is it now". Tickets are opened on
    /// the existing Ticket Detail page — there is no second ticket view.</summary>
    public async Task<SprintDetailView?> GetSprintDetailAsync(CurrentUser user, int projectId, int sprintId, CancellationToken cancellationToken = default)
    {
        var header = await GetHeaderAsync(user, projectId, cancellationToken);
        if (header is null)
        {
            return null;
        }

        var sprint = await _dbContext.Sprints
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == sprintId && s.ProjectId == projectId, cancellationToken);
        if (sprint is null)
        {
            return null;
        }

        var useSnapshot = sprint.Status == SprintStatus.Completed
            && await _dbContext.SprintTicketSnapshots.AnyAsync(s => s.SprintId == sprintId, cancellationToken);

        List<SprintTicketRow> rows;
        if (useSnapshot)
        {
            rows = await (
                from snapshot in _dbContext.SprintTicketSnapshots.Where(s => s.SprintId == sprintId)
                join ticket in ScopedTickets(user) on snapshot.TicketId equals ticket.Id
                join assignee in _dbContext.Users on ticket.AssigneeId equals assignee.Id into assignees
                from assignee in assignees.DefaultIfEmpty()
                join current in _dbContext.Sprints on ticket.SprintId equals current.Id into currents
                from current in currents.DefaultIfEmpty()
                orderby ticket.Id
                select new SprintTicketRow(
                    ticket.Id, ticket.Reference!, ticket.Title, ticket.Priority, snapshot.StatusAtCompletion, ticket.Status, snapshot.WasDone,
                    ticket.TeamId, ticket.RequesterId, ticket.AssigneeId, assignee == null ? null : assignee.DisplayName, ticket.DueDate,
                    ticket.SprintId, current == null ? null : current.Name))
                .Take(BoardTicketLimit)
                .ToListAsync(cancellationToken);
        }
        else
        {
            rows = await (
                from ticket in ScopedTickets(user).Where(t => t.SprintId == sprintId)
                join assignee in _dbContext.Users on ticket.AssigneeId equals assignee.Id into assignees
                from assignee in assignees.DefaultIfEmpty()
                orderby ticket.Id
                select new SprintTicketRow(
                    ticket.Id, ticket.Reference!, ticket.Title, ticket.Priority, ticket.Status, ticket.Status,
                    ticket.Status == Status.Resolved || ticket.Status == Status.Closed,
                    ticket.TeamId, ticket.RequesterId, ticket.AssigneeId, assignee == null ? null : assignee.DisplayName, ticket.DueDate,
                    ticket.SprintId, sprint.Name))
                .Take(BoardTicketLimit)
                .ToListAsync(cancellationToken);
        }

        var summaries = await LoadSprintSummariesAsync(user, projectId, cancellationToken);
        var entry = new SprintArchiveEntry(sprint.Id, sprint.Name, sprint.StartDate, sprint.EndDate, sprint.Status, rows.Count, rows.Count(r => r.WasDone));

        return new SprintDetailView(
            header,
            entry,
            rows.Where(r => r.WasDone).ToList(),
            rows.Where(r => !r.WasDone).ToList(),
            summaries.FirstOrDefault(s => s.Status == SprintStatus.Active),
            summaries.Where(s => s.Status is SprintStatus.Planned or SprintStatus.Active).OrderBy(s => s.StartDate).ToList(),
            useSnapshot);
    }

    private IQueryable<Ticket> ScopedTickets(CurrentUser user) =>
        TicketQueryService.ApplyViewScope(_dbContext, _dbContext.Tickets.AsNoTracking(), user);

    private Task<ProjectHeader?> GetHeaderAsync(CurrentUser user, int projectId, CancellationToken cancellationToken) =>
        _dbContext.Projects
            .AsNoTracking()
            .Where(p => p.Id == projectId && p.OrganizationId == user.OrganizationId)
            .Select(p => new ProjectHeader(p.Id, p.Name, p.IsActive))
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>All of a project's sprints, newest first, with caller-visible ticket counts.</summary>
    private async Task<List<SprintSummary>> LoadSprintSummariesAsync(CurrentUser user, int projectId, CancellationToken cancellationToken)
    {
        var sprints = await _dbContext.Sprints
            .AsNoTracking()
            .Where(s => s.ProjectId == projectId)
            .OrderByDescending(s => s.StartDate)
            .ThenByDescending(s => s.Id)
            .ToListAsync(cancellationToken);

        var counts = await ScopedTickets(user)
            .Where(t => t.ProjectId == projectId && t.SprintId != null)
            .GroupBy(t => t.SprintId!.Value)
            .Select(g => new { SprintId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.SprintId, g => g.Count, cancellationToken);

        return sprints.Select(s => ToSummary(s, counts.GetValueOrDefault(s.Id))).ToList();
    }

    private static SprintSummary ToSummary(Sprint sprint, int ticketCount) =>
        new(sprint.Id, sprint.Name, sprint.StartDate, sprint.EndDate, sprint.Status, ticketCount);
}
