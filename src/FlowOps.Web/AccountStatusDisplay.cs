using FlowOps.Domain.Accounts;

namespace FlowOps.Web;

/// <summary>Presentation-only: how an <see cref="AccountStatus"/> is labeled and colored across
/// every <c>/Platform/Users</c> view. Decides nothing about authorization — purely display.</summary>
public static class AccountStatusDisplay
{
    public static string Label(AccountStatus status) => status switch
    {
        AccountStatus.Pending => "Pending approval",
        AccountStatus.Active => "Active",
        AccountStatus.Inactive => "Inactive",
        AccountStatus.Rejected => "Rejected",
        _ => status.ToString(),
    };

    /// <summary>Phase 24A: a <c>.fo-status-dot--*</c> modifier class rather than a raw
    /// <c>style="background:...">-able colour string — CSP's <c>style-src 'self'</c> blocks the
    /// <c>style</c> attribute outright, so colour must come from a class, never an inline value.</summary>
    public static string DotClass(AccountStatus status) => status switch
    {
        AccountStatus.Pending => "fo-status-dot--warn",
        AccountStatus.Active => "fo-status-dot--ok",
        AccountStatus.Inactive => "fo-status-dot--muted",
        AccountStatus.Rejected => "fo-status-dot--danger",
        _ => "fo-status-dot--muted",
    };
}
