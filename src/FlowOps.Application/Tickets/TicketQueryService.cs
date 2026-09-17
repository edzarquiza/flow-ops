using FlowOps.Domain.Sla;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FlowOps.Application.Tickets;

/// <summary>
/// Ticket read-side queries: the paginated work queue and the detail view. Every query is
/// <c>AsNoTracking</c> and projects straight to a DTO (CLAUDE.md §7.2) — no entity is ever
/// materialised to be hand-mapped, and none leaves this layer.
/// </summary>
/// <remarks>
/// SLA status is derived per read (SLA-RULE-10/11) rather than stored, so each query fetches the
/// ticket's raw SLA columns and hands them to <see cref="SlaPolicy.GetStatus"/> once the rows are
/// materialised. It cannot be computed in SQL without reimplementing the policy in the database,
/// which SLA-RULE-12 forbids.
/// </remarks>
public sealed class TicketQueryService
{
    /// <summary>CLAUDE.md §7.2: page size 25.</summary>
    public const int PageSize = 25;

    private readonly FlowOpsDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public TicketQueryService(FlowOpsDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// The raw SLA columns a projection must carry so <see cref="SlaPolicy.GetStatus"/> can be
    /// applied after materialisation. Internal to this service — never leaves it.
    /// </summary>
    private sealed record SlaFacts(
        WorkType WorkType,
        Priority Priority,
        Status Status,
        bool? SlaMet,
        DateTimeOffset SlaStartedAt,
        DateTimeOffset SlaDueAt,
        int SlaPausedMinutes,
        int SlaTargetMinutes);

    /// <summary>
    /// Derives the SLA view for one ticket. <paramref name="configurations"/> is the whole SLA
    /// configuration table, loaded once per query — four reference rows, so resolving per ticket
    /// in memory costs nothing and adds no query per row.
    /// </summary>
    private static TicketSlaView BuildSlaView(SlaFacts facts, IReadOnlyList<SlaConfiguration> configurations, DateTimeOffset now)
    {
        // SLA-RULE-01 resolution stays in the Domain; this only supplies the rows and the clock.
        var configuration = SlaPolicy.ResolveConfiguration(configurations, facts.WorkType, facts.Priority);

        var status = SlaPolicy.GetStatus(
            facts.Status,
            facts.SlaMet,
            now,
            facts.SlaStartedAt,
            facts.SlaDueAt,
            facts.SlaPausedMinutes,
            facts.SlaTargetMinutes,
            configuration.RiskThresholdPercent);

        // A terminal ticket's outcome is the persisted SlaMet fact, not a countdown, so it gets
        // no remaining time — a resolved ticket is neither "42m left" nor "2h over".
        var remaining = facts.Status is Status.Resolved or Status.Closed
            ? (TimeSpan?)null
            : facts.SlaDueAt - now;

        return new TicketSlaView(status, facts.SlaDueAt, facts.SlaTargetMinutes, facts.SlaPausedMinutes, remaining);
    }

    private Task<List<SlaConfiguration>> LoadSlaConfigurationsAsync(CancellationToken cancellationToken) =>
        _dbContext.SlaConfigurations.AsNoTracking().ToListAsync(cancellationToken);

    /// <summary>
    /// One page of the caller's visible work queue, newest first. Both the page query and the
    /// total-count query carry the same authorization scope, so the count reflects what the
    /// caller may actually see rather than the size of the table.
    /// </summary>
    /// <param name="filter">
    /// Phase 10: the closed set of named filters a dashboard KPI card links through to — see
    /// <see cref="TicketQueueFilter"/>. Defaults to <see cref="TicketQueueFilter.None"/>, so every
    /// existing caller's behavior is unchanged. This is deliberately not a general filter/search
    /// framework (CLAUDE.md's ban on unnecessary abstraction) — it is the smallest extension that
    /// makes each KPI "clickable through to the filtered work it represents" (§22).
    /// </param>
    /// <param name="search">
    /// An optional free-text term matched against reference, title, description, requester,
    /// assignee, team, and category — applied as an additional <c>AND</c> narrowing the same
    /// authorization- and filter-scoped query (ATTN/AUTH ordering: scope, then filter, then
    /// search), never a replacement for either. <see langword="null"/>/whitespace is treated as no
    /// search, so an empty box reproduces this method's exact pre-search behavior.
    /// </param>
    public async Task<PagedResult<TicketListItem>> GetQueueAsync(
        CurrentUser user,
        int pageNumber,
        TicketQueueFilter filter = TicketQueueFilter.None,
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1)
        {
            pageNumber = 1;
        }

        var normalizedSearch = SearchTermNormalizer.Normalize(search);

        var scoped = ApplyViewScope(_dbContext, _dbContext.Tickets.AsNoTracking(), user);
        scoped = ApplyQueueFilter(scoped, filter, _timeProvider.GetUtcNow());

        // The requester join exists only to make "Requester" a searchable field (the queue never
        // displayed it before). RequesterId is a required FK, so this INNER JOIN can never exclude
        // an otherwise-visible ticket — result set and count are unaffected when search is empty.
        var joined =
            from ticket in scoped
            join team in _dbContext.Teams on ticket.TeamId equals team.Id
            join category in _dbContext.Categories on ticket.CategoryId equals category.Id
            join requester in _dbContext.Users on ticket.RequesterId equals requester.Id
            join assignee in _dbContext.Users on ticket.AssigneeId equals assignee.Id into assignees
            from assignee in assignees.DefaultIfEmpty()
            select new { ticket, team, category, requester, assignee };

        if (normalizedSearch is not null)
        {
            var pattern = SearchTermNormalizer.ToLikePattern(normalizedSearch);
            joined = joined.Where(x =>
                EF.Functions.ILike(x.ticket.Reference!, pattern, SearchTermNormalizer.LikeEscapeCharacter)
                || EF.Functions.ILike(x.ticket.Title, pattern, SearchTermNormalizer.LikeEscapeCharacter)
                || EF.Functions.ILike(x.ticket.Description, pattern, SearchTermNormalizer.LikeEscapeCharacter)
                || EF.Functions.ILike(x.requester.DisplayName, pattern, SearchTermNormalizer.LikeEscapeCharacter)
                || (x.assignee != null && EF.Functions.ILike(x.assignee.DisplayName, pattern, SearchTermNormalizer.LikeEscapeCharacter))
                || EF.Functions.ILike(x.team.Name, pattern, SearchTermNormalizer.LikeEscapeCharacter)
                || EF.Functions.ILike(x.category.Name, pattern, SearchTermNormalizer.LikeEscapeCharacter));
        }

        var totalCount = await joined.CountAsync(cancellationToken);

        // Deterministic ordering (CLAUDE.md §7.2): CreatedAt DESC with Id DESC as the tiebreak,
        // so two tickets created in the same instant can never swap places between pages.
        // Deliberately not an urgency ordering — ranking at-risk work is AttentionPolicy's job in
        // Phase 8, and pre-empting it here would create a second ordering rule to keep in sync.
        var rows = await joined
            .OrderByDescending(x => x.ticket.CreatedAt).ThenByDescending(x => x.ticket.Id)
            .Skip((pageNumber - 1) * PageSize)
            .Take(PageSize)
            .Select(x => new
            {
                x.ticket.Id,
                Reference = x.ticket.Reference!,
                x.ticket.Title,
                x.ticket.WorkType,
                x.ticket.Priority,
                x.ticket.Status,
                TeamName = x.team.Name,
                CategoryName = x.category.Name,
                AssigneeDisplayName = x.assignee == null ? null : x.assignee.DisplayName,
                x.ticket.CreatedAt,
                Sla = new SlaFacts(
                    x.ticket.WorkType,
                    x.ticket.Priority,
                    x.ticket.Status,
                    x.ticket.SlaMet,
                    x.ticket.SlaStartedAt,
                    x.ticket.SlaDueAt,
                    x.ticket.SlaPausedMinutes,
                    x.ticket.SlaTargetMinutes),
            })
            .ToListAsync(cancellationToken);

        // One configuration read for the whole page, not one per row.
        var configurations = await LoadSlaConfigurationsAsync(cancellationToken);
        var now = _timeProvider.GetUtcNow();

        var items = rows
            .Select(r => new TicketListItem(
                r.Id,
                r.Reference,
                r.Title,
                r.WorkType,
                r.Priority,
                r.Status,
                r.TeamName,
                r.CategoryName,
                r.AssigneeDisplayName,
                r.CreatedAt,
                BuildSlaView(r.Sla, configurations, now)))
            .ToList();

        return new PagedResult<TicketListItem>(items, pageNumber, PageSize, totalCount);
    }

