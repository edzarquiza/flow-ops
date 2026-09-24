using FlowOps.Domain.Attention;
using FlowOps.Domain.Sla;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FlowOps.Application.Tickets;

/// <summary>
/// The at-risk read path (ATTN-RULE-05): an indexed SQL prefilter narrows the table to candidates,
/// then <see cref="AttentionPolicy"/> — the single authority on what needs attention — evaluates
/// and ranks them in memory. This service detects nothing and ranks nothing itself; it loads facts,
/// supplies one clock reading, and maps the policy's output to DTOs.
/// </summary>
public sealed class AttentionQueryService
{
    /// <summary>Same page size as the work queue (CLAUDE.md §7.2).</summary>
    public const int PageSize = 25;

    private readonly FlowOpsDbContext _dbContext;
    private readonly TimeProvider _timeProvider;
    private readonly AttentionOptions _options;
    private readonly TicketQueryService _ticketQueryService;

    public AttentionQueryService(FlowOpsDbContext dbContext, TimeProvider timeProvider, AttentionOptions options, TicketQueryService ticketQueryService)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
        _options = options;
        _ticketQueryService = ticketQueryService;
    }

    /// <summary>
    /// One page of the caller's at-risk work, ranked most urgent first. Tickets with no signals are
    /// absent by construction — <see cref="AttentionPolicy.Rank"/> drops them.
    /// </summary>
    /// <remarks>
    /// Ranking is global and happens before paging: ATTN-RULE-04 orders by severity across the whole
    /// candidate set, so page 1 must be the most urgent tickets overall, not the most urgent of an
    /// arbitrary database page. That means candidates are materialised before paging — a deliberate
    /// departure from the SQL-side paging the work queue uses, accepted at CLAUDE.md §16's scale and
    /// recorded in docs/database.md.
    /// </remarks>
    /// <param name="search">
    /// An optional free-text term matched against reference, title, description, requester,
    /// assignee, team, and category. Applied strictly after <see cref="AttentionPolicy.Rank"/> has
    /// already decided both eligibility (which candidates truly have signals) and order — search
    /// only narrows that already-ranked, already-eligible list, so it can never admit a ticket
    /// AttentionPolicy would not have flagged, nor change the relative order of what remains.
    /// <see langword="null"/>/whitespace is treated as no search.
    /// </param>
    /// <param name="dueDateRange">
    /// Phase 25 §16: At-Risk is an intelligence view, not a ticket-history report — its date
    /// filter is deliberately narrower than the Work Queue's and offers no field choice. It
    /// narrows by <c>SlaDueAt</c>, the one date already central to this page's own primary signal
    /// (SLA breach/at-risk), applied in the same position as <paramref name="search"/>: strictly
    /// after <see cref="AttentionPolicy.Rank"/>, so it can only narrow an already-eligible,
    /// already-ranked list, never change which tickets qualify as at-risk or their relative order.
    /// A candidate flagged solely by Aging/Stalled/Churn/Reopened with an <c>SlaDueAt</c> outside
    /// the selected range is intentionally excluded by this filter — the same tradeoff a
    /// "due-date-focused" filter makes on any list mixing due-dated and non-due-dated items; it is
    /// not a defect in <see cref="AttentionPolicy"/>, which is untouched by this parameter.
    /// </param>
    public async Task<PagedResult<AttentionListItem>> GetAtRiskAsync(
        CurrentUser user,
        int pageNumber,
        string? search = null,
        DateRangeFilter? dueDateRange = null,
        CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1)
        {
            pageNumber = 1;
        }

        var normalizedSearch = SearchTermNormalizer.Normalize(search);

        // One clock reading for the whole request: the prefilter's SQL parameters and the policy's
        // evaluation must agree, or a ticket could pass the filter and then fail the policy purely
        // because time moved between the two.
        var now = _timeProvider.GetUtcNow();

        // Four reference rows, read once — never per ticket.
        var slaConfigurations = await _dbContext.SlaConfigurations
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var candidates = await BuildCandidateQuery(user, now, slaConfigurations)
            // Option A (approved): the Stalled/InProgress branch reads ticket.Events, so the
            // history is loaded explicitly with the candidates. One JOIN in the same query — not
            // a per-ticket lookup — served by ix_ticket_events_ticket.
            .Include(t => t.Events)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var evaluated = candidates
            .Select(ticket => new TicketAttentionResult(
                ticket,
                AttentionPolicy.Evaluate(
                    ticket,
                    now,
                    _options,
                    SlaPolicy.ResolveConfiguration(slaConfigurations, ticket.WorkType, ticket.Priority).RiskThresholdPercent)))
            .ToList();

        // Rank drops signal-less candidates, so this is both the ordering and the final filter —
        // AttentionPolicy's decision, untouched by search.
        var ranked = AttentionPolicy.Rank(evaluated);

        var searched = normalizedSearch is null
            ? ranked
            : await FilterBySearchAsync(ranked, normalizedSearch, cancellationToken);

        var dateFiltered = dueDateRange?.Resolve(now) is { } range
            ? searched.Where(r => r.Ticket.SlaDueAt >= range.Start && r.Ticket.SlaDueAt < range.End).ToList()
            : searched;

        var page = dateFiltered
            .Skip((pageNumber - 1) * PageSize)
            .Take(PageSize)
            .ToList();

        var items = await MapAsync(page, slaConfigurations, now, cancellationToken);

        return new PagedResult<AttentionListItem>(items, pageNumber, PageSize, dateFiltered.Count);
    }

    /// <summary>
    /// Phase 30: the Attention Brief — explains one ticket's existing attention decision, never a
    /// second one. Calls the exact same <see cref="AttentionPolicy.Evaluate"/> this class's own
    /// <see cref="GetAtRiskAsync"/> already calls, for exactly one ticket, through the same
    /// organization/team view-scope every other ticket read uses. Returns <see langword="null"/>
    /// when the ticket does not exist, the caller may not view it (the two are indistinguishable,
    /// per AUTH-RULE-04's non-disclosure pattern — this is the same query shape
    /// <see cref="TicketQueryService.GetDetailAsync"/> already uses), or the ticket genuinely has no
    /// signals right now — a ticket with nothing wrong gets no brief, not an empty one.
    /// </summary>
    public async Task<AttentionBrief?> GetBriefAsync(CurrentUser user, int ticketId, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        var slaConfigurations = await _dbContext.SlaConfigurations.AsNoTracking().ToListAsync(cancellationToken);

        var ticket = await ApplyViewScope(_dbContext, _dbContext.Tickets, user)
            .Where(t => t.Id == ticketId)
            .Include(t => t.Events)
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken);

        if (ticket is null)
        {
            return null;
        }

        var riskThresholdPercent = SlaPolicy.ResolveConfiguration(slaConfigurations, ticket.WorkType, ticket.Priority).RiskThresholdPercent;
        var signals = AttentionPolicy.Evaluate(ticket, now, _options, riskThresholdPercent);
        if (signals.Count == 0)
        {
            return null;
        }

        var signalViews = signals
            .Select(s => new AttentionSignalView(s.Code, s.Severity, s.Headline))
            .ToList();

        // "What changed" (Phase 30 Gate A): real, already-recorded events only — never comments,
        // never a reconstructed historical attention timeline (FlowOps persists no such thing).
        // Reuses GetHistoryAsync's own authorization/visibility/display-name-resolution rather than
        // a second, parallel event query.
        var history = await _ticketQueryService.GetHistoryAsync(ticketId, user, cancellationToken);
        var recentEvents = history
            .Where(e => e.Kind == TicketTimelineEntryKind.Event && e.EventType != TicketEventType.CommentAdded)
            .OrderByDescending(e => e.OccurredAt)
            .Take(2)
            .ToList();

        var suggestedNextStep = AttentionSuggestion.SuggestNextStep(signals, ticket.Status);

        return new AttentionBrief(signalViews, recentEvents, suggestedNextStep);
    }

    /// <summary>
    /// ADR-0036: Team Workload's own at-risk counts — total plus a per-team breakdown, evaluated
    /// over the same candidate-then-evaluate shape <see cref="GetAtRiskAsync"/> already uses,
    /// narrowed to the caller's Team Analytics scope (<see cref="TicketAccessPolicy.GetAnalyticsScope"/>)
    /// rather than <see cref="BuildCandidateQuery"/>'s own broader view-scope — Team Workload must
    /// never disclose more than the analytics authority every other analytics view already grants
    /// (a Manager's count here is "managed teams," not every team they merely belong to). Grouped
    /// in memory, never in SQL: "is this ticket at risk" is <see cref="AttentionPolicy"/>'s own
    /// per-ticket decision, not a column — this method decides nothing itself.
    /// </summary>
    public async Task<(int Total, IReadOnlyDictionary<int, int> ByTeamId)> GetAtRiskSummaryAsync(
        CurrentUser user,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var slaConfigurations = await _dbContext.SlaConfigurations.AsNoTracking().ToListAsync(cancellationToken);

        var candidates = await ApplyAnalyticsScopeNarrowing(BuildCandidateQuery(user, now, slaConfigurations), user)
            .Include(t => t.Events)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var atRisk = candidates
            .Where(t => AttentionPolicy.Evaluate(
                    t, now, _options, SlaPolicy.ResolveConfiguration(slaConfigurations, t.WorkType, t.Priority).RiskThresholdPercent)
                .Count > 0)
            .ToList();

        var byTeam = atRisk.GroupBy(t => t.TeamId).ToDictionary(g => g.Key, g => g.Count());

        return (atRisk.Count, byTeam);
    }

    /// <summary>
    /// ADR-0036: one team's per-member at-risk counts, computed only when that team is explicitly
    /// expanded — never eagerly for every team. Same authorization as
    /// <see cref="AnalyticsQueryService.GetTeamMemberWorkloadAsync"/>
    /// (<see cref="TicketAccessPolicy.CanViewTeamWorkload"/>); already having confirmed the caller
    /// may see this specific team, filtering <see cref="BuildCandidateQuery"/>'s results down to it
    /// needs no further scope narrowing beyond that check. Same in-memory grouping reasoning as
    /// <see cref="GetAtRiskSummaryAsync"/>.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, int>> GetAtRiskCountsByAssigneeAsync(
        CurrentUser user,
        int teamId,
        CancellationToken cancellationToken = default)
    {
        if (!TicketAccessPolicy.CanViewTeamWorkload(user, teamId))
        {
            throw new TicketAccessDeniedException("This team's workload is not available to you.");
        }

        var now = _timeProvider.GetUtcNow();
        var slaConfigurations = await _dbContext.SlaConfigurations.AsNoTracking().ToListAsync(cancellationToken);

        var candidates = await BuildCandidateQuery(user, now, slaConfigurations)
            .Where(t => t.TeamId == teamId && t.AssigneeId != null)
            .Include(t => t.Events)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return candidates
            .Where(t => AttentionPolicy.Evaluate(
                    t, now, _options, SlaPolicy.ResolveConfiguration(slaConfigurations, t.WorkType, t.Priority).RiskThresholdPercent)
                .Count > 0)
            .GroupBy(t => t.AssigneeId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>
    /// ADR-0036: narrows an already view-scoped candidate query down to the caller's Team Analytics
    /// scope specifically — the same translation <see cref="AnalyticsQueryService"/>'s own private
    /// <c>ApplyAnalyticsScope</c> performs, kept as its own small copy here for the same reason
    /// <see cref="ApplyViewScope"/> is already a deliberate duplication of
    /// <see cref="TicketQueryService"/>'s copy (this class's own doc comment). A strict narrowing
    /// only — it can never admit a ticket <see cref="BuildCandidateQuery"/> did not already include.
    /// </summary>
    private static IQueryable<Ticket> ApplyAnalyticsScopeNarrowing(IQueryable<Ticket> tickets, CurrentUser user)
    {
        var scope = TicketAccessPolicy.GetAnalyticsScope(user);
        var teamIds = scope.TeamIds.ToArray();

        return scope.Kind switch
        {
            AnalyticsScopeKind.AllTeams => tickets,
            AnalyticsScopeKind.ManagedTeams or AnalyticsScopeKind.MemberTeams => tickets.Where(t => teamIds.Contains(t.TeamId)),
            AnalyticsScopeKind.OwnAssignedTicketsOnly => tickets.Where(t => t.AssigneeId == user.UserId),
            _ => tickets.Where(_ => false),
        };
    }

    /// <summary>
    /// Narrows an already-ranked, already-eligible result set to rows matching <paramref name="search"/>
    /// — in-memory (the candidate set is already fully materialised by this point, per
    /// <see cref="GetAtRiskAsync"/>'s own doc comment on ATTN-RULE-06's small-scale assumption), so
    /// this needs no SQL of its own beyond the two small id→name lookups team/category/requester
    /// display names require (assignee names are fetched again, identically, by
    /// <see cref="MapAsync"/> — an accepted, bounded-by-page-size second lookup, not a per-row one).
    /// <see cref="Ticket.Reference"/>/<see cref="Ticket.Title"/>/<see cref="Ticket.Description"/>
    /// are already loaded on every candidate and need no lookup at all.
    /// </summary>
    private async Task<IReadOnlyList<TicketAttentionResult>> FilterBySearchAsync(
        IReadOnlyList<TicketAttentionResult> ranked,
        string search,
        CancellationToken cancellationToken)
    {
        if (ranked.Count == 0)
        {
            return ranked;
        }

        var teamIds = ranked.Select(r => r.Ticket.TeamId).Distinct().ToArray();
        var categoryIds = ranked.Select(r => r.Ticket.CategoryId).Distinct().ToArray();
        var userIds = ranked
            .Select(r => r.Ticket.RequesterId)
            .Concat(ranked.Where(r => r.Ticket.AssigneeId.HasValue).Select(r => r.Ticket.AssigneeId!.Value))
            .Distinct()
            .ToArray();

        var teamNames = await _dbContext.Teams
            .AsNoTracking()
            .Where(t => teamIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);

        var categoryNames = await _dbContext.Categories
            .AsNoTracking()
            .Where(c => categoryIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);

        var userNames = await _dbContext.Users
            .AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken);

        return ranked
            .Where(r =>
            {
                var ticket = r.Ticket;
                return Matches(ticket.Reference, search)
                    || Matches(ticket.Title, search)
                    || Matches(ticket.Description, search)
                    || Matches(userNames.GetValueOrDefault(ticket.RequesterId), search)
                    || (ticket.AssigneeId is { } assigneeId && Matches(userNames.GetValueOrDefault(assigneeId), search))
                    || Matches(teamNames.GetValueOrDefault(ticket.TeamId), search)
                    || Matches(categoryNames.GetValueOrDefault(ticket.CategoryId), search);
            })
            .ToList();
    }

    private static bool Matches(string? value, string search) =>
        value is not null && value.Contains(search, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The candidate prefilter (ATTN-RULE-06). Every branch below is at least as wide as the signal
    /// it stands in for, so a ticket <see cref="AttentionPolicy"/> would flag can never be filtered
    /// out here. Two branches are deliberately wider than their signal — the at-risk branch uses the
    /// smallest configured risk threshold, and the aging branch the shortest configured window —
    /// because both real thresholds vary per row and per priority. Over-inclusion is free: the
    /// policy discards anything that does not truly qualify.
    /// </summary>
    /// <remarks>
    /// This is the one place ATTN-RULE-06 permits logic to be restated outside the policy, and the
    /// superset property is therefore guarded by an explicit test that seeds every signal and every
    /// near-miss. No threshold is hardcoded here: each is derived from persisted SLA configuration
    /// or from <see cref="AttentionOptions"/>, so widening a configured threshold widens the filter
    /// with it.
    /// </remarks>
    internal IQueryable<Ticket> BuildCandidateQuery(
        CurrentUser user,
        DateTimeOffset now,
        IReadOnlyList<SlaConfiguration> slaConfigurations)
    {
        // The lowest threshold any configuration row can impose. The real threshold for a given
        // ticket is >= this, so its real at-risk moment is never earlier than this branch admits.
        var minRiskThresholdPercent = slaConfigurations.Count == 0
            ? 0
            : slaConfigurations.Min(c => c.RiskThresholdPercent);
        var minRiskFraction = minRiskThresholdPercent / 100d;

        // The shortest aging window across all priorities, for the same reason.
        var minAgingDays = _options.AgingThresholdDays.Count == 0
            ? 0
            : _options.AgingThresholdDays.Values.Min();

        var unassignedUrgentCutoff = now.AddMinutes(-_options.UnassignedUrgentMinutes);
        var agingCutoff = now.AddDays(-minAgingDays);
        var stalledPendingCutoff = now.AddDays(-_options.StalledPendingDays);
        var stalledInProgressCutoff = now.AddDays(-_options.StalledInProgressDays);
        var churnThreshold = _options.ChurnAssignmentChangeThreshold;

        return ApplyViewScope(_dbContext, _dbContext.Tickets, user)
            // Terminal tickets never carry a signal, so they never become candidates.
            .Where(t => t.Status != Status.Resolved && t.Status != Status.Closed)
            .Where(t =>
                // SlaBreached — exact.
                t.SlaDueAt <= now
                // SlaAtRisk — widened by the smallest configured risk threshold.
                || t.SlaStartedAt.AddMinutes((t.SlaTargetMinutes * minRiskFraction) + t.SlaPausedMinutes) <= now
                // Overdue — exact.
                || (t.DueDate != null && t.DueDate < now)
                // UnassignedUrgent — exact.
                || (t.AssigneeId == null
                    && (t.Priority == Priority.Critical || t.Priority == Priority.High)
                    && t.CreatedAt < unassignedUrgentCutoff)
                // Aging — widened by the shortest configured aging window.
                || t.CreatedAt < agingCutoff
                // Stalled, Pending branch — exact.
                || (t.Status == Status.Pending && t.PendingSince != null && t.PendingSince < stalledPendingCutoff)
                // Stalled, InProgress branch — exact, via UpdatedAt, which Ticket.AppendEvent keeps
                // equal to the latest event's OccurredAt (the value the policy computes from Events).
                || (t.Status == Status.InProgress && t.UpdatedAt < stalledInProgressCutoff)
                // Churn — exact.
                || t.AssignmentChangeCount >= churnThreshold
                // Reopened — exact.
                || t.ReopenCount >= 1);
    }

    /// <summary>
    /// Maps ranked domain results to DTOs, resolving the team and assignee names the page needs in
    /// one lookup per page rather than one per ticket.
    /// </summary>
    private async Task<IReadOnlyList<AttentionListItem>> MapAsync(
        IReadOnlyList<TicketAttentionResult> ranked,
        IReadOnlyList<SlaConfiguration> slaConfigurations,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (ranked.Count == 0)
        {
            return [];
        }

        var teamIds = ranked.Select(r => r.Ticket.TeamId).Distinct().ToArray();
        var assigneeIds = ranked
            .Select(r => r.Ticket.AssigneeId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();

        var teamNames = await _dbContext.Teams
            .AsNoTracking()
            .Where(t => teamIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);

        var assigneeNames = assigneeIds.Length == 0
            ? []
            : await _dbContext.Users
                .AsNoTracking()
                .Where(u => assigneeIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken);

        return ranked
            .Select(result =>
            {
                var ticket = result.Ticket;
                var configuration = SlaPolicy.ResolveConfiguration(slaConfigurations, ticket.WorkType, ticket.Priority);

                var slaStatus = SlaPolicy.GetStatus(
                    ticket.Status,
                    ticket.SlaMet,
                    now,
                    ticket.SlaStartedAt,
                    ticket.SlaDueAt,
                    ticket.SlaPausedMinutes,
                    ticket.SlaTargetMinutes,
                    configuration.RiskThresholdPercent);

                return new AttentionListItem(
                    ticket.Id,
                    ticket.Reference!,
                    ticket.Title,
                    ticket.Priority,
                    ticket.Status,
                    teamNames.GetValueOrDefault(ticket.TeamId, string.Empty),
                    ticket.AssigneeId is { } assigneeId ? assigneeNames.GetValueOrDefault(assigneeId) : null,
                    new TicketSlaView(
                        slaStatus,
                        ticket.SlaDueAt,
                        ticket.SlaTargetMinutes,
                        ticket.SlaPausedMinutes,
                        ticket.SlaDueAt - now),
                    result.Signals
                        .Select(s => new AttentionSignalView(s.Code, s.Severity, s.Headline))
                        .ToList());
            })
            .ToList();
    }

    /// <summary>
    /// AUTH-RULE-05, identical to the work queue's scope: attention information can only ever be
    /// produced from tickets <see cref="TicketAccessPolicy.CanView"/> would admit, because tickets
    /// outside the caller's scope are never fetched in the first place.
    /// </summary>
    private static IQueryable<Ticket> ApplyViewScope(FlowOpsDbContext dbContext, IQueryable<Ticket> tickets, CurrentUser user)
    {
        // Phase 16: see TicketQueryService.ApplyViewScope — the same org-first, then-role-scoping
        // shape, kept in this second copy per the class doc's own note that this duplication is
        // deliberate (ATTN-RULE-06's superset-of-CanView requirement is tested independently here).
        var orgScoped = tickets.Where(t =>
            dbContext.Teams.Any(team => team.Id == t.TeamId && team.OrganizationId == user.OrganizationId));

        if (user.Role == UserRole.Admin)
        {
            return orgScoped;
        }

        var memberTeamIds = user.MemberTeamIds.ToArray();
        return orgScoped.Where(t => memberTeamIds.Contains(t.TeamId));
    }
}
