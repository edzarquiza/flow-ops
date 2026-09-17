using System.ComponentModel.DataAnnotations;
using FlowOps.Application.Accounts;
using FlowOps.Application.Demo;
using FlowOps.Application.Directory;
using FlowOps.Application.Organizations;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Organizations;
using FlowOps.Domain.Tickets;
using FlowOps.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FlowOps.Web.Pages.Organization;

/// <summary>
/// Phase 18: the current organization's member list, invitations, role changes, and removal — all
/// organization-scoped by construction, since every operation goes through
/// <see cref="CurrentUserAccessor"/>'s server-resolved <c>CurrentUser</c>, never a client-supplied
/// organization id (Step 7/16).
/// </summary>
public sealed class MembersModel : PageModel
{
    private readonly CurrentUserAccessor _currentUserAccessor;
    private readonly MembershipService _membershipService;
    private readonly InvitationService _invitationService;
    private readonly WorkspaceSetupService _workspaceSetupService;
    private readonly TeamService _teamService;
    private readonly UserManager<ApplicationUser> _userManager;

    public MembersModel(
        CurrentUserAccessor currentUserAccessor,
        MembershipService membershipService,
        InvitationService invitationService,
        WorkspaceSetupService workspaceSetupService,
        TeamService teamService,
        UserManager<ApplicationUser> userManager)
    {
        _currentUserAccessor = currentUserAccessor;
        _membershipService = membershipService;
        _invitationService = invitationService;
        _workspaceSetupService = workspaceSetupService;
        _teamService = teamService;
        _userManager = userManager;
    }

    [BindProperty]
    public InviteInputModel InviteInput { get; set; } = new();

    /// <summary>The active search term. Bound from the query string on GET and from a hidden
    /// field on every mutating form on this page (Invite/ChangeRole/RemoveMember), so a search
    /// stays active across a role change or removal instead of silently resetting to "no search".</summary>
    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    public IReadOnlyList<MemberListItem> Members { get; private set; } = [];

    public IReadOnlyList<UserRole> AssignableRoles { get; private set; } = [];

    public string? CreatedInvitationLink { get; private set; }

    /// <summary>ADR-0027: active teams the caller may invite into — empty for a role that cannot
    /// invite at all (the form itself is hidden then), populated for Admin/Manager.</summary>
    public IReadOnlyList<TeamListItem> InvitableTeams { get; private set; } = [];

    /// <summary>Admin-generated password-reset link for a member, shown once (same "generate,
    /// Admin copies, never emailed" pattern as <see cref="CreatedInvitationLink"/>).</summary>
    public string? CreatedResetLink { get; private set; }

    public string? CreatedResetLinkForDisplayName { get; private set; }

    /// <summary>Whether the "Reset password" row action should render — Admin-only (see
    /// <see cref="OnPostGenerateResetLinkAsync"/>'s own remarks).</summary>
    public bool CallerIsAdmin { get; private set; }

    public string? StatusMessage { get; set; }

