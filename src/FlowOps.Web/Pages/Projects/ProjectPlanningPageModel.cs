using FlowOps.Application.Planning;
using FlowOps.Application.Tickets;
using FlowOps.Domain;
using FlowOps.Domain.Planning;
using FlowOps.Domain.Tickets;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FlowOps.Web.Pages.Projects;

/// <summary>
/// Shared plumbing for the three project planning pages (Overview, Board, Tickets), ADR-0029.
/// Every mutation handler follows the same shape as <c>Tickets/Details</c>: resolve the caller,
/// call exactly one existing application method, redirect back. No rule is decided here — status
/// changes call the same <see cref="TicketService"/> transitions the ticket page uses; sprint moves
/// and lifecycle go through <see cref="TicketService"/>/<see cref="SprintService"/>, which
/// authorize server-side. Hiding a menu item is an affordance only.
/// </summary>
public abstract class ProjectPlanningPageModel : PageModel
{
    private readonly CurrentUserAccessor _currentUserAccessor;
    private readonly TicketService _ticketService;
    private readonly SprintService _sprintService;

    protected ProjectPlanningPageModel(
        CurrentUserAccessor currentUserAccessor,
        TicketService ticketService,
        SprintService sprintService)
    {
        _currentUserAccessor = currentUserAccessor;
        _ticketService = ticketService;
        _sprintService = sprintService;
    }

    protected CurrentUserAccessor CurrentUsers => _currentUserAccessor;

    /// <summary>A rejected planning action (invalid dates, completed sprint, ...) survives the
    /// post-redirect-get in TempData and is shown as a danger notice.</summary>
    [TempData]
    public string? PlanningError { get; set; }

    [TempData]
    public string? PlanningNotice { get; set; }

    public bool CanManageSprints { get; protected set; }

    /// <summary>The resolved caller, for the views that decide which actions to offer.</summary>
    public CurrentUser Caller { get; protected set; } = null!;

    public Task<IActionResult> OnPostMoveToSprintAsync(int id, int ticketId, int? sprintId, string? returnUrl, CancellationToken cancellationToken = default) =>
        RunAsync(id, returnUrl, user => _ticketService.MoveToSprintAsync(ticketId, sprintId, user, cancellationToken), cancellationToken);

    public Task<IActionResult> OnPostPullToBoardAsync(int id, int ticketId, string? returnUrl, CancellationToken cancellationToken = default) =>
        RunAsync(id, returnUrl, user => _ticketService.PullFromSprintBacklogAsync(ticketId, user, cancellationToken), cancellationToken);

    public Task<IActionResult> OnPostAssignMeAsync(int id, int ticketId, string? returnUrl, CancellationToken cancellationToken = default) =>
        RunAsync(id, returnUrl, user => _ticketService.AssignAsync(ticketId, user.UserId, user, cancellationToken), cancellationToken);

    public Task<IActionResult> OnPostStartWorkAsync(int id, int ticketId, string? returnUrl, CancellationToken cancellationToken = default) =>
        RunAsync(id, returnUrl, user => _ticketService.StartWorkAsync(ticketId, user, cancellationToken), cancellationToken);

    public Task<IActionResult> OnPostResumeAsync(int id, int ticketId, string? returnUrl, CancellationToken cancellationToken = default) =>
        RunAsync(id, returnUrl, user => _ticketService.ResumeAsync(ticketId, user, cancellationToken), cancellationToken);

    public Task<IActionResult> OnPostCloseAsync(int id, int ticketId, string? returnUrl, CancellationToken cancellationToken = default) =>
        RunAsync(id, returnUrl, user => _ticketService.CloseAsync(ticketId, user, cancellationToken), cancellationToken);

    /// <summary>The Create Sprint form's values survive a rejected submit (post-redirect-get), so a
    /// validation error never costs the user what they typed.</summary>
    [TempData]
    public string? SprintFormName { get; set; }

    [TempData]
    public string? SprintFormStart { get; set; }

    [TempData]
    public string? SprintFormEnd { get; set; }

    // Held as yyyyMMdd, never yyyy-MM-dd: the cookie TempData provider round-trips a date-looking
    // string as a DateTime, which then cannot be assigned back to a string property.
    public string? SprintFormStartIso => ToIso(SprintFormStart);

    public string? SprintFormEndIso => ToIso(SprintFormEnd);

    private static string? ToIso(string? compact) =>
        DateOnly.TryParseExact(compact, "yyyyMMdd", out var date) ? date.ToString("yyyy-MM-dd") : null;

    public async Task<IActionResult> OnPostCreateSprintAsync(int id, string? name, DateOnly? start, DateOnly? end, string? returnUrl, CancellationToken cancellationToken = default)
    {
        var result = await RunSprintAsync(id, returnUrl, "Sprint created.", user =>
            start is null || end is null
                ? Task.FromResult(SprintMutationResult.Failed("Enter a start and an end date."))
                : _sprintService.CreateSprintAsync(user, id, name ?? string.Empty, start.Value, end.Value, cancellationToken),
            cancellationToken);

        if (PlanningError is not null)
        {
            SprintFormName = name;
            SprintFormStart = start?.ToString("yyyyMMdd");
            SprintFormEnd = end?.ToString("yyyyMMdd");
        }

        return result;
    }

