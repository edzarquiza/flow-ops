using FlowOps.Domain;
using FlowOps.Domain.Planning;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowOps.Application.Planning;

/// <summary>
/// Sprint lifecycle writes (ADR-0029): create, start, complete. Ticket membership changes are NOT
/// here — they are ticket mutations and live on <c>TicketService</c> so they share its
/// org-scoped load, authorization, audit event, and concurrency handling. Sprint lifecycle itself
/// is recorded on the sprint row (<c>CreatedAt/ActivatedAt/CompletedAt</c>) and in structured logs;
/// it is deliberately not a <c>TicketEvent</c> (those are per-ticket business changes only).
/// </summary>
public sealed class SprintService
{
    private readonly FlowOpsDbContext _dbContext;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SprintService> _logger;

    public SprintService(FlowOpsDbContext dbContext, TimeProvider timeProvider, ILogger<SprintService>? logger = null)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
        _logger = logger ?? NullLogger<SprintService>.Instance;
    }

    /// <summary>Creates a Planned sprint. Validation failures (name, dates, overlap) come back as a
    /// <see cref="SprintMutationResult"/> message; authorization / cross-organization attempts throw
    /// <see cref="PlanningAccessDeniedException"/>.</summary>
    public async Task<SprintMutationResult> CreateSprintAsync(
        CurrentUser actor,
        int projectId,
        string name,
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        RequireCanManage(actor, "create sprints");

        var projectIsActive = await _dbContext.Projects
            .AsNoTracking()
            .Where(p => p.Id == projectId && p.OrganizationId == actor.OrganizationId)
            .Select(p => (bool?)p.IsActive)
            .SingleOrDefaultAsync(cancellationToken);

        if (projectIsActive is not true)
        {
            throw Denied(actor, "create a sprint", projectId);
        }

        Sprint sprint;
        try
        {
            sprint = Sprint.Create(projectId, name, startDate, endDate, _timeProvider.GetUtcNow());
        }
        catch (DomainRuleException ex)
        {
            return SprintMutationResult.Failed(ex.Message);
        }

        // SPRINT-INV-05: planned/active sprints of one project do not overlap. Completed/cancelled sprints are
        // history and never block planning the same dates again.
        var overlaps = await _dbContext.Sprints
            .AsNoTracking()
            .AnyAsync(s => s.ProjectId == projectId && (s.Status == SprintStatus.Planned || s.Status == SprintStatus.Active) && s.StartDate <= endDate && startDate <= s.EndDate, cancellationToken);
        if (overlaps)
        {
            return SprintMutationResult.Failed("These dates overlap another planned or active sprint in this project.");
        }

        _dbContext.Sprints.Add(sprint);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Sprint {SprintId} created in project {ProjectId} by {ActorUserId}.", sprint.Id, projectId, actor.UserId);
        return SprintMutationResult.Success(sprint.Id);
    }

    public Task<SprintMutationResult> StartSprintAsync(CurrentUser actor, int sprintId, CancellationToken cancellationToken = default) =>
        MutateAsync(actor, sprintId, "started", async sprint =>
        {
            var anotherActive = await _dbContext.Sprints
                .AsNoTracking()
                .AnyAsync(s => s.ProjectId == sprint.ProjectId && s.Status == SprintStatus.Active && s.Id != sprint.Id, cancellationToken);
            if (anotherActive)
            {
                return "This project already has an active sprint. Complete it before starting another.";
            }

            sprint.Activate(_timeProvider.GetUtcNow());
            return null;
        }, cancellationToken);

    /// <summary>Completes the active sprint. Membership is untouched — unfinished tickets stay
    /// attached to the completed sprint as history and can be moved to a planned sprint afterwards
    /// (SPRINT-INV-06); nothing is moved automatically.</summary>
    public Task<SprintMutationResult> CompleteSprintAsync(CurrentUser actor, int sprintId, CancellationToken cancellationToken = default) =>
        MutateAsync(actor, sprintId, "completed", async sprint =>
        {
            sprint.Complete(_timeProvider.GetUtcNow());

            // ADR-0030: freeze every member ticket's result in the same save, so "this ticket was
            // part of this sprint, and this is where it stood" survives a later carry-forward. All
            // members are captured (visibility is applied when the archive is read, never here).
            var members = await _dbContext.Tickets
                .AsNoTracking()
                .Where(t => t.SprintId == sprint.Id)
                .Select(t => new { t.Id, t.Status })
                .ToListAsync(cancellationToken);
            _dbContext.SprintTicketSnapshots.AddRange(members.Select(m => new SprintTicketSnapshot(sprint.Id, m.Id, m.Status)));
            return null;
        }, cancellationToken);

    /// <summary>
    /// ADR-0030: cancels a Planned sprint (never one that started). Its tickets were only ever
    /// <em>selected</em> for it — no work happened in it — so their membership is released (each an
    /// audited <c>SprintChanged</c> event) rather than stranded in a read-only sprint; a ticket that
    /// has since been finished keeps its record. The sprint row itself is kept as history.
    /// </summary>
    public Task<SprintMutationResult> CancelSprintAsync(CurrentUser actor, int sprintId, CancellationToken cancellationToken = default) =>
        MutateAsync(actor, sprintId, "cancelled", async sprint =>
        {
            var now = _timeProvider.GetUtcNow();
            sprint.Cancel(now);

            var members = await _dbContext.Tickets
                .Where(t => t.SprintId == sprint.Id && t.Status != Status.Resolved && t.Status != Status.Closed)
                .ToListAsync(cancellationToken);
            foreach (var ticket in members)
            {
                ticket.MoveToSprint(null, sprintAcceptsTickets: true, actor.UserId, now);
            }

            return null;
        }, cancellationToken);

    /// <summary>
    /// ADR-0030: the explicit "move unfinished tickets to the current sprint" action on a completed
    /// sprint. Never automatic. Only tickets that are still unfinished (not Resolved/Closed) and still
    /// in that completed sprint are eligible, and only those the caller may plan
    /// (<see cref="TicketAccessPolicy.CanPlan"/>) — the rest are counted as skipped. Each moved ticket
    /// goes through <c>Ticket.MoveToSprint</c> (status, assignee, SLA, and dates untouched; one audited
    /// event); the completed sprint's snapshot is not touched, so its history stays true. All-or-nothing.
    /// </summary>
    public async Task<SprintCarryForwardResult> CarryForwardAsync(CurrentUser actor, int completedSprintId, CancellationToken cancellationToken = default)
    {
        RequireCanManage(actor, "carry tickets forward");

        var completed = await _dbContext.Sprints
            .AsNoTracking()
            .Where(s => s.Id == completedSprintId && _dbContext.Projects.Any(p => p.Id == s.ProjectId && p.OrganizationId == actor.OrganizationId))
            .SingleOrDefaultAsync(cancellationToken);
        if (completed is null)
        {
            throw Denied(actor, "carry tickets forward from a sprint", completedSprintId);
        }

        if (completed.Status != SprintStatus.Completed)
        {
            return SprintCarryForwardResult.Failed("Only a completed sprint's unfinished tickets can be carried forward.");
        }

        var current = await _dbContext.Sprints
            .AsNoTracking()
            .Where(s => s.ProjectId == completed.ProjectId && s.Status == SprintStatus.Active)
            .SingleOrDefaultAsync(cancellationToken);
        if (current is null)
        {
            return SprintCarryForwardResult.Failed("This project has no current sprint. Start a sprint first.");
        }

        var candidates = await _dbContext.Tickets
            .Where(t => t.SprintId == completed.Id && t.Status != Status.Resolved && t.Status != Status.Closed
                && _dbContext.Teams.Any(team => team.Id == t.TeamId && team.OrganizationId == actor.OrganizationId))
            .ToListAsync(cancellationToken);

        var now = _timeProvider.GetUtcNow();
        int moved = 0, skipped = 0;
        foreach (var ticket in candidates)
        {
            var snapshot = new TicketAuthorizationSnapshot(ticket.Id, ticket.TeamId, ticket.RequesterId, ticket.AssigneeId, ticket.Status);
            if (!TicketAccessPolicy.CanView(snapshot, actor) || !TicketAccessPolicy.CanPlan(snapshot, actor))
            {
                skipped++;
                continue;
            }

            ticket.MoveToSprint(current.Id, sprintAcceptsTickets: true, actor.UserId, now);
            moved++;
        }

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return SprintCarryForwardResult.Failed("A ticket changed while you were working on it. Reload the page and try again.");
        }

        _logger.LogInformation("Carried {Moved} unfinished tickets from sprint {From} to sprint {To} by {ActorUserId} ({Skipped} skipped).", moved, completed.Id, current.Id, actor.UserId, skipped);
        return new SprintCarryForwardResult(true, moved, skipped, null);
    }

    private async Task<SprintMutationResult> MutateAsync(
        CurrentUser actor,
        int sprintId,
        string operation,
        Func<Sprint, Task<string?>> apply,
        CancellationToken cancellationToken)
    {
        RequireCanManage(actor, $"change sprint {operation}");

        // Organization scope in the load itself, so another organization's sprint is
        // indistinguishable from a missing one.
        var sprint = await _dbContext.Sprints
            .Where(s => s.Id == sprintId && _dbContext.Projects.Any(p => p.Id == s.ProjectId && p.OrganizationId == actor.OrganizationId))
            .SingleOrDefaultAsync(cancellationToken);

        if (sprint is null)
        {
            throw Denied(actor, $"change (\"{operation}\") sprint", sprintId);
        }

        try
        {
            var rejection = await apply(sprint);
            if (rejection is not null)
            {
                return SprintMutationResult.Failed(rejection);
            }
        }
        catch (DomainRuleException ex)
        {
            return SprintMutationResult.Failed(ex.Message);
        }

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return SprintMutationResult.Failed("This sprint changed while you were working on it. Reload the page and try again.");
        }
        catch (DbUpdateException)
        {
            // The ux_sprints_one_active_per_project index: a concurrent start won the race.
            return SprintMutationResult.Failed("This project already has an active sprint. Complete it before starting another.");
        }

        _logger.LogInformation("Sprint {SprintId} {Operation} by {ActorUserId}.", sprint.Id, operation, actor.UserId);
        return SprintMutationResult.Success(sprint.Id);
    }

    private void RequireCanManage(CurrentUser actor, string what)
    {
        if (!PlanningAccessPolicy.CanManageSprints(actor))
        {
            _logger.LogWarning("Authorization denied: user {ActorUserId} with role {ActorRole} attempted to {What}.", actor.UserId, actor.Role, what);
            throw new PlanningAccessDeniedException("This role may not manage sprints.");
        }
    }

    private PlanningAccessDeniedException Denied(CurrentUser actor, string what, int id)
    {
        _logger.LogWarning("Authorization denied: user {ActorUserId} attempted to {What} for id {Id}, which is not available to them.", actor.UserId, what, id);
        return new PlanningAccessDeniedException("This project or sprint is not available to you.");
    }
}
