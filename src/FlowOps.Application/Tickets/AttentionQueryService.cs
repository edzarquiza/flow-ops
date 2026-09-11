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

    public AttentionQueryService(FlowOpsDbContext dbContext, TimeProvider timeProvider, AttentionOptions options)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
        _options = options;
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
    public async Task<PagedResult<AttentionListItem>> GetAtRiskAsync(
        CurrentUser user,
        int pageNumber,
        CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1)
        {
            pageNumber = 1;
        }

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

        // Rank drops signal-less candidates, so this is both the ordering and the final filter.
        var ranked = AttentionPolicy.Rank(evaluated);

        var page = ranked
            .Skip((pageNumber - 1) * PageSize)
            .Take(PageSize)
            .ToList();

        var items = await MapAsync(page, slaConfigurations, now, cancellationToken);

        return new PagedResult<AttentionListItem>(items, pageNumber, PageSize, ranked.Count);
    }

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

        return ApplyViewScope(_dbContext.Tickets, user)
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
    private static IQueryable<Ticket> ApplyViewScope(IQueryable<Ticket> tickets, CurrentUser user)
    {
        if (user.Role == UserRole.Admin)
        {
            return tickets;
        }

        var memberTeamIds = user.MemberTeamIds.ToArray();
        return tickets.Where(t => memberTeamIds.Contains(t.TeamId));
    }
}
