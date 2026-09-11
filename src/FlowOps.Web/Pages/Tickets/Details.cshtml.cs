using System.ComponentModel.DataAnnotations;
using FlowOps.Application.Tickets;
using FlowOps.Domain;
using FlowOps.Domain.Tickets;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

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

    public DetailsModel(
        CurrentUserAccessor currentUserAccessor,
        TicketQueryService ticketQueryService,
        TicketService ticketService)
    {
        _currentUserAccessor = currentUserAccessor;
        _ticketQueryService = ticketQueryService;
        _ticketService = ticketService;
    }

    public TicketDetail Ticket { get; private set; } = null!;

    public IReadOnlyList<TicketTimelineEntry> History { get; private set; } = [];

    /// <summary>Which workflow actions to offer. A UX affordance only — the service re-decides.</summary>
    public WorkflowAffordances Actions { get; private set; } = new();

    [BindProperty]
    public WorkflowInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken = default) =>
        await LoadOrNotFoundAsync(id, cancellationToken);

    public Task<IActionResult> OnPostAssignAsync(int id, CancellationToken cancellationToken = default) =>
        RunAsync(id, user => _ticketService.AssignAsync(id, user.UserId, user, cancellationToken), cancellationToken);

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
            // an unhandled exception page. The rule code is shown so the message is traceable.
            ModelState.AddModelError(string.Empty, $"{ex.Message} ({ex.RuleCode})");
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
        return Page();
    }

    /// <summary>
    /// Which buttons to render. Each combines the same <see cref="TicketAccessPolicy"/> call the
    /// service will make with the status the transition is legal from, so the page does not offer
    /// an action that is certain to fail. It grants nothing: the service authorizes independently.
    /// </summary>
    public sealed class WorkflowAffordances
    {
        public bool Assign { get; private init; }

        public bool Unassign { get; private init; }

        public bool StartWork { get; private init; }

        public bool PutOnHold { get; private init; }

        public bool Resume { get; private init; }

        public bool Resolve { get; private init; }

        public bool Close { get; private init; }

        public bool Reopen { get; private init; }

        public bool Comment { get; private init; }

        public bool Any => Assign || Unassign || StartWork || PutOnHold || Resume || Resolve || Close || Reopen;

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

            return new WorkflowAffordances
            {
                Assign = canSelfAssign && ticket.Status == Status.Open,
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

        // No [StringLength]: TICKET-ENT-05 / Ticket.AddComment enforce only "non-blank" — unlike
        // Ticket.Description (TICKET-INV-01's 8000-char limit), no maximum comment length is
        // documented anywhere in the contract, so none is invented here.
        [Display(Name = "Comment")]
        public string? CommentBody { get; set; }

        [Display(Name = "Internal comment (not visible to Viewers)")]
        public bool CommentIsInternal { get; set; }
    }
}