    /// <summary>
    /// One ticket, or <c>null</c> when it does not exist <em>or</em> the caller may not view it.
    /// The two cases are deliberately indistinguishable to the caller: the authorization scope is
    /// part of the query, so a ticket outside the caller's teams is never loaded and Web has no
    /// way to answer "does this ticket exist?" differently from "may I see it?".
    /// </summary>
    public async Task<TicketDetail?> GetDetailAsync(
        int ticketId,
        CurrentUser user,
        CancellationToken cancellationToken = default)
    {
        var scoped = ApplyViewScope(_dbContext, _dbContext.Tickets.AsNoTracking(), user)
            .Where(t => t.Id == ticketId);

        var row = await (
            from ticket in scoped
            join team in _dbContext.Teams on ticket.TeamId equals team.Id
            join category in _dbContext.Categories on ticket.CategoryId equals category.Id
            join requester in _dbContext.Users on ticket.RequesterId equals requester.Id
            join project in _dbContext.Projects on ticket.ProjectId equals project.Id into projects
            from project in projects.DefaultIfEmpty()
            join assignee in _dbContext.Users on ticket.AssigneeId equals assignee.Id into assignees
            from assignee in assignees.DefaultIfEmpty()
            select new
            {
                ticket.Id,
                Reference = ticket.Reference!,
                ticket.Title,
                ticket.Description,
                ticket.WorkType,
                ticket.Priority,
                ticket.Status,
                ticket.TeamId,
                TeamName = team.Name,
                CategoryName = category.Name,
                ProjectName = project == null ? null : project.Name,
                ticket.RequesterId,
                RequesterDisplayName = requester.DisplayName,
                ticket.AssigneeId,
                AssigneeDisplayName = assignee == null ? null : assignee.DisplayName,
                ticket.CreatedAt,
                ticket.UpdatedAt,
                ticket.DueDate,
                Sla = new SlaFacts(
                    ticket.WorkType,
                    ticket.Priority,
                    ticket.Status,
                    ticket.SlaMet,
                    ticket.SlaStartedAt,
                    ticket.SlaDueAt,
                    ticket.SlaPausedMinutes,
                    ticket.SlaTargetMinutes),
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        var configurations = await LoadSlaConfigurationsAsync(cancellationToken);

        return new TicketDetail(
            row.Id,
            row.Reference,
            row.Title,
            row.Description,
            row.WorkType,
            row.Priority,
            row.Status,
            row.TeamId,
            row.TeamName,
            row.CategoryName,
            row.ProjectName,
            row.RequesterId,
            row.RequesterDisplayName,
            row.AssigneeId,
            row.AssigneeDisplayName,
            row.CreatedAt,
            row.UpdatedAt,
            row.DueDate,
            BuildSlaView(row.Sla, configurations, _timeProvider.GetUtcNow()));
    }

    /// <summary>
    /// The ticket's merged activity timeline (AUDIT-RULE-06): every <c>TicketEvent</c> plus every
    /// <c>TicketComment</c> the caller may see, newest first. Returns an empty list for a ticket
    /// the caller may not view — the same visibility scope guards the timeline as guards the
    /// ticket itself, so it can never disclose a ticket the caller cannot already see.
    /// </summary>
    /// <remarks>
    /// Events and comments are two separate, narrow, indexed queries — never a SQL <c>UNION</c> of
    /// two differently-shaped rows — merged only in memory into <see cref="TicketTimelineEntry"/>,
    /// exactly as AUDIT-RULE-06 requires ("stored and queried separately... the UI only merges for
    /// display"). Internal comments are excluded from a comment query the caller cannot see
    /// (<see cref="TicketAccessPolicy.CanSeeInternalComments"/>) at the query itself, not by
    /// filtering an already-fetched row afterward — a Viewer's SQL never touches an internal
    /// comment's text.
    ///
    /// Deliberately unpaginated, unlike every list elsewhere in FlowOps (CLAUDE.md §7.2): this is
    /// one ticket's own activity, not a table scan, and Phase 6 already established the same
    /// exception for ticket-scoped history before comments existed. At the documented demo scale
    /// (~600 tickets / ~1500 comments, plus a handful of events per lifecycle) a single ticket's
    /// timeline stays small; revisit only if a real ticket's history is ever observed to grow
    /// large enough to matter.
    /// </remarks>
    public async Task<IReadOnlyList<TicketTimelineEntry>> GetHistoryAsync(
        int ticketId,
        CurrentUser user,
        CancellationToken cancellationToken = default)
    {
        var scoped = ApplyViewScope(_dbContext, _dbContext.Tickets.AsNoTracking(), user)
            .Where(t => t.Id == ticketId);

        var events = await (
            from ticket in scoped
            join auditEvent in _dbContext.TicketEvents on ticket.Id equals auditEvent.TicketId
            join actor in _dbContext.Users on auditEvent.ActorUserId equals actor.Id
            select new TicketTimelineEntry(
                TicketTimelineEntryKind.Event,
                auditEvent.Id,
                auditEvent.OccurredAt,
                actor.DisplayName,
                auditEvent.EventType,
                auditEvent.Field,
                auditEvent.OldValue,
                auditEvent.NewValue,
                auditEvent.Note,
                Body: null,
                IsInternal: null))
            .ToListAsync(cancellationToken);

        // TICKET-ENT-05 / AUTH-RULE-04: a Viewer's query for this ticket's comments never
        // includes an internal one — the exclusion is a WHERE clause, not a post-fetch filter.
        var canSeeInternal = TicketAccessPolicy.CanSeeInternalComments(user);
        var visibleComments = _dbContext.TicketComments.Where(c => canSeeInternal || !c.IsInternal);

        var comments = await (
            from ticket in scoped
            join comment in visibleComments on ticket.Id equals comment.TicketId
            join author in _dbContext.Users on comment.AuthorId equals author.Id
            select new TicketTimelineEntry(
                TicketTimelineEntryKind.Comment,
                comment.Id,
                comment.CreatedAt,
                author.DisplayName,
                EventType: null,
                Field: null,
                OldValue: null,
                NewValue: null,
                Note: null,
                comment.Body,
                comment.IsInternal))
            .ToListAsync(cancellationToken);

        // Timestamp DESC, then Kind, then Id DESC (decision 3) — Kind is the tiebreak an event id
        // and a comment id need because the two sequences are not comparable; it carries no
        // business meaning, only a fixed, deterministic presentation order.
        return events
            .Concat(comments)
            .OrderByDescending(e => e.OccurredAt)
            .ThenBy(e => e.Kind)
            .ThenByDescending(e => e.Id)
            .ToList();
    }

    /// <summary>
    /// AUTH-RULE-05: the caller's visibility scope as a <c>WHERE</c> clause, applied before any
    /// row is materialised — never a post-fetch filter.
    /// </summary>
    /// <remarks>
    /// This predicate is the SQL expression of <see cref="TicketAccessPolicy.CanView"/>
    /// (<c>Admin, or a member of the ticket's team</c>). It is the one place ticket authorization
    /// is restated, because a static policy method cannot itself be translated into SQL by EF
    /// Core. It is kept honest by tests that assert the rows this scope returns are exactly the
    /// rows <c>CanView</c> accepts — the same pattern ATTN-RULE-06 uses to make its prefilter
    /// duplication safe. If <c>CanView</c> changes, this method and those tests change with it.
    /// </remarks>
    private static IQueryable<Ticket> ApplyViewScope(FlowOpsDbContext dbContext, IQueryable<Ticket> tickets, CurrentUser user)
    {
        // Phase 16: the organization boundary is applied first, unconditionally — including for
        // Admin, who otherwise bypasses every team check below and would see every organization's
        // tickets. This is the query-side twin of TicketService.MutateAsync's org-scoped load.
        var orgScoped = tickets.Where(t =>
            dbContext.Teams.Any(team => team.Id == t.TeamId && team.OrganizationId == user.OrganizationId));

        if (user.Role == UserRole.Admin)
        {
            return orgScoped;
        }

        // Materialised to an array so the provider translates it to a single SQL array
        // membership test rather than an unpredictable set-type translation.
        var memberTeamIds = user.MemberTeamIds.ToArray();
        return orgScoped.Where(t => memberTeamIds.Contains(t.TeamId));
    }

    /// <summary>
    /// Phase 11: the Team/Category options a ticket-creation form offers, so a caller no longer
    /// has to type a raw database id (C-3). Admin sees every team; everyone else sees the teams
    /// they belong to — a UI convenience only, never a new authorization boundary.
    /// <see cref="TicketAccessPolicy.CanCreate"/> and TICKET-INV-02 remain the actual gates,
    /// unchanged, inside <see cref="TicketService.CreateAsync"/>: a caller who somehow submits a
    /// team/category pair outside this list is still fully re-checked there, exactly as before
    /// this method existed.
    /// </summary>
    /// <remarks>
    /// Two queries, bounded by the small size of reference data (a handful of teams and
    /// categories, per CLAUDE.md §16's target scale) — categories for every eligible team are
    /// fetched in one query, never one query per team.
    /// </remarks>
    public async Task<TicketCreationOptions> GetCreationOptionsAsync(CurrentUser user, CancellationToken cancellationToken = default)
    {
        // Phase 22 (ADR-0022): a deactivated team offers no options for a *new* ticket — its
        // categories are never even queried, so nothing about the team or its categories needs to
        // be touched when it is deactivated.
        var teamsQuery = _dbContext.Teams.AsNoTracking().Where(t => t.OrganizationId == user.OrganizationId && t.IsActive);
        if (user.Role != UserRole.Admin)
        {
            var memberTeamIds = user.MemberTeamIds.ToArray();
            teamsQuery = teamsQuery.Where(t => memberTeamIds.Contains(t.Id));
        }

        var teams = await teamsQuery
            .OrderBy(t => t.Name)
            .Select(t => new { t.Id, t.Name })
            .ToListAsync(cancellationToken);

        var teamIds = teams.Select(t => t.Id).ToArray();

        var categories = await _dbContext.Categories
            .AsNoTracking()
            .Where(c => teamIds.Contains(c.TeamId) && c.IsActive)
            .OrderBy(c => c.Name)
            .Select(c => new { c.Id, c.Name, c.TeamId })
            .ToListAsync(cancellationToken);

        var teamOptions = teams
            .Select(t => new TeamOption(
                t.Id,
                t.Name,
                categories.Where(c => c.TeamId == t.Id).Select(c => new CategoryOption(c.Id, c.Name)).ToList()))
            .Where(t => t.Categories.Count > 0) // a team with no categories offers nothing to select
            .ToList();

        // Projects have no team relation (unlike Category) — every ACTIVE project in the caller's
        // own organization is offered, the same organization boundary CreateAsync's own ProjectId
        // check already enforces (Phase 16), plus the same active-only rule that check now also
        // enforces (project management phase) — a deactivated project cannot be selected for a new
        // ticket even though it remains fully visible on tickets that already reference it.
        var projectOptions = await _dbContext.Projects
            .AsNoTracking()
            .Where(p => p.OrganizationId == user.OrganizationId && p.IsActive)
            .OrderBy(p => p.Name)
            .Select(p => new ProjectOption(p.Id, p.Name))
            .ToListAsync(cancellationToken);

        return new TicketCreationOptions(teamOptions, projectOptions);
    }

    /// <summary>
    /// The active members of <paramref name="teamId"/> a caller who can assign to someone other
    /// than themselves (Admin, or a Manager of this specific team — the same two branches
    /// <see cref="TicketAccessPolicy.CanAssign"/> already grants regardless of target) may choose
    /// from. Deliberately not gated by the Admin-only <c>DirectoryAccessPolicy.CanManageTeams</c>
    /// (<see cref="FlowOps.Application.Directory.TeamService.GetTeamDetailAsync"/>'s own gate) — a Manager must reach
    /// this too, and merely seeing a team's member names grants no team-management authority.
    /// </summary>
    public async Task<IReadOnlyList<AssignableMember>> GetAssignableTeamMembersAsync(CurrentUser actor, int teamId, CancellationToken cancellationToken = default)
    {
        if (actor.Role != UserRole.Admin && !actor.ManagedTeamIds.Contains(teamId))
        {
            throw new TicketAccessDeniedException("This role may not assign tickets on this team.");
        }

        var teamInOrganization = await _dbContext.Teams
            .AsNoTracking()
            .AnyAsync(t => t.Id == teamId && t.OrganizationId == actor.OrganizationId, cancellationToken);
        if (!teamInOrganization)
        {
            throw new TicketAccessDeniedException("This team is not available to you.");
        }

        return await _dbContext.TeamMembers
            .AsNoTracking()
            .Where(m => m.TeamId == teamId)
            .Join(_dbContext.Users.Where(u => u.IsActive), m => m.UserId, u => u.Id, (_, u) => u)
            .OrderBy(u => u.DisplayName)
            .Select(u => new AssignableMember(u.Id, u.DisplayName))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Phase 10: the closed <see cref="TicketQueueFilter"/> set, translated to SQL. Each branch is
    /// the exact predicate its dashboard KPI counts (see <see cref="AnalyticsQueryService"/>) —
    /// applied after <see cref="ApplyViewScope"/>, so a filtered queue can never show a ticket
    /// outside the caller's ordinary view scope.
    /// </summary>
    private static IQueryable<Ticket> ApplyQueueFilter(IQueryable<Ticket> tickets, TicketQueueFilter filter, DateTimeOffset now) =>
        filter switch
        {
            TicketQueueFilter.None => tickets,
            TicketQueueFilter.OpenWork => tickets.Where(t => t.Status != Status.Resolved && t.Status != Status.Closed),
            // AttentionSignalCode.Overdue's exact definition — DueDate, never SlaDueAt/SLA breach.
            TicketQueueFilter.Overdue => tickets.Where(t =>
                t.DueDate != null && t.DueDate < now && t.Status != Status.Resolved && t.Status != Status.Closed),
            TicketQueueFilter.ResolvedRecently => tickets.Where(t =>
                t.ResolvedAt != null && t.ResolvedAt >= now.AddDays(-AnalyticsQueryService.ReportingWindowDays)),
            _ => throw new ArgumentOutOfRangeException(nameof(filter)),
        };
}