    /// <summary>ADR-0026: non-null only for an Admin once this page's own step (an invitation has
    /// been sent, or a second member already joined) is done and another setup step still isn't —
    /// see <c>_SetupNextStep.cshtml</c>. Always null for a Manager, since only an Admin acts on
    /// setup items (ADR-0020).</summary>
    public SetupNextStepViewModel? NextStep { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var user = await _currentUserAccessor.GetCurrentUserAsync(User, cancellationToken);
        if (user is null)
        {
            return Forbid();
        }

        try
        {
            Members = await _membershipService.GetMembersAsync(user, Search, cancellationToken);
        }
        catch (OrganizationAccessDeniedException)
        {
            return Forbid();
        }

        AssignableRoles = ComputeAssignableRoles(user);
        InvitableTeams = await GetInvitableTeamsAsync(user, cancellationToken);
        CallerIsAdmin = user.Role == UserRole.Admin;
        NextStep = await ResolveNextStepAsync(user, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostInviteAsync(CancellationToken cancellationToken)
    {
        var user = await _currentUserAccessor.GetCurrentUserAsync(User, cancellationToken);
        if (user is null)
        {
            return Forbid();
        }

        ModelState.Clear(); // isolate this form's own validation — see Settings page's identical note
        if (!TryValidateModel(InviteInput, nameof(InviteInput)))
        {
            await ReloadAsync(user, cancellationToken);
            return Page();
        }

        try
        {
            var result = await _invitationService.CreateInvitationAsync(user, new CreateInvitationRequest(InviteInput.Email, InviteInput.Role, InviteInput.TeamId), cancellationToken);
            if (!result.Succeeded)
            {
                foreach (var error in result.Errors)
                {
                    ModelState.AddModelError(string.Empty, error);
                }

                await ReloadAsync(user, cancellationToken);
                return Page();
            }

            CreatedInvitationLink = Url.Page("/Account/AcceptInvitation", pageHandler: null, values: new { token = result.RawToken }, protocol: Request.Scheme);
        }
        catch (OrganizationAccessDeniedException)
        {
            return Forbid();
        }

        await ReloadAsync(user, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostChangeRoleAsync(Guid targetUserId, UserRole newRole, CancellationToken cancellationToken)
    {
        var user = await _currentUserAccessor.GetCurrentUserAsync(User, cancellationToken);
        if (user is null)
        {
            return Forbid();
        }

        try
        {
            var result = await _membershipService.ChangeRoleAsync(user, targetUserId, newRole, cancellationToken);
            ModelState.Clear();
            if (!result.Succeeded)
            {
                ModelState.AddModelError(string.Empty, result.Error!);
            }
            else
            {
                StatusMessage = "Member role updated.";
            }
        }
        catch (OrganizationAccessDeniedException)
        {
            return Forbid();
        }
        catch (DemoProtectedAccountException ex)
        {
            ModelState.Clear();
            ModelState.AddModelError(string.Empty, ex.Message);
        }

        await ReloadAsync(user, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostRemoveMemberAsync(Guid targetUserId, CancellationToken cancellationToken)
    {
        var user = await _currentUserAccessor.GetCurrentUserAsync(User, cancellationToken);
        if (user is null)
        {
            return Forbid();
        }

        try
        {
            var result = await _membershipService.RemoveMemberAsync(user, targetUserId, cancellationToken);
            ModelState.Clear();
            if (!result.Succeeded)
            {
                ModelState.AddModelError(string.Empty, result.Error!);
            }
            else
            {
                StatusMessage = "Member removed from this organization.";
            }
        }
        catch (OrganizationAccessDeniedException)
        {
            return Forbid();
        }
        catch (DemoProtectedAccountException ex)
        {
            ModelState.Clear();
            ModelState.AddModelError(string.Empty, ex.Message);
        }

        await ReloadAsync(user, cancellationToken);
        return Page();
    }

    /// <summary>
    /// ADR-0027: generates a one-time password-reset link for a member. Deliberately Admin-only —
    /// a more conservative gate than Manager's ordinary "invite/manage Agents and Viewers"
    /// authority, since handing out a reset link is direct access to that account, not merely
    /// managing its membership. Uses ASP.NET Core Identity's own built-in
    /// <see cref="UserManager{TUser}.GeneratePasswordResetTokenAsync"/> — no custom token scheme.
    /// The link itself is never emailed (no email capability exists in this app); the Admin copies
    /// and sends it out-of-band, the same UX as inviting a member.
    /// </summary>
    public async Task<IActionResult> OnPostGenerateResetLinkAsync(Guid targetUserId, CancellationToken cancellationToken)
    {
        var user = await _currentUserAccessor.GetCurrentUserAsync(User, cancellationToken);
        if (user is null || user.Role != UserRole.Admin)
        {
            return Forbid();
        }

        var targetMembership = await _membershipService.GetMembersAsync(user, search: null, cancellationToken);
        var target = targetMembership.FirstOrDefault(m => m.UserId == targetUserId);
        if (target is null)
        {
            return Forbid();
        }

        var targetUser = await _userManager.FindByIdAsync(targetUserId.ToString());
        if (targetUser is null)
        {
            return Forbid();
        }

        var token = await _userManager.GeneratePasswordResetTokenAsync(targetUser);
        CreatedResetLink = Url.Page("/Account/ResetPassword", pageHandler: null, values: new { userId = targetUser.Id, token }, protocol: Request.Scheme);
        CreatedResetLinkForDisplayName = target.DisplayName;

        await ReloadAsync(user, cancellationToken);
        return Page();
    }

    private async Task ReloadAsync(CurrentUser user, CancellationToken cancellationToken)
    {
        Members = await _membershipService.GetMembersAsync(user, Search, cancellationToken);
        AssignableRoles = ComputeAssignableRoles(user);
        InvitableTeams = await GetInvitableTeamsAsync(user, cancellationToken);
        CallerIsAdmin = user.Role == UserRole.Admin;
        NextStep = await ResolveNextStepAsync(user, cancellationToken);
    }

    private async Task<IReadOnlyList<TeamListItem>> GetInvitableTeamsAsync(CurrentUser user, CancellationToken cancellationToken)
    {
        if (!OrganizationAccessPolicy.CanInvite(user))
        {
            return [];
        }

        return await _teamService.GetActiveTeamsForInviteAsync(user, cancellationToken);
    }

    private async Task<SetupNextStepViewModel?> ResolveNextStepAsync(CurrentUser user, CancellationToken cancellationToken)
    {
        if (user.Role != UserRole.Admin)
        {
            return null;
        }

        var status = await _workspaceSetupService.GetWorkspaceSetupStatusAsync(user, cancellationToken);
        if (!SetupSteps.IsStepDone("invite", status))
        {
            return null;
        }

        var next = SetupSteps.FirstIncomplete(status);
        return next is null ? null : new SetupNextStepViewModel(next);
    }

    private static IReadOnlyList<UserRole> ComputeAssignableRoles(CurrentUser user) =>
        Enum.GetValues<UserRole>().Where(role => OrganizationAccessPolicy.CanInviteRole(user, role)).ToList();

    public sealed class InviteInputModel
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        [Display(Name = "Role")]
        public UserRole Role { get; set; } = UserRole.Agent;

        [Display(Name = "Team (optional)")]
        public int? TeamId { get; set; }
    }
}