    public Task<IActionResult> OnPostCancelSprintAsync(int id, int sprintId, string? returnUrl, CancellationToken cancellationToken = default) =>
        RunSprintAsync(id, returnUrl, "Sprint cancelled. It stays in the sprint history.", user => _sprintService.CancelSprintAsync(user, sprintId, cancellationToken), cancellationToken);

    /// <summary>ADR-0030: the explicit carry-forward of a completed sprint's unfinished tickets.</summary>
    public async Task<IActionResult> OnPostCarryForwardAsync(int id, int sprintId, string? returnUrl, CancellationToken cancellationToken = default)
    {
        var user = await _currentUserAccessor.GetCurrentUserAsync(User, cancellationToken);
        if (user is null)
        {
            return Forbid();
        }

        try
        {
            var result = await _sprintService.CarryForwardAsync(user, sprintId, cancellationToken);
            if (result.Succeeded)
            {
                PlanningNotice = result.Moved == 0
                    ? "No unfinished tickets were moved."
                    : $"Moved {result.Moved} unfinished ticket{(result.Moved == 1 ? "" : "s")} to the current sprint."
                        + (result.Skipped > 0 ? $" {result.Skipped} you can't plan {(result.Skipped == 1 ? "was" : "were")} left where {(result.Skipped == 1 ? "it is" : "they are")}." : string.Empty);
            }
            else
            {
                PlanningError = result.Error;
            }
        }
        catch (PlanningAccessDeniedException)
        {
            return Forbid();
        }

        return SafeRedirect(id, returnUrl);
    }

    /// <summary>A drag on the board (or its explicit menu twin), ADR-0030: the same existing
    /// operations, authorized and validated server-side; the page is then re-rendered from the
    /// server — nothing is applied optimistically in the browser.</summary>
    public Task<IActionResult> OnPostBoardMoveAsync(int id, int ticketId, BoardColumnKey target, string? reason, Resolution? resolutionCode, string? resolutionNotes, string? returnUrl, CancellationToken cancellationToken = default) =>
        RunAsync(id, returnUrl, user => _ticketService.MoveOnBoardAsync(ticketId, target, new BoardMoveInput(reason, resolutionCode, resolutionNotes), user, cancellationToken), cancellationToken);

    public Task<IActionResult> OnPostStartSprintAsync(int id, int sprintId, string? returnUrl, CancellationToken cancellationToken = default) =>
        RunSprintAsync(id, returnUrl, "Sprint started.", user => _sprintService.StartSprintAsync(user, sprintId, cancellationToken), cancellationToken);

    public Task<IActionResult> OnPostCompleteSprintAsync(int id, int sprintId, string? returnUrl, CancellationToken cancellationToken = default) =>
        RunSprintAsync(
            id,
            returnUrl,
            "Sprint completed. Unfinished tickets remain in its history — move them to another sprint from Project Tickets, or from the sprint's page.",
            user => _sprintService.CompleteSprintAsync(user, sprintId, cancellationToken),
            cancellationToken);

    private async Task<IActionResult> RunAsync(int projectId, string? returnUrl, Func<CurrentUser, Task> action, CancellationToken cancellationToken)
    {
        var user = await _currentUserAccessor.GetCurrentUserAsync(User, cancellationToken);
        if (user is null)
        {
            return Forbid();
        }

        try
        {
            await action(user);
        }
        catch (TicketAccessDeniedException)
        {
            return Forbid();
        }
        catch (DomainRuleException ex)
        {
            // The rule code is developer-facing: users see the message, the log keeps the code.
            HttpContext.RequestServices.GetRequiredService<ILogger<ProjectPlanningPageModel>>()
                .LogInformation("Planning action in project {ProjectId} rejected by rule {RuleCode}: {Message}", projectId, ex.RuleCode, ex.Message);
            PlanningError = ex.Message;
        }
        catch (DbUpdateConcurrencyException)
        {
            PlanningError = "This ticket changed while you were working on it. Reload the page and try again.";
        }

        return SafeRedirect(projectId, returnUrl);
    }

    private async Task<IActionResult> RunSprintAsync(
        int projectId,
        string? returnUrl,
        string successMessage,
        Func<CurrentUser, Task<SprintMutationResult>> action,
        CancellationToken cancellationToken)
    {
        var user = await _currentUserAccessor.GetCurrentUserAsync(User, cancellationToken);
        if (user is null)
        {
            return Forbid();
        }

        try
        {
            var result = await action(user);
            if (result.Succeeded)
            {
                PlanningNotice = successMessage;
            }
            else
            {
                PlanningError = result.Error;
            }
        }
        catch (PlanningAccessDeniedException)
        {
            return Forbid();
        }

        return SafeRedirect(projectId, returnUrl);
    }

    /// <summary>Back to where the action was taken (filters and page preserved) — only ever a
    /// local URL, so a forged <c>returnUrl</c> can never become an open redirect.</summary>
    private IActionResult SafeRedirect(int projectId, string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToPage("/Projects/Details", new { id = projectId });
}
