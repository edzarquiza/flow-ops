using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FlowOps.Web.Pages;

/// <summary>
/// Phase 12 (§11.3): the single friendly landing page for <c>UseExceptionHandler("/Error")</c> —
/// every unhandled exception, from any page, ends up here. Reachable anonymously: the exception
/// that got a caller here may have happened before authentication even ran.
/// </summary>
/// <remarks>
/// Carries no exception detail whatsoever — not the message, not the type, not a stack trace —
/// only <see cref="HttpContext.TraceIdentifier"/>, ASP.NET Core's own per-request correlation id,
/// which <c>UseExceptionHandler</c>'s built-in logging already attaches to the server-side log
/// entry for this same request, so an operator can find the two by matching this one value.
/// </remarks>
[AllowAnonymous]
public sealed class ErrorModel : PageModel
{
    public string RequestId { get; private set; } = string.Empty;

    public void OnGet()
    {
        RequestId = HttpContext.TraceIdentifier;
    }
}
