using System.ComponentModel.DataAnnotations;
using FlowOps.Application.Tickets;
using FlowOps.Domain;
using FlowOps.Domain.Tickets;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FlowOps.Web.Pages.Tickets;

/// <summary>
/// Ticket detail, its workflow actions, and its audit history. A ticket the caller may not view
/// and a ticket that does not exist both produce the same 404 — the visibility scope lives in the
/// query, so this page never learns the difference and cannot leak it.
/// </summary>
/// <remarks>
/// Every handler follows the same shape: resolve the caller, call one <see cref="TicketService"/>
/// method, redirect on success. The three failure modes map to the responses CLAUDE.md §11.3
/// prescribes — 403 for an authorization refusal, an inline message for a domain-rule rejection,
/// and a reload-and-retry message for a concurrent edit (ADR-0011). No authorization rule is
/// evaluated here; the policy decides and the service enforces.
/// </remarks>
public sealed class DetailsModel : PageModel
{
    private readonly CurrentUserAccessor _currentUserAccessor;
    private readonly TicketQueryService _ticketQueryService;
    private readonly TicketService _ticketService;
    private readonly AttentionQueryService _attentionQueryService;

    public DetailsModel(
        CurrentUserAccessor currentUserAccessor,
        TicketQueryService ticketQueryService,
        TicketService ticketService,
        AttentionQueryService attentionQueryService)
    {
        _currentUserAccessor = currentUserAccessor;
        _ticketQueryService = ticketQueryService;
        _ticketService = ticketService;
        _attentionQueryService = attentionQueryService;
    }

    public TicketDetail Ticket { get; private set; } = null!;

    public IReadOnlyList<TicketTimelineEntry> History { get; private set; } = [];

    /// <summary>Phase 30: "Why this needs attention" — null on a ticket with no current signals
    /// (including every terminal ticket), in which case the panel simply does not render.</summary>
    public AttentionBrief? AttentionBrief { get; private set; }

    /// <summary>Which workflow actions to offer. A UX affordance only — the service re-decides.</summary>
    public WorkflowAffordances Actions { get; private set; } = new();

    /// <summary>Non-empty only when <see cref="WorkflowAffordances.AssignToOther"/> — the "assign
    /// to someone else" picker's own data source.</summary>
    public IReadOnlyList<AssignableMember> AssignableMembers { get; private set; } = [];

    /// <summary>Phase 30C: the Team/Category pickers' own data source — non-empty only when at
    /// least one of <see cref="WorkflowAffordances.EditCategory"/>/<see cref="WorkflowAffordances.EditTeam"/>
    /// is offered. The same active, organization/caller-scoped tree <see cref="TicketService.CreateAsync"/>'s
    /// own Create Ticket form already uses — never a second, parallel data source.</summary>
    public TicketCreationOptions EditOptions { get; private set; } = new([], []);

