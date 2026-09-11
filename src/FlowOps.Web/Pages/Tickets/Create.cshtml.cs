using System.ComponentModel.DataAnnotations;
using FlowOps.Application.Tickets;
using FlowOps.Domain;
using FlowOps.Domain.Tickets;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FlowOps.Web.Pages.Tickets;

/// <summary>
/// Ticket creation. The requester is always the authenticated caller, and reference, timestamps,
/// status, and every SLA field are server-owned — none of them are bound from the request
/// (CLAUDE.md §12).
/// </summary>
/// <remarks>
/// Phase 11 (C-3): the form binds only <see cref="InputModel.CategoryId"/> — there is no
/// user-facing Team field at all, because a category belongs to exactly one team
/// (TICKET-INV-02), so the team is looked up from the chosen category rather than typed
/// separately. This removes the raw-numeric-id usability defect without touching
/// <see cref="CreateTicketRequest"/>'s shape or <see cref="TicketService.CreateAsync"/>'s
/// authorization: <see cref="TicketAccessPolicy.CanCreate"/> and TICKET-INV-02 are exactly as
/// authoritative as before this page existed.
/// </remarks>
public sealed class CreateModel : PageModel
{
    private readonly CurrentUserAccessor _currentUserAccessor;
    private readonly TicketQueryService _ticketQueryService;
    private readonly TicketService _ticketService;

    public CreateModel(CurrentUserAccessor currentUserAccessor, TicketQueryService ticketQueryService, TicketService ticketService)
    {
        _currentUserAccessor = currentUserAccessor;
        _ticketQueryService = ticketQueryService;
        _ticketService = ticketService;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public TicketCreationOptions Options { get; private set; } = new([]);

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken = default)
    {
        var user = await _currentUserAccessor.GetCurrentUserAsync(User, cancellationToken);
        if (user is null)
        {
            return Forbid();
        }

        // The same centralized policy the service enforces (AUTH-RULE-04) — calling it, not
        // restating it. Hiding the form is an affordance; TicketService is the boundary.
        if (!TicketAccessPolicy.CanCreate(user))
        {
            return Forbid();
        }

        Options = await _ticketQueryService.GetCreationOptionsAsync(user, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken = default)
    {
        var user = await _currentUserAccessor.GetCurrentUserAsync(User, cancellationToken);
        if (user is null)
        {
            return Forbid();
        }

        // Re-fetched fresh rather than trusted from a hidden field: the caller's eligible
        // teams/categories could have changed since the form was rendered, and this is also what
        // the page needs to re-render if validation fails below.
        Options = await _ticketQueryService.GetCreationOptionsAsync(user, cancellationToken);

        if (!ModelState.IsValid)
        {
            return Page();
        }

        // The only place TeamId is decided: whichever team owns the chosen category, per
        // TICKET-INV-02. A tampered or stale CategoryId that matches none of the caller's options
        // is rejected here as a form error — a stricter, UI-level convenience check that sits in
        // front of (and does not replace) TicketService.CreateAsync's own TICKET-INV-02 check.
        var teamId = Options.Teams
            .Where(t => t.Categories.Any(c => c.CategoryId == Input.CategoryId))
            .Select(t => (int?)t.TeamId)
            .FirstOrDefault();

        if (teamId is null)
        {
            ModelState.AddModelError(nameof(Input.CategoryId), "Select a valid category.");
            return Page();
        }

        var request = new CreateTicketRequest(
            Input.Title,
            Input.Description,
            Input.WorkType,
            Input.Priority,
            teamId.Value,
            Input.CategoryId,
            Input.ProjectId);

        try
        {
            var (id, _) = await _ticketService.CreateAsync(request, user, cancellationToken);
            return RedirectToPage("/Tickets/Details", new { id });
        }
        catch (TicketAccessDeniedException)
        {
            // 403 via the cookie scheme's AccessDenied path (CLAUDE.md §11.3).
            return Forbid();
        }
        catch (DomainRuleException ex)
        {
            // A domain invariant rejected the input — surfaced as an inline form error rather
            // than an exception page (CLAUDE.md §11.3/§11.4 tier 3).
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }

    public sealed class InputModel
    {
        [Required]
        [StringLength(200, MinimumLength = 5, ErrorMessage = "Title must be between 5 and 200 characters.")]
        public string Title { get; set; } = string.Empty;

        [Required]
        [StringLength(8000, ErrorMessage = "Description must be at most 8000 characters.")]
        [DataType(DataType.MultilineText)]
        public string Description { get; set; } = string.Empty;

        [Required]
        [Display(Name = "Work type")]
        public WorkType WorkType { get; set; } = WorkType.Incident;

        [Required]
        public Priority Priority { get; set; } = Priority.Medium;

        // No TeamId here: the team is derived server-side from the chosen category (see
        // OnPostAsync) — a category belongs to exactly one team (TICKET-INV-02), so asking the
        // user to pick both independently would only reintroduce the chance of a mismatch.
        [Required]
        [Display(Name = "Category")]
        public int CategoryId { get; set; }

        [Display(Name = "Project (optional)")]
        public int? ProjectId { get; set; }
    }
}
