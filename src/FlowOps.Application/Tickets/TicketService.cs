using FlowOps.Domain;
using FlowOps.Domain.Sla;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Email;
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
    private readonly IEmailSender _emailSender;
    private readonly EmailOptions _emailOptions;
    private readonly ILogger<TicketService> _logger;

    /// <summary>
    /// Phase 30 (ADR-0035): <paramref name="emailSender"/>/<paramref name="emailOptions"/> are
    /// required, deliberately — every production composition always has a real
    /// <see cref="IEmailSender"/> (Log or Resend), and a test that does not care about email still
    /// passes a real recording test double rather than this constructor silently tolerating "none."
    /// <paramref name="logger"/> keeps its own pre-existing optional-with-no-op-default shape
    /// (Phase 5) — untouched by this phase.
    /// </summary>
    public TicketService(FlowOpsDbContext dbContext, TimeProvider timeProvider, IEmailSender emailSender, EmailOptions emailOptions, ILogger<TicketService>? logger = null)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
        _emailSender = emailSender;
        _emailOptions = emailOptions;
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
        // Phase 16: the team's organization is resolved in the same query, since a category whose
        // team belongs to a different organization must be refused exactly like a nonexistent one
        // (AUTH-RULE-04's non-disclosure pattern) — never disclosed by a different error shape.
        var categoryTeam = await _dbContext.Categories
            .AsNoTracking()
            .Where(c => c.Id == request.CategoryId)
            .Join(_dbContext.Teams, c => c.TeamId, t => t.Id, (c, t) => new { t.Id, t.OrganizationId, TeamIsActive = t.IsActive, CategoryIsActive = c.IsActive })
            .SingleOrDefaultAsync(cancellationToken);

        if (categoryTeam is null)
        {
            throw new DomainRuleException("TICKET-INV-02", "The selected category does not exist.");
        }

        if (categoryTeam.OrganizationId != user.OrganizationId)
        {
            _logger.LogWarning(
                "Authorization denied: user {ActorUserId} in organization {ActorOrganizationId} attempted to create a ticket against team {TeamId} in a different organization.",
                user.UserId,
                user.OrganizationId,
                categoryTeam.Id);
            throw new TicketAccessDeniedException("This team is not available to you.");
        }

        // Phase 22 (ADR-0022): a deactivated team or category can no longer be selected for a
        // *new* ticket, refused identically to a nonexistent/cross-organization one — existing
        // tickets already filed against either are completely unaffected, since neither row is
        // ever touched by deactivation.
        if (!categoryTeam.TeamIsActive)
        {
            throw new TicketAccessDeniedException("This team is not available to you.");
        }

        if (!categoryTeam.CategoryIsActive)
        {
            throw new TicketAccessDeniedException("This category is not available to you.");
        }

        // Phase 16: a caller-supplied ProjectId must belong to the caller's own organization. Not
        // found, wrong-organization, and inactive are all refused identically, for the same
        // non-disclosure reason — a deactivated project (project management phase) is no longer a
        // valid choice for a *new* ticket, even if the client somehow still submits its id.
        if (request.ProjectId is { } projectId)
        {
            var projectExistsInOrganization = await _dbContext.Projects
                .AsNoTracking()
                .AnyAsync(p => p.Id == projectId && p.OrganizationId == user.OrganizationId && p.IsActive, cancellationToken);

            if (!projectExistsInOrganization)
            {
                throw new TicketAccessDeniedException("This project is not available to you.");
            }
        }

        var categoryTeamId = categoryTeam.Id;

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
            categoryTeamId: categoryTeamId,
            projectId: request.ProjectId,
            slaTargetMinutes: slaTargetMinutes,
            now: now,
            plannedStartDate: request.PlannedStartDate,
            dueDate: request.DueDate);

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
    public async Task AssignAsync(int ticketId, Guid assigneeId, CurrentUser user, CancellationToken cancellationToken = default)
    {
        await MutateAsync(
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

        // Phase 30 (ADR-0035): self-assign is never emailed — the actor already knows what they
        // just did. Strictly after MutateAsync's own SaveChangesAsync already committed.
        if (assigneeId != user.UserId)
        {
            await TrySendAssignmentEmailAsync(ticketId, assigneeId, cancellationToken);
        }
    }

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
    public async Task ReassignAsync(int ticketId, Guid newAssigneeId, CurrentUser user, CancellationToken cancellationToken = default)
    {
        await MutateAsync(
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

        // Phase 30 (ADR-0035): same self-assign exclusion as AssignAsync — a Manager/Admin
        // reassigning a ticket to themselves is not emailed either.
        if (newAssigneeId != user.UserId)
        {
            await TrySendAssignmentEmailAsync(ticketId, newAssigneeId, cancellationToken);
        }
    }

    /// <summary>
    /// Phase 30C (ADR-0033): the "Change priority" AUTH-RULE-02 row — <see cref="Ticket.ChangePriority"/>
    /// already existed in the Domain (TICKET-INV-08/SLA-RULE-06) but was never reachable from any
    /// service or page before this phase. Recomputes the SLA target/due date from the current
    /// configuration, exactly as <see cref="CreateAsync"/> resolves it for a new ticket.
    /// </summary>
    public Task ChangePriorityAsync(int ticketId, Priority newPriority, CurrentUser user, CancellationToken cancellationToken = default) =>
        MutateAsync(
            ticketId,
            user,
            "priority changed",
            TicketAccessPolicy.CanTransition,
            async (ticket, now, ct) =>
            {
                var slaConfigurations = await _dbContext.SlaConfigurations.AsNoTracking().ToListAsync(ct);
                ticket.ChangePriority(newPriority, slaConfigurations, user.UserId, now);
            },
            cancellationToken);

    /// <summary>
    /// Phase 30C (ADR-0033): the "Change category" AUTH-RULE-02 row (same footprint as "Change
    /// priority" — no separate policy method, matching how that row itself piggybacks on
    /// <see cref="TicketAccessPolicy.CanTransition"/>). The new category must belong to the
    /// ticket's own team (TICKET-INV-02, enforced by <see cref="Ticket.ChangeCategory"/> itself)
    /// and must be active — re-validated here exactly as <see cref="CreateAsync"/> validates a new
    /// ticket's category, since a caller-chosen id is never trusted.
    /// </summary>
    public Task ChangeCategoryAsync(int ticketId, int newCategoryId, CurrentUser user, CancellationToken cancellationToken = default) =>
        MutateAsync(
            ticketId,
            user,
            "category changed",
            TicketAccessPolicy.CanTransition,
            async (ticket, now, ct) =>
            {
                var category = await _dbContext.Categories
                    .AsNoTracking()
                    .Where(c => c.Id == newCategoryId)
                    .Select(c => new { c.TeamId, c.IsActive })
                    .SingleOrDefaultAsync(ct);

                if (category is null || !category.IsActive)
                {
                    throw new DomainRuleException("TICKET-INV-02", "The selected category is not available.");
                }

                ticket.ChangeCategory(newCategoryId, category.TeamId, user.UserId, now);
            },
            cancellationToken);

    /// <summary>
    /// Phase 30C (ADR-0033): the "Change team" AUTH-RULE-02 row. Takes only the destination
    /// <paramref name="newCategoryId"/> — its own team is the destination team, never a second,
    /// independently chosen id. <see cref="Ticket.ChangeTeam"/> moves <c>TeamId</c> alone and never
    /// touches <c>CategoryId</c>, so calling it with a team id that does not match the caller's
    /// chosen category would leave the ticket referencing a category that belongs to its *old*
    /// team, silently violating TICKET-INV-02 the moment it returned — deriving the team from the
    /// category makes that state unreachable rather than merely validated against. The category's
    /// team must be active and in the caller's own organization, the same checks
    /// <see cref="CreateAsync"/> applies to a new ticket's team. Two audit events (TeamChanged,
    /// then CategoryChanged) are appended by one mutation and saved atomically, exactly like
    /// <see cref="Ticket.Create"/>'s own single <c>SaveChangesAsync</c> for its own two initial facts.
    /// </summary>
    public Task ChangeTeamAsync(int ticketId, int newCategoryId, CurrentUser user, CancellationToken cancellationToken = default) =>
        MutateAsync(
            ticketId,
            user,
            "team changed",
            TicketAccessPolicy.CanTransition,
            async (ticket, now, ct) =>
            {
                var category = await _dbContext.Categories
                    .AsNoTracking()
                    .Where(c => c.Id == newCategoryId)
                    .Join(_dbContext.Teams, c => c.TeamId, t => t.Id, (c, t) => new { c.TeamId, CategoryIsActive = c.IsActive, t.OrganizationId, TeamIsActive = t.IsActive })
                    .SingleOrDefaultAsync(ct);

                if (category is null || !category.CategoryIsActive)
                {
                    throw new DomainRuleException("TICKET-INV-02", "The selected category is not available.");
                }

                if (category.OrganizationId != user.OrganizationId || !category.TeamIsActive)
                {
                    throw new DomainRuleException("TICKET-INV-08", "The selected team is not available.");
                }

                ticket.ChangeTeam(category.TeamId, user.UserId, now);
                ticket.ChangeCategory(newCategoryId, category.TeamId, user.UserId, now);
            },
            cancellationToken);

    /// <summary>
    /// Phase 30C (ADR-0033): the "Change due date" AUTH-RULE-02 row. Unlike Priority/Category/Team,
    /// <see cref="Ticket.ChangeDueDate"/> is deliberately not gated by TICKET-INV-08 or any other
    /// invariant (its own doc comment says so explicitly) — a due date may be corrected even on a
    /// Resolved or Closed ticket, and this method does not add a restriction the Domain itself does
    /// not have. <paramref name="newDueDate"/> of <see langword="null"/> clears it.
    /// </summary>
    public Task ChangeDueDateAsync(int ticketId, DateTimeOffset? newDueDate, CurrentUser user, CancellationToken cancellationToken = default) =>
        MutateAsync(
            ticketId,
            user,
            "due date changed",
            TicketAccessPolicy.CanTransition,
            (ticket, now, _) =>
            {
                ticket.ChangeDueDate(newDueDate, user.UserId, now);
                return Task.CompletedTask;
            },
            cancellationToken);

    /// <summary>
    /// ADR-0029: plans the ticket into a sprint, or out of any sprint when <paramref name="sprintId"/>
    /// is null. Same load-authorize-mutate-save shape as every transition (org-scoped ticket load,
    /// <see cref="TicketAccessPolicy.CanPlan"/>, one audit event, xmin concurrency). The sprint must
    /// belong to the ticket's own project and the caller's organization — a sprint id from another
    /// project or organization is refused exactly like a missing one — and must not be completed.
    /// Status, assignee, SLA, and dates are never touched.
    /// </summary>
    public Task MoveToSprintAsync(int ticketId, int? sprintId, CurrentUser user, CancellationToken cancellationToken = default) =>
        MutateAsync(
            ticketId,
            user,
            "planned into a sprint",
            TicketAccessPolicy.CanPlan,
            async (ticket, now, ct) =>
            {
                var acceptsTickets = true;
                if (sprintId is { } id)
                {
                    var projectId = ticket.ProjectId;
                    var status = await _dbContext.Sprints
                        .AsNoTracking()
                        .Where(s => s.Id == id
                            && s.ProjectId == projectId
                            && _dbContext.Projects.Any(p => p.Id == s.ProjectId && p.OrganizationId == user.OrganizationId))
                        .Select(s => (FlowOps.Domain.Planning.SprintStatus?)s.Status)
                        .SingleOrDefaultAsync(ct);

                    if (status is null)
                    {
                        throw new TicketAccessDeniedException("This sprint is not available to you.");
                    }

                    acceptsTickets = status is FlowOps.Domain.Planning.SprintStatus.Planned or FlowOps.Domain.Planning.SprintStatus.Active;
                }

                ticket.MoveToSprint(sprintId, acceptsTickets, user.UserId, now);
            },
            cancellationToken);

    /// <summary>
    /// ADR-0030: a drag on the sprint board. <see cref="BoardMovePlanner"/> turns (status, backlog flag,
    /// target column) into the existing operations that realize it — pull/return backlog, assign,
    /// start work, resume, hold, resolve, reopen — and each one is authorized with the same
    /// <see cref="TicketAccessPolicy"/> call and validated by the same <see cref="Ticket"/> method its
    /// explicit action uses. Nothing sets a status directly; an illegal drag is rejected with the
    /// planner's message (or the domain's own rule) and leaves the ticket untouched, because the
    /// whole move is one load-authorize-mutate-save (a rejected step aborts before any save).
    /// </summary>
    public Task MoveOnBoardAsync(int ticketId, FlowOps.Application.Planning.BoardColumnKey target, FlowOps.Application.Planning.BoardMoveInput input, CurrentUser user, CancellationToken cancellationToken = default) =>
        MutateAsync(
            ticketId,
            user,
            "moved on the sprint board",
            (_, _) => true, // CanView already passed; each step below authorizes with its own policy
            async (ticket, now, ct) =>
            {
                if (ticket.SprintId is null)
                {
                    throw new DomainRuleException("BOARD-MOVE", "This ticket is not on a sprint board.");
                }

                var plan = FlowOps.Application.Planning.BoardMovePlanner.Plan(ticket.Status, ticket.SprintBacklog, target);
                if (!plan.IsAllowed)
                {
                    throw new DomainRuleException("BOARD-MOVE", plan.Rejection!);
                }

                TicketAuthorizationSnapshot Snapshot() => new(ticket.Id, ticket.TeamId, ticket.RequesterId, ticket.AssigneeId, ticket.Status);

                void Require(bool allowed)
                {
                    if (!allowed)
                    {
                        _logger.LogWarning("Authorization denied: user {ActorUserId} attempted a board move on ticket {TicketId}.", user.UserId, ticket.Id);
                        throw new TicketAccessDeniedException("This ticket is not available to you.");
                    }
                }

                foreach (var step in plan.Steps)
                {
                    switch (step)
                    {
                        case FlowOps.Application.Planning.BoardStep.PullFromBacklog:
                            Require(TicketAccessPolicy.CanPlan(Snapshot(), user));
                            ticket.PullFromSprintBacklog(user.UserId, now);
                            break;
                        case FlowOps.Application.Planning.BoardStep.ClearBacklogFlagIfPermitted:
                            if (ticket.SprintBacklog && TicketAccessPolicy.CanPlan(Snapshot(), user))
                            {
                                ticket.PullFromSprintBacklog(user.UserId, now);
                            }

                            break;
                        case FlowOps.Application.Planning.BoardStep.ReturnToBacklog:
                            Require(TicketAccessPolicy.CanPlan(Snapshot(), user));
                            ticket.ReturnToSprintBacklog(user.UserId, now);
                            break;
                        case FlowOps.Application.Planning.BoardStep.AssignToCaller:
                            Require(TicketAccessPolicy.CanAssign(Snapshot(), user, user.UserId));
                            ticket.Assign(user.UserId, await IsActiveTeamMemberAsync(ticket.TeamId, user.UserId, ct), user.UserId, now);
                            break;
                        case FlowOps.Application.Planning.BoardStep.StartWork:
                            Require(TicketAccessPolicy.CanTransition(Snapshot(), user));
                            ticket.StartWork(new TicketActor(user.UserId, user.Role), now);
                            break;
                        case FlowOps.Application.Planning.BoardStep.StartWorkIfAssigned:
                            if (ticket.Status == Status.Assigned)
                            {
                                Require(TicketAccessPolicy.CanTransition(Snapshot(), user));
                                ticket.StartWork(new TicketActor(user.UserId, user.Role), now);
                            }

                            break;
                        case FlowOps.Application.Planning.BoardStep.Resume:
                            Require(TicketAccessPolicy.CanTransition(Snapshot(), user));
                            ticket.Resume(user.UserId, now);
                            break;
                        case FlowOps.Application.Planning.BoardStep.PutOnHold:
                            Require(TicketAccessPolicy.CanTransition(Snapshot(), user));
                            ticket.PutOnHold(input.Reason ?? string.Empty, user.UserId, now);
                            break;
                        case FlowOps.Application.Planning.BoardStep.Resolve:
                            Require(TicketAccessPolicy.CanTransition(Snapshot(), user));
                            ticket.Resolve(input.ResolutionCode ?? Resolution.Fixed, input.ResolutionNotes ?? string.Empty, user.UserId, now);
                            break;
                        case FlowOps.Application.Planning.BoardStep.Reopen:
                            Require(TicketAccessPolicy.CanReopen(Snapshot(), user));
                            var slaConfigurations = await _dbContext.SlaConfigurations.AsNoTracking().ToListAsync(ct);
                            ticket.Reopen(input.Reason ?? string.Empty, user.UserId, now, slaConfigurations);
                            break;
                    }
                }
            },
            cancellationToken);

    /// <summary>ADR-0029: takes a ticket out of its sprint's backlog and onto the board's status
    /// columns. Same authority as planning it into the sprint.</summary>
    public Task PullFromSprintBacklogAsync(int ticketId, CurrentUser user, CancellationToken cancellationToken = default) =>
        MutateAsync(
            ticketId,
            user,
            "pulled from a sprint backlog",
            TicketAccessPolicy.CanPlan,
            (ticket, now, _) =>
            {
                ticket.PullFromSprintBacklog(user.UserId, now);
                return Task.CompletedTask;
            },
            cancellationToken);

    /// <summary>
    /// TICKET-ENT-05. Not a workflow transition — status is untouched — but the same
    /// load-authorize-mutate-save shape applies: <c>Ticket.AddComment</c> appends the comment and
    /// its <c>CommentAdded</c> event to the same object graph, persisted by the one
    /// <c>SaveChangesAsync</c> <see cref="MutateAsync"/> already performs (AUDIT-RULE-04).
    /// </summary>
    public async Task AddCommentAsync(int ticketId, string body, bool isInternal, CurrentUser user, CancellationToken cancellationToken = default)
    {
        await MutateAsync(
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

        // Phase 30 (ADR-0035): strictly after the comment is already committed.
        await TrySendCommentEmailAsync(ticketId, body, isInternal, user, cancellationToken);
    }

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
        // Phase 16: a ticket outside the caller's organization must be indistinguishable from a
        // ticket that does not exist at all — this is the single load point every workflow
        // transition (Assign, StartWork, Resolve, Close, Reopen, comment, ...) goes through, so
        // scoping it here closes the same cross-tenant gap for every one of them at once, exactly
        // as TicketQueryService.ApplyViewScope closes it for the read side.
        var ticket = await _dbContext.Tickets
            .Where(t => _dbContext.Teams.Any(team => team.Id == t.TeamId && team.OrganizationId == user.OrganizationId))
            .SingleOrDefaultAsync(t => t.Id == ticketId, cancellationToken);

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

    /// <summary>
    /// Phase 30 (ADR-0035): the ticket-assignment email — the new assignee only (self-assign is
    /// excluded by <see cref="AssignAsync"/>/<see cref="ReassignAsync"/> before this is even
    /// called). TICKET-INV-03 already guarantees the assignee is an active team member by the time
    /// this runs (<see cref="IsActiveTeamMemberAsync"/> would have failed the mutation otherwise),
    /// so the <c>IsActive</c> check here is defensive, not load-bearing.
    /// </summary>
    private async Task TrySendAssignmentEmailAsync(int ticketId, Guid assigneeId, CancellationToken cancellationToken)
    {
        var assignee = await _dbContext.Users
            .AsNoTracking()
            .Where(u => u.Id == assigneeId)
            .Select(u => new { u.Email, u.DisplayName, u.IsActive })
            .SingleOrDefaultAsync(cancellationToken);
        if (assignee is null || !assignee.IsActive || string.IsNullOrWhiteSpace(assignee.Email))
        {
            return;
        }

        var ticket = await _dbContext.Tickets
            .AsNoTracking()
            .Where(t => t.Id == ticketId)
            .Join(_dbContext.Teams, t => t.TeamId, team => team.Id, (t, team) => new { t.Reference, t.Title, t.Priority, t.Status, TeamName = team.Name })
            .SingleOrDefaultAsync(cancellationToken);
        if (ticket is null)
        {
            return;
        }

        var ticketUrl = $"{_emailOptions.BaseUrl.TrimEnd('/')}/Tickets/Details/{ticketId}";

        var textBody =
            $"""
            You've been assigned {ticket.Reference}: {ticket.Title}

            Priority: {ticket.Priority}
            Team: {ticket.TeamName}
            Status: {ticket.Status}

            View the ticket: {ticketUrl}

            — FlowOps
            """;

        var htmlBody =
            $"""
            <p>You've been assigned <strong>{System.Net.WebUtility.HtmlEncode(ticket.Reference)}: {System.Net.WebUtility.HtmlEncode(ticket.Title)}</strong>.</p>
            <p>Priority: {ticket.Priority} &middot; Team: {System.Net.WebUtility.HtmlEncode(ticket.TeamName)} &middot; Status: {ticket.Status}</p>
            <p><a href="{ticketUrl}">View the ticket</a></p>
            <p>— FlowOps</p>
            """;

        var message = new EmailMessage(assignee.Email!, assignee.DisplayName, $"You've been assigned {ticket.Reference}", textBody, htmlBody);
        await TrySendAsync(message, "Assignment", ticketId, cancellationToken);
    }

    /// <summary>
    /// Phase 30 (ADR-0035): the new-comment email — ticket requester + current assignee, excluding
    /// the comment author and any duplicate (the same person can be both), any deactivated account,
    /// and — only for an internal comment — anyone whose role fails
    /// <see cref="TicketAccessPolicy.CanSeeInternalComments"/>. Reuses that exact policy method
    /// (via a minimal, throwaway <see cref="CurrentUser"/> carrying just the recipient's own role)
    /// rather than restating its one-line rule here.
    /// </summary>
    private async Task TrySendCommentEmailAsync(int ticketId, string commentBody, bool isInternal, CurrentUser author, CancellationToken cancellationToken)
    {
        var ticket = await _dbContext.Tickets
            .AsNoTracking()
            .Where(t => t.Id == ticketId)
            .Select(t => new { t.Reference, t.Title, t.RequesterId, t.AssigneeId })
            .SingleOrDefaultAsync(cancellationToken);
        if (ticket is null)
        {
            return;
        }

        var candidateIds = new HashSet<Guid> { ticket.RequesterId };
        if (ticket.AssigneeId is { } assigneeId)
        {
            candidateIds.Add(assigneeId);
        }

        candidateIds.Remove(author.UserId);
        if (candidateIds.Count == 0)
        {
            return;
        }

        var candidates = await _dbContext.Users
            .AsNoTracking()
            .Where(u => candidateIds.Contains(u.Id))
            .Join(
                _dbContext.OrganizationMemberships.Where(m => m.OrganizationId == author.OrganizationId),
                u => u.Id,
                m => m.UserId,
                (u, m) => new { u.Id, u.Email, u.DisplayName, u.IsActive, m.Role })
            .ToListAsync(cancellationToken);

        var recipients = candidates
            .Where(c => c.IsActive && !string.IsNullOrWhiteSpace(c.Email))
            .Where(c => !isInternal || TicketAccessPolicy.CanSeeInternalComments(new CurrentUser(c.Id, author.OrganizationId, c.Role, new HashSet<int>(), new HashSet<int>())))
            .ToList();
        if (recipients.Count == 0)
        {
            return;
        }

        var authorName = await _dbContext.Users
            .AsNoTracking()
            .Where(u => u.Id == author.UserId)
            .Select(u => u.DisplayName)
            .SingleOrDefaultAsync(cancellationToken) ?? "A team member";

        var ticketUrl = $"{_emailOptions.BaseUrl.TrimEnd('/')}/Tickets/Details/{ticketId}";
        const int maxQuotedLength = 500;
        var quotedComment = commentBody.Length > maxQuotedLength ? commentBody[..maxQuotedLength] + "…" : commentBody;

        foreach (var recipient in recipients)
        {
            var textBody =
                $"""
                {authorName} commented on {ticket.Reference}: {ticket.Title}

                "{quotedComment}"

                View the ticket: {ticketUrl}

                — FlowOps
                """;

            var htmlBody =
                $"""
                <p>{System.Net.WebUtility.HtmlEncode(authorName)} commented on <strong>{System.Net.WebUtility.HtmlEncode(ticket.Reference)}: {System.Net.WebUtility.HtmlEncode(ticket.Title)}</strong>.</p>
                <blockquote>{System.Net.WebUtility.HtmlEncode(quotedComment)}</blockquote>
                <p><a href="{ticketUrl}">View the ticket</a></p>
                <p>— FlowOps</p>
                """;

            var message = new EmailMessage(recipient.Email!, recipient.DisplayName, $"New comment on {ticket.Reference}", textBody, htmlBody);
            await TrySendAsync(message, "Comment", ticketId, cancellationToken);
        }
    }

    /// <summary>The one place every trigger's try/catch + Warning-log shape lives — an email
    /// delivery failure (thrown or returned) is always logged and never propagated, so it can
    /// never affect the business operation that already committed before this was ever called.</summary>
    private async Task TrySendAsync(EmailMessage message, string emailKind, int ticketId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _emailSender.SendAsync(message, cancellationToken);
            if (!result.Succeeded)
            {
                _logger.LogWarning("{EmailKind} email delivery failed for ticket {TicketId}: {Error}", emailKind, ticketId, result.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{EmailKind} email delivery threw an exception for ticket {TicketId}.", emailKind, ticketId);
        }
    }
}