    [BindProperty]
    public WorkflowInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken = default) =>
        await LoadOrNotFoundAsync(id, cancellationToken);

    public Task<IActionResult> OnPostAssignAsync(int id, CancellationToken cancellationToken = default) =>
        RunAsync(id, user => _ticketService.AssignAsync(id, user.UserId, user, cancellationToken), cancellationToken);

    /// <summary>"Assign to someone else" — <paramref name="assigneeId"/> is a caller-chosen value,
    /// same as every other id bound from a form in this app; <c>TicketService.AssignAsync</c>
    /// re-authorizes and re-validates it independently (TicketAccessPolicy.CanAssign,
    /// TICKET-INV-03) regardless of what <see cref="AssignableMembers"/> offered.</summary>
    public Task<IActionResult> OnPostAssignToAsync(int id, Guid assigneeId, CancellationToken cancellationToken = default) =>
        RunAsync(id, user => _ticketService.AssignAsync(id, assigneeId, user, cancellationToken), cancellationToken);

    public Task<IActionResult> OnPostUnassignAsync(int id, CancellationToken cancellationToken = default) =>
        RunAsync(id, user => _ticketService.UnassignAsync(id, user, cancellationToken), cancellationToken);

    public Task<IActionResult> OnPostStartWorkAsync(int id, CancellationToken cancellationToken = default) =>
        RunAsync(id, user => _ticketService.StartWorkAsync(id, user, cancellationToken), cancellationToken);

    public Task<IActionResult> OnPostPutOnHoldAsync(int id, CancellationToken cancellationToken = default) =>
        RunAsync(id, user => _ticketService.PutOnHoldAsync(id, Input.PendingReason ?? string.Empty, user, cancellationToken), cancellationToken);

    public Task<IActionResult> OnPostResumeAsync(int id, CancellationToken cancellationToken = default) =>
        RunAsync(id, user => _ticketService.ResumeAsync(id, user, cancellationToken), cancellationToken);

    public Task<IActionResult> OnPostResolveAsync(int id, CancellationToken cancellationToken = default) =>
        RunAsync(
            id,
            user => _ticketService.ResolveAsync(id, Input.ResolutionCode, Input.ResolutionNotes ?? string.Empty, user, cancellationToken),
            cancellationToken);

    public Task<IActionResult> OnPostCloseAsync(int id, CancellationToken cancellationToken = default) =>
        RunAsync(id, user => _ticketService.CloseAsync(id, user, cancellationToken), cancellationToken);

    public Task<IActionResult> OnPostReopenAsync(int id, CancellationToken cancellationToken = default) =>
        RunAsync(id, user => _ticketService.ReopenAsync(id, Input.ReopenReason ?? string.Empty, user, cancellationToken), cancellationToken);

    /// <summary>
    /// TICKET-ENT-05. The "internal" checkbox is bound like any other input; server-side
    /// authorization is <see cref="TicketAccessPolicy.CanComment"/> inside
    /// <see cref="TicketService.AddCommentAsync"/> — hiding the form for a Viewer (decision 8 of
    /// the WorkflowAffordances below) is an affordance only. A forged POST from a Viewer still
    /// reaches this handler and is still refused by the service (CLAUDE.md §2 rule 8).
    /// </summary>
    public Task<IActionResult> OnPostAddCommentAsync(int id, CancellationToken cancellationToken = default) =>
        RunAsync(
            id,
            user => _ticketService.AddCommentAsync(id, Input.CommentBody ?? string.Empty, Input.CommentIsInternal, user, cancellationToken),
            cancellationToken);

    /// <summary>Phase 30C (ADR-0033).</summary>
    public Task<IActionResult> OnPostChangePriorityAsync(int id, CancellationToken cancellationToken = default) =>
        RunAsync(id, user => _ticketService.ChangePriorityAsync(id, Input.NewPriority, user, cancellationToken), cancellationToken);

    /// <summary>Phase 30C (ADR-0033).</summary>
    public Task<IActionResult> OnPostChangeCategoryAsync(int id, CancellationToken cancellationToken = default) =>
        RunAsync(id, user => _ticketService.ChangeCategoryAsync(id, Input.NewCategoryId, user, cancellationToken), cancellationToken);

    /// <summary>Phase 30C (ADR-0033): a single category picker — its own team becomes the ticket's
    /// new team. See <see cref="TicketService.ChangeTeamAsync"/>'s own doc comment for why a bare
    /// team-only change is never offered.</summary>
    public Task<IActionResult> OnPostChangeTeamAsync(int id, CancellationToken cancellationToken = default) =>
        RunAsync(id, user => _ticketService.ChangeTeamAsync(id, Input.NewTeamCategoryId, user, cancellationToken), cancellationToken);

    /// <summary>Phase 30C (ADR-0033). A blank date input binds <see cref="WorkflowInput.NewDueDate"/>
    /// to <see langword="null"/> — the same "clear by submitting empty" shape <c>ChangeDueDateAsync</c>
    /// already accepts, no separate "clear" control needed.</summary>
    public Task<IActionResult> OnPostChangeDueDateAsync(int id, CancellationToken cancellationToken = default) =>
        RunAsync(id, user => _ticketService.ChangeDueDateAsync(id, ToUtc(Input.NewDueDate), user, cancellationToken), cancellationToken);

    /// <summary>Same documented limitation as <c>Tickets/Create.cshtml.cs</c>'s own copy: a
    /// <c>datetime-local</c> input carries no timezone offset at all, so the value is trusted as
    /// UTC-as-typed — correct only for a server whose local time happens to be UTC, which is how
    /// this app is deployed.</summary>
    private static DateTimeOffset? ToUtc(DateTime? value) =>
        value is { } v ? new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)) : null;

    private async Task<IActionResult> RunAsync(
        int id,
        Func<CurrentUser, Task> action,
        CancellationToken cancellationToken)
    {
        var user = await _currentUserAccessor.GetCurrentUserAsync(User, cancellationToken);
        if (user is null)
        {
            return Forbid();
        }

        try
        {
            await action(user);
            return RedirectToPage("/Tickets/Details", new { id });
        }
        catch (TicketAccessDeniedException)
        {
            return Forbid();
        }
        catch (DomainRuleException ex)
        {
            // An illegal transition or a missing required field — the user's problem to fix, not
            // an unhandled exception page. The message is shown as-is; the rule code is not part of
            // it (developer-facing) and goes to the log so a rejection stays traceable.
            HttpContext.RequestServices.GetRequiredService<ILogger<DetailsModel>>()
                .LogInformation("Ticket {TicketId} action rejected by rule {RuleCode}: {Message}", id, ex.RuleCode, ex.Message);
            ModelState.AddModelError(string.Empty, ex.Message);
            return await LoadOrNotFoundAsync(id, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // ADR-0011: surfaced, never retried automatically — retrying would apply a stale
            // mutation on top of whatever the other actor just committed.
            ModelState.AddModelError(
                string.Empty,
                "This ticket changed while you were working on it. Reload the page and try again.");
            return await LoadOrNotFoundAsync(id, cancellationToken);
        }
    }

    private async Task<IActionResult> LoadOrNotFoundAsync(int id, CancellationToken cancellationToken)
    {
        var user = await _currentUserAccessor.GetCurrentUserAsync(User, cancellationToken);
        if (user is null)
        {
            return Forbid();
        }

        var ticket = await _ticketQueryService.GetDetailAsync(id, user, cancellationToken);
        if (ticket is null)
        {
            return NotFound();
        }

        Ticket = ticket;
        History = await _ticketQueryService.GetHistoryAsync(id, user, cancellationToken);
        Actions = WorkflowAffordances.For(ticket, user);
        AssignableMembers = Actions.AssignToOther
            ? await _ticketQueryService.GetAssignableTeamMembersAsync(user, ticket.TeamId, cancellationToken)
            : [];
        // Phase 30C: the Category/Team edit pickers' own data — the same tree Create Ticket
        // already builds from, so a Category/Team edit can never offer a choice Create Ticket
        // itself would have refused.
        EditOptions = Actions.EditCategory || Actions.EditTeam
            ? EnsureCurrentCategoryIsSelectable(await _ticketQueryService.GetCreationOptionsAsync(user, cancellationToken), ticket)
            : new TicketCreationOptions([], []);
        // Phase 30: read-only, explanatory only — never affects Actions/EditOptions above.
        AttentionBrief = await _attentionQueryService.GetBriefAsync(user, id, cancellationToken);
        return Page();
    }

    /// <summary>
    /// <see cref="TicketQueryService.GetCreationOptionsAsync"/> only ever offers <em>active</em>
    /// teams/categories (correctly, for a <em>new</em> ticket) — but an existing ticket may already
    /// reference a category (or team) that has since been deactivated (ADR-0022: deactivation never
    /// touches an existing ticket). Left as-is, the edit picker would have no option matching the
    /// ticket's actual current value, the browser would default to selecting whatever the first
    /// option happens to be, and submitting the form without deliberately choosing anything would
    /// silently change the ticket instead of being a safe no-op. This guarantees the ticket's own
    /// current category — and, for the team picker, its own current team — always appears as a
    /// genuine, selectable (and pre-selected) option, synthesizing one if it is otherwise absent.
    /// </summary>
    private static TicketCreationOptions EnsureCurrentCategoryIsSelectable(TicketCreationOptions options, TicketDetail ticket)
    {
        var currentCategory = new CategoryOption(ticket.CategoryId, ticket.CategoryName);
        var ownTeam = options.Teams.FirstOrDefault(t => t.TeamId == ticket.TeamId);

        if (ownTeam is null)
        {
            var teams = options.Teams.Append(new TeamOption(ticket.TeamId, ticket.TeamName, [currentCategory])).ToList();
            return options with { Teams = teams };
        }

        if (ownTeam.Categories.Any(c => c.CategoryId == ticket.CategoryId))
        {
            return options;
        }

        var patchedCategories = ownTeam.Categories.Append(currentCategory).ToList();
        var patchedTeam = ownTeam with { Categories = patchedCategories };
        var patchedTeams = options.Teams.Select(t => t.TeamId == ticket.TeamId ? patchedTeam : t).ToList();
        return options with { Teams = patchedTeams };
    }

    /// <summary>
    /// Which buttons to render. Each combines the same <see cref="TicketAccessPolicy"/> call the
    /// service will make with the status the transition is legal from, so the page does not offer
    /// an action that is certain to fail. It grants nothing: the service authorizes independently.
    /// </summary>
    public sealed class WorkflowAffordances
    {
        public bool Assign { get; private init; }

        /// <summary>Offer a picker for a target other than the caller — mirrors
        /// <see cref="TicketAccessPolicy.CanAssign"/>'s own Admin/Manager branches directly rather
        /// than calling it with a fake target id (its Agent branch requires the target to equal
        /// the caller, so evaluating it against an arbitrary "other" id would always answer
        /// false for Agent regardless of who that other person actually is).</summary>
        public bool AssignToOther { get; private init; }

        public bool Unassign { get; private init; }

        public bool StartWork { get; private init; }

        public bool PutOnHold { get; private init; }

        public bool Resume { get; private init; }

        public bool Resolve { get; private init; }

        public bool Close { get; private init; }

        public bool Reopen { get; private init; }

        public bool Comment { get; private init; }

        /// <summary>Phase 30C (ADR-0033): Priority/Category/Team edits mirror
        /// <see cref="Ticket.ChangePriority"/>/<see cref="Ticket.ChangeCategory"/>/
        /// <see cref="Ticket.ChangeTeam"/>'s own TICKET-INV-08 "not on a terminal ticket"
        /// restriction — offering one on a Resolved/Closed ticket would be certain to fail.</summary>
        public bool EditPriority { get; private init; }

        public bool EditCategory { get; private init; }

        public bool EditTeam { get; private init; }

        /// <summary>Phase 30C (ADR-0033): unlike Priority/Category/Team, <see cref="Ticket.ChangeDueDate"/>
        /// carries no terminal-ticket restriction — this affordance is offered regardless of status.</summary>
        public bool EditDueDate { get; private init; }

        public bool Any => Assign || Unassign || StartWork || PutOnHold || Resume || Resolve || Close || Reopen;

        public bool AnyEdit => EditPriority || EditCategory || EditTeam || EditDueDate;

        public static WorkflowAffordances For(TicketDetail ticket, CurrentUser user)
        {
            // AssigneeId is not carried on the detail DTO; self-assignment is the only assignment
            // this page offers, so the snapshot's assignee is only needed for the policy's
            // "is the caller the assignee" test, which self-assign already answers.
            var snapshot = new TicketAuthorizationSnapshot(
                ticket.Id,
                ticket.TeamId,
                ticket.RequesterId,
                ticket.AssigneeId,
                ticket.Status);

            var canTransition = TicketAccessPolicy.CanTransition(snapshot, user);
            var canSelfAssign = TicketAccessPolicy.CanAssign(snapshot, user, user.UserId);
            var canAssignOther = user.Role == UserRole.Admin || (user.Role == UserRole.Manager && user.ManagedTeamIds.Contains(ticket.TeamId));
            var isTerminal = ticket.Status is Status.Resolved or Status.Closed;

            return new WorkflowAffordances
            {
                Assign = canSelfAssign && ticket.Status == Status.Open,
                AssignToOther = canAssignOther && ticket.Status == Status.Open,
                Unassign = canTransition && ticket.Status == Status.Assigned,
                StartWork = canTransition && ticket.Status == Status.Assigned,
                PutOnHold = canTransition && ticket.Status is Status.Assigned or Status.InProgress,
                Resume = canTransition && ticket.Status == Status.Pending,
                Resolve = canTransition && ticket.Status is Status.InProgress or Status.Pending,
                Close = TicketAccessPolicy.CanClose(snapshot, user) && ticket.Status == Status.Resolved,
                Reopen = TicketAccessPolicy.CanReopen(snapshot, user) && ticket.Status is Status.Resolved or Status.Closed,
                // Commenting is not gated by status — TICKET-ENT-05 places no status restriction
                // on it, unlike every workflow transition above.
                Comment = TicketAccessPolicy.CanComment(snapshot, user),
                EditPriority = canTransition && !isTerminal,
                EditCategory = canTransition && !isTerminal,
                EditTeam = canTransition && !isTerminal,
                EditDueDate = canTransition,
            };
        }
    }

    public sealed class WorkflowInput
    {
        [Display(Name = "Reason")]
        [StringLength(500)]
        public string? PendingReason { get; set; }

        [Display(Name = "Resolution")]
        public Resolution ResolutionCode { get; set; } = Resolution.Fixed;

        [Display(Name = "Resolution notes")]
        [StringLength(4000)]
        public string? ResolutionNotes { get; set; }

        [Display(Name = "Reason for reopening")]
        [StringLength(500)]
        public string? ReopenReason { get; set; }

        // Phase F1-B: mirrors Ticket.AddComment's own MaxCommentLength bound (8000, the same limit
        // Description already uses) — the domain rule is authoritative; this is only the same
        // early client/model-validation courtesy every other bounded field on this form already gets.
        [Display(Name = "Comment")]
        [StringLength(8000)]
        public string? CommentBody { get; set; }

        [Display(Name = "Internal comment (not visible to Viewers)")]
        public bool CommentIsInternal { get; set; }

        // ---- Phase 30C (ADR-0033): ticket field edits ----

        [Display(Name = "Priority")]
        public Priority NewPriority { get; set; }

        [Display(Name = "Category")]
        public int NewCategoryId { get; set; }

        /// <summary>The team + category picker for "Change team" — its own team becomes the
        /// ticket's new team; see <see cref="TicketService.ChangeTeamAsync"/>'s own doc comment.</summary>
        [Display(Name = "Team / Category")]
        public int NewTeamCategoryId { get; set; }

        /// <summary>A blank date input binds to <see langword="null"/> — the "clear the due date"
        /// case <c>ChangeDueDateAsync</c> already accepts, no separate control needed. Bound as a
        /// plain <see cref="DateTime"/> for the same reason <c>Create.cshtml.cs</c>'s own
        /// <c>DueDate</c> is — a <c>datetime-local</c> input carries no offset at all — and
        /// converted the same way, via <see cref="ToUtc"/>.</summary>
        [Display(Name = "Due date")]
        public DateTime? NewDueDate { get; set; }
    }
}
