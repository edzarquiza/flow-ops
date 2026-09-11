using FlowOps.Domain;
using FlowOps.Domain.Sla;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FlowOps.Application.Tickets;

/// <summary>
/// Ticket write-side use cases. Phase 5 owns creation only — every workflow transition
/// (<c>Assign</c>, <c>StartWork</c>, …) is Phase 6 and is deliberately absent here even though
/// the aggregate already implements them.
/// </summary>
/// <remarks>
/// Orchestration only, no business rules of its own (docs/architecture.md §3): it authorizes via
/// <see cref="TicketAccessPolicy"/>, resolves the two facts the Domain cannot look up for itself
/// (the category's owning team and the SLA target), then hands off to <see cref="Ticket.Create"/>
/// and persists with a single <c>SaveChangesAsync</c> — which is what makes the ticket row and
/// its <c>Created</c> audit event atomic (AUDIT-RULE-04, ADR-0012).
/// </remarks>
public sealed class TicketService
{
    private readonly FlowOpsDbContext _dbContext;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TicketService> _logger;

    /// <summary>
    /// <paramref name="logger"/> defaults to a no-op logger so the many existing tests that
    /// construct this service directly (<c>new TicketService(context, clock)</c>) keep compiling
    /// unchanged — DI still supplies a real <see cref="ILogger{TicketService}"/> in every
    /// production path, since <see cref="TicketService"/> is only ever constructed by the
    /// container there (CLAUDE.md §13's logging requirements; Domain itself stays free of any
    /// logging dependency).
    /// </summary>
    public TicketService(FlowOpsDbContext dbContext, TimeProvider timeProvider, ILogger<TicketService>? logger = null)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
        _logger = logger ?? NullLogger<TicketService>.Instance;
    }

    /// <summary>
    /// Creates a ticket on behalf of <paramref name="user"/>, who becomes the requester. Returns
    /// the new ticket's id and database-generated reference.
    /// </summary>
    /// <exception cref="TicketAccessDeniedException">The caller's role may not create tickets.</exception>
    /// <exception cref="DomainRuleException">A domain invariant rejected the input.</exception>
    public async Task<(int Id, string Reference)> CreateAsync(
        CreateTicketRequest request,
        CurrentUser user,
        CancellationToken cancellationToken = default)
    {
        // AUTH-RULE-04: the policy decides, not this service and not the calling page.
        if (!TicketAccessPolicy.CanCreate(user))
        {
            _logger.LogWarning(
                "Authorization denied: user {ActorUserId} with role {ActorRole} attempted to create a ticket.",
                user.UserId,
                user.Role);
            throw new TicketAccessDeniedException("This role may not create tickets.");
        }

        // TICKET-INV-02 — the Domain enforces "category belongs to team" but cannot look the
        // category's team up itself (no I/O in Domain), so that fact is resolved here and passed in.
        var categoryTeamId = await _dbContext.Categories
            .AsNoTracking()
            .Where(c => c.Id == request.CategoryId)
            .Select(c => (int?)c.TeamId)
            .SingleOrDefaultAsync(cancellationToken);

        if (categoryTeamId is null)
        {
            throw new DomainRuleException("TICKET-INV-02", "The selected category does not exist.");
        }

        // SLA-RULE-01/03: resolved from configuration and captured onto the ticket at clock start,
        // never referenced live. Four reference rows, so loading them is a trivial read; the
        // resolution order itself belongs to SlaPolicy (SLA-RULE-12), not to this service.
        var slaConfigurations = await _dbContext.SlaConfigurations
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var slaTargetMinutes = SlaPolicy.ResolveTargetMinutes(
            slaConfigurations,
            request.WorkType,
            request.Priority);

        var now = _timeProvider.GetUtcNow();

        // The requester is the authenticated caller — never a client-supplied value. Reference,
        // CreatedAt, Status, and every SLA field are set by the aggregate or the database.
        var ticket = Ticket.Create(
            title: request.Title,
            description: request.Description,
            workType: request.WorkType,
            priority: request.Priority,
            requesterId: user.UserId,
            teamId: request.TeamId,
            categoryId: request.CategoryId,
            categoryTeamId: categoryTeamId.Value,
            projectId: request.ProjectId,
            slaTargetMinutes: slaTargetMinutes,
            now: now);

        _dbContext.Tickets.Add(ticket);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Reference is populated by EF from the sequence-backed column default during INSERT
        // (docs/architecture.md §5) — Ticket.SetReference is never called.
        _logger.LogInformation(
            "Ticket {TicketId} ({TicketReference}) created by {ActorUserId}.",
            ticket.Id,
            ticket.Reference,
            user.UserId);

        return (ticket.Id, ticket.Reference!);
    }

    /// <summary>TICKET-WF-01. The assignee must be an active member of the ticket's team
    /// (TICKET-INV-03) — a fact only the database knows, so it is resolved here and handed to the
    /// aggregate, which owns the rule itself.</summary>
    public Task AssignAsync(int ticketId, Guid assigneeId, CurrentUser user, CancellationToken cancellationToken = default) =>
        MutateAsync(
            ticketId,
            user,
            "assigned",
            (snapshot, caller) => TicketAccessPolicy.CanAssign(snapshot, caller, assigneeId),
            async (ticket, now, ct) =>
            {
                var isActiveMember = await IsActiveTeamMemberAsync(ticket.TeamId, assigneeId, ct);
                ticket.Assign(assigneeId, isActiveMember, user.UserId, now);
            },
            cancellationToken);

    /// <summary>
    /// TICKET-WF-02. Authorized by <see cref="TicketAccessPolicy.CanTransition"/>: unassignment is
    /// a status transition (Assigned → Open) and the "Transition status" matrix row already states
    /// its permitted actors exactly. A dedicated <c>CanUnassign</c> would evaluate identically in
    /// every case, so adding one would be an alias with no decision of its own (CLAUDE.md §21.6).
    /// </summary>
    public Task UnassignAsync(int ticketId, CurrentUser user, CancellationToken cancellationToken = default) =>
        MutateAsync(
            ticketId,
            user,
            "status changed",
            TicketAccessPolicy.CanTransition,
            (ticket, now, _) =>
            {
                ticket.Unassign(user.UserId, now);
                return Task.CompletedTask;
            },
            cancellationToken);

    /// <summary>TICKET-WF-03. The aggregate additionally enforces "actor must be the assignee
    /// unless Manager/Admin", which is a domain invariant rather than an access decision.</summary>
    public Task StartWorkAsync(int ticketId, CurrentUser user, CancellationToken cancellationToken = default) =>
        MutateAsync(
            ticketId,
            user,
            "status changed",
            TicketAccessPolicy.CanTransition,
            (ticket, now, _) =>
            {
                ticket.StartWork(new TicketActor(user.UserId, user.Role), now);
                return Task.CompletedTask;
            },
            cancellationToken);

    /// <summary>TICKET-WF-04.</summary>
    public Task PutOnHoldAsync(int ticketId, string reason, CurrentUser user, CancellationToken cancellationToken = default) =>
        MutateAsync(
            ticketId,
            user,
            "status changed",
            TicketAccessPolicy.CanTransition,
            (ticket, now, _) =>
            {
                ticket.PutOnHold(reason, user.UserId, now);
                return Task.CompletedTask;
            },
            cancellationToken);

    /// <summary>TICKET-WF-05 / TICKET-WF-06. The pause arithmetic this triggers already lives in
    /// the aggregate (SLA-RULE-07) — nothing about it is recomputed here.</summary>
    public Task ResumeAsync(int ticketId, CurrentUser user, CancellationToken cancellationToken = default) =>
        MutateAsync(
            ticketId,
            user,
            "status changed",
            TicketAccessPolicy.CanTransition,
            (ticket, now, _) =>
            {
                ticket.Resume(user.UserId, now);
                return Task.CompletedTask;
            },
            cancellationToken);

    /// <summary>TICKET-WF-07.</summary>
    public Task ResolveAsync(
        int ticketId,
        Resolution resolutionCode,
        string resolutionNotes,
        CurrentUser user,
        CancellationToken cancellationToken = default) =>
        MutateAsync(
            ticketId,
            user,
            "resolved",
            TicketAccessPolicy.CanTransition,
            (ticket, now, _) =>
            {
                ticket.Resolve(resolutionCode, resolutionNotes, user.UserId, now);
                return Task.CompletedTask;
            },
            cancellationToken);

    /// <summary>TICKET-WF-08. Gated by <see cref="TicketAccessPolicy.CanClose"/> rather than
    /// <c>CanTransition</c> — see that method for why closing is the one transition with its own
    /// set of permitted actors.</summary>
    public Task CloseAsync(int ticketId, CurrentUser user, CancellationToken cancellationToken = default) =>
        MutateAsync(
            ticketId,
            user,
            "status changed",
            TicketAccessPolicy.CanClose,
            (ticket, now, _) =>
            {
                ticket.Close(new TicketActor(user.UserId, user.Role), now);
                return Task.CompletedTask;
            },
            cancellationToken);

    /// <summary>TICKET-WF-09. The new SLA cycle is resolved by the aggregate from the current
    /// configuration; this service only supplies the rows, it does no SLA arithmetic.</summary>
    public Task ReopenAsync(int ticketId, string reason, CurrentUser user, CancellationToken cancellationToken = default) =>
        MutateAsync(
            ticketId,
            user,
            "reopened",
            TicketAccessPolicy.CanReopen,
            async (ticket, now, ct) =>
            {
                var slaConfigurations = await _dbContext.SlaConfigurations.AsNoTracking().ToListAsync(ct);
                ticket.Reopen(reason, user.UserId, now, slaConfigurations);
            },
            cancellationToken);

    /// <summary>TICKET-WF-11.</summary>
    public Task ReassignAsync(int ticketId, Guid newAssigneeId, CurrentUser user, CancellationToken cancellationToken = default) =>
        MutateAsync(
            ticketId,
            user,
            "assigned",
            (snapshot, caller) => TicketAccessPolicy.CanAssign(snapshot, caller, newAssigneeId),
            async (ticket, now, ct) =>
            {
                var isActiveMember = await IsActiveTeamMemberAsync(ticket.TeamId, newAssigneeId, ct);
                ticket.Reassign(newAssigneeId, isActiveMember, user.UserId, now);
            },
            cancellationToken);

    /// <summary>
    /// TICKET-ENT-05. Not a workflow transition — status is untouched — but the same
    /// load-authorize-mutate-save shape applies: <c>Ticket.AddComment</c> appends the comment and
    /// its <c>CommentAdded</c> event to the same object graph, persisted by the one
    /// <c>SaveChangesAsync</c> <see cref="MutateAsync"/> already performs (AUDIT-RULE-04).
    /// </summary>
    public Task AddCommentAsync(int ticketId, string body, bool isInternal, CurrentUser user, CancellationToken cancellationToken = default) =>
        MutateAsync(
            ticketId,
            user,
            "commented",
            TicketAccessPolicy.CanComment,
            (ticket, now, _) =>
            {
                ticket.AddComment(user.UserId, body, isInternal, now);
                return Task.CompletedTask;
            },
            cancellationToken);

    /// <summary>
    /// The one shape every workflow transition follows: load the tracked aggregate, let
    /// <see cref="TicketAccessPolicy"/> decide, invoke the domain method, and persist state and
    /// its audit event together in a single <c>SaveChangesAsync</c> (AUDIT-RULE-04, ADR-0012).
    /// </summary>
    /// <remarks>
    /// A caller who may not view the ticket, and a ticket that does not exist, are both refused
    /// identically — so a probe cannot tell a forbidden ticket from an absent one.
    /// <see cref="DbUpdateConcurrencyException"/> is deliberately allowed to propagate: ADR-0011
    /// requires a concurrent edit to reach the user as "reload and retry", never a silent retry.
    /// </remarks>
    private async Task MutateAsync(
        int ticketId,
        CurrentUser user,
        string operationName,
        Func<TicketAuthorizationSnapshot, CurrentUser, bool> authorize,
        Func<Ticket, DateTimeOffset, CancellationToken, Task> mutate,
        CancellationToken cancellationToken)
    {
        var ticket = await _dbContext.Tickets.SingleOrDefaultAsync(t => t.Id == ticketId, cancellationToken);

        if (ticket is null)
        {
            _logger.LogWarning(
                "Authorization denied: user {ActorUserId} attempted to perform {Operation} on ticket {TicketId}, which does not exist.",
                user.UserId,
                operationName,
                ticketId);
            throw new TicketAccessDeniedException("This ticket is not available to you.");
        }

        var snapshot = new TicketAuthorizationSnapshot(
            ticket.Id,
            ticket.TeamId,
            ticket.RequesterId,
            ticket.AssigneeId,
            ticket.Status);

        if (!TicketAccessPolicy.CanView(snapshot, user) || !authorize(snapshot, user))
        {
            _logger.LogWarning(
                "Authorization denied: user {ActorUserId} attempted to perform {Operation} on ticket {TicketId}.",
                user.UserId,
                operationName,
                ticket.Id);
            throw new TicketAccessDeniedException("This ticket is not available to you.");
        }

        await mutate(ticket, _timeProvider.GetUtcNow(), cancellationToken);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Logged, never swallowed: ADR-0011 still requires this to reach the caller as a
            // concurrency conflict, not a silent retry — see this method's own doc comment.
            _logger.LogWarning(
                "Concurrency conflict: user {ActorUserId}'s {Operation} on ticket {TicketId} lost to a concurrent edit.",
                user.UserId,
                operationName,
                ticket.Id);
            throw;
        }

        _logger.LogInformation(
            "Ticket {TicketId} {Operation} by {ActorUserId}.",
            ticket.Id,
            operationName,
            user.UserId);
    }

    /// <summary>
    /// TICKET-INV-03's I/O half: membership of the ticket's team <em>and</em> an active account.
    /// Returns false when either is missing, letting the aggregate raise the domain error.
    /// </summary>
    private Task<bool> IsActiveTeamMemberAsync(int teamId, Guid userId, CancellationToken cancellationToken) =>
        _dbContext.TeamMembers
            .AsNoTracking()
            .Where(m => m.TeamId == teamId && m.UserId == userId)
            .Join(_dbContext.Users, m => m.UserId, u => u.Id, (_, u) => u.IsActive)
            .SingleOrDefaultAsync(cancellationToken);
}
