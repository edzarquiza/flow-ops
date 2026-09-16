using System.ComponentModel.DataAnnotations;
using FlowOps.Application.Accounts;
using FlowOps.Application.Demo;
using FlowOps.Application.Organizations;
using FlowOps.Application.Tickets;
using FlowOps.Domain.Organizations;
using FlowOps.Domain.Tickets;
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

    public MembersModel(CurrentUserAccessor currentUserAccessor, MembershipService membershipService, InvitationService invitationService)
    {
        _currentUserAccessor = currentUserAccessor;
        _membershipService = membershipService;
        _invitationService = invitationService;
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

    public string? StatusMessage { get; set; }

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
            var result = await _invitationService.CreateInvitationAsync(user, new CreateInvitationRequest(InviteInput.Email, InviteInput.Role), cancellationToken);
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

    private async Task ReloadAsync(CurrentUser user, CancellationToken cancellationToken)
    {
        Members = await _membershipService.GetMembersAsync(user, Search, cancellationToken);
        AssignableRoles = ComputeAssignableRoles(user);
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
    }
}
