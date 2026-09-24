using System.ComponentModel.DataAnnotations;
using FlowOps.Application.Platform;
using FlowOps.Domain.Tickets;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FlowOps.Web.Pages.Platform.Sla;

/// <summary>
/// Phase 30D (ADR-0034): Platform-level SLA configuration administration. Admin-only in the
/// platform sense — gated by <see cref="PlatformUserAccessor"/>, the same as every other
/// <c>/Platform</c> page, never <c>OrganizationMembership</c> (a Platform Admin need not belong to
/// any organization at all). Deliberately global: this screen edits the one shared SLA table every
/// organization's tickets resolve against (ADR-0015).
/// </summary>
public sealed class IndexModel : PageModel
{
    private readonly PlatformUserAccessor _platformUserAccessor;
    private readonly SlaConfigurationService _slaConfigurationService;

    public IndexModel(PlatformUserAccessor platformUserAccessor, SlaConfigurationService slaConfigurationService)
    {
        _platformUserAccessor = platformUserAccessor;
        _slaConfigurationService = slaConfigurationService;
    }

    public IReadOnlyList<SlaConfigurationListItem> Configurations { get; private set; } = [];

    [BindProperty]
    public CreateOverrideInputModel CreateOverrideInput { get; set; } = new();

    public string? StatusMessage { get; set; }

    /// <summary>Phase 24A's own convention: which .notice tone StatusMessage renders in — false
    /// (the default) for an ordinary confirmation, true when it is actually result.Error from a
    /// failed mutation.</summary>
    public bool StatusIsError { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var admin = await _platformUserAccessor.GetCurrentPlatformAdminAsync(User, cancellationToken);
        if (admin is null)
        {
            return Forbid();
        }

        Configurations = await _slaConfigurationService.GetAllAsync(admin, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostUpdateAsync(int id, int targetMinutes, int riskThresholdPercent, CancellationToken cancellationToken)
    {
        var admin = await _platformUserAccessor.GetCurrentPlatformAdminAsync(User, cancellationToken);
        if (admin is null)
        {
            return Forbid();
        }

        var result = await _slaConfigurationService.UpdateAsync(admin, id, targetMinutes, riskThresholdPercent, cancellationToken);
        StatusMessage = result.Succeeded ? "SLA configuration updated." : result.Error;
        StatusIsError = !result.Succeeded;

        return await ReloadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        var admin = await _platformUserAccessor.GetCurrentPlatformAdminAsync(User, cancellationToken);
        if (admin is null)
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            Configurations = await _slaConfigurationService.GetAllAsync(admin, cancellationToken);
            return Page();
        }

        var result = await _slaConfigurationService.CreateOverrideAsync(
            admin,
            CreateOverrideInput.WorkType,
            CreateOverrideInput.Priority,
            CreateOverrideInput.TargetMinutes,
            CreateOverrideInput.RiskThresholdPercent,
            cancellationToken);

        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.Error!);
            Configurations = await _slaConfigurationService.GetAllAsync(admin, cancellationToken);
            return Page();
        }

        StatusMessage = "SLA override added.";
        CreateOverrideInput = new CreateOverrideInputModel();
        return await ReloadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id, CancellationToken cancellationToken)
    {
        var admin = await _platformUserAccessor.GetCurrentPlatformAdminAsync(User, cancellationToken);
        if (admin is null)
        {
            return Forbid();
        }

        var result = await _slaConfigurationService.DeleteOverrideAsync(admin, id, cancellationToken);
        StatusMessage = result.Succeeded ? "SLA override removed." : result.Error;
        StatusIsError = !result.Succeeded;

        return await ReloadAsync(cancellationToken);
    }

    private async Task<IActionResult> ReloadAsync(CancellationToken cancellationToken)
    {
        // Re-resolving admin rather than threading it through every caller: this only ever runs
        // right after one of the handlers above already confirmed it non-null, so the extra
        // lookup is cheap and keeps this helper's own signature simple.
        var admin = await _platformUserAccessor.GetCurrentPlatformAdminAsync(User, cancellationToken);
        if (admin is null)
        {
            return Forbid();
        }

        Configurations = await _slaConfigurationService.GetAllAsync(admin, cancellationToken);
        return Page();
    }

    public sealed class CreateOverrideInputModel
    {
        [Required]
        [Display(Name = "Work type")]
        public WorkType WorkType { get; set; } = FlowOps.Domain.Tickets.WorkType.Incident;

        [Required]
        [Display(Name = "Priority")]
        public Priority Priority { get; set; } = FlowOps.Domain.Tickets.Priority.Medium;

        [Required]
        [Range(SlaConfigurationService.MinTargetMinutes, int.MaxValue, ErrorMessage = "Target minutes must be at least 1.")]
        [Display(Name = "Target minutes")]
        public int TargetMinutes { get; set; } = 480;

        [Required]
        [Range(SlaConfigurationService.MinRiskThresholdPercent, SlaConfigurationService.MaxRiskThresholdPercent, ErrorMessage = "Risk threshold percent must be between 1 and 100.")]
        [Display(Name = "Risk threshold percent")]
        public int RiskThresholdPercent { get; set; } = 80;
    }
}
