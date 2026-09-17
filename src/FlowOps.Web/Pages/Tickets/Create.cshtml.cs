using System.ComponentModel.DataAnnotations;
using FlowOps.Application.Tickets;
using FlowOps.Domain;
using FlowOps.Domain.Directory;
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
    private readonly WorkspaceSetupService _workspaceSetupService;

    public CreateModel(CurrentUserAccessor currentUserAccessor, TicketQueryService ticketQueryService, TicketService ticketService, WorkspaceSetupService workspaceSetupService)
    {
        _currentUserAccessor = currentUserAccessor;
        _ticketQueryService = ticketQueryService;
        _ticketService = ticketService;
        _workspaceSetupService = workspaceSetupService;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public TicketCreationOptions Options { get; private set; } = new([], []);

    /// <summary>True when the caller has no eligible team+category to file a ticket against yet —
    /// Phase 22 (PG-3): the form is hidden in favor of an explanatory empty state, rather than
    /// silently rendering with an empty Category dropdown.</summary>
    public bool WorkspaceNotReady => Options.Teams.Count == 0;

    /// <summary>Whether the caller can actually perform the setup this empty state points to —
    /// never shown to a non-Admin, who cannot reach <c>/Admin</c> anyway (CLAUDE.md §6.1).</summary>
    public bool CanSetUpWorkspace { get; private set; }

    /// <summary>ADR-0026: non-null only for an Admin once this org already has at least one
    /// ticket (so this page's own setup step is not what's currently outstanding — a fresh visit
    /// while filing the org's first ticket never shows this) and another setup step still isn't
    /// done — see <c>_SetupNextStep.cshtml</c>.</summary>
    public SetupNextStepViewModel? NextStep { get; private set; }

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
        CanSetUpWorkspace = DirectoryAccessPolicy.CanManageTeams(user);

        if (user.Role == UserRole.Admin)
        {
            var status = await _workspaceSetupService.GetWorkspaceSetupStatusAsync(user, cancellationToken);
            if (SetupSteps.IsStepDone("ticket", status) && SetupSteps.FirstIncomplete(status) is { } next)
            {
                NextStep = new SetupNextStepViewModel(next);
            }
        }

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
        CanSetUpWorkspace = DirectoryAccessPolicy.CanManageTeams(user);

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
            Input.ProjectId,
            ToUtc(Input.PlannedStartDate),
            ToUtc(Input.DueDate));

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
            // than an exception page (CLAUDE.md §11.3/§11.4 tier 3). Covers TICKET-INV-11 (planned
            // start after due date) the same way it already covers every other invariant here.
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
    }

    /// <summary>
    /// Phase 25 §5: FlowOps has no organization/user timezone concept (confirmed by inspection) —
    /// every other timestamp in this codebase is UTC (TICKET-INV-10), so a native
    /// <c>datetime-local</c> input's offset-less value is deliberately treated as a UTC wall-clock
    /// reading rather than the server's local timezone (the .NET default for an offset-less
    /// <see cref="DateTimeOffset"/> parse), which would silently depend on where the process
    /// happens to be deployed. This is a documented limitation, not a full timezone system.
    /// </summary>
    private static DateTimeOffset? ToUtc(DateTime? value) =>
        value is { } v ? new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)) : null;

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

        // Phase 25: planning facts, not SLA fields (TICKET-INV-11 is the only rule linking them —
        // when both are given, start must not be after due). Both optional; TICKET-INV-11's
        // rejection surfaces as the same inline DomainRuleException-driven error every other
        // invariant on this page already uses, not a client-side [Compare]-style attribute, since
        // the comparison needs the actual invariant's wording (and stays correct if it ever
        // changes) rather than a duplicated one here.
        [Display(Name = "Planned start (optional)")]
        [DataType(DataType.DateTime)]
        public DateTime? PlannedStartDate { get; set; }

        [Display(Name = "Due date (optional)")]
        [DataType(DataType.DateTime)]
        public DateTime? DueDate { get; set; }
    }
}
