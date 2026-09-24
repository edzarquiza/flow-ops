namespace FlowOps.Infrastructure.Email;

/// <summary>
/// Phase 30 (ADR-0035): `FlowOps:Email` configuration, bound and validated at startup
/// (<c>ValidateOnStart</c>) exactly like <c>DemoOptions</c>/<c>AttentionOptions</c> already are —
/// the app fails fast on a misconfigured deployment rather than at the first email attempt.
/// </summary>
public sealed class EmailOptions
{
    /// <summary><c>"Log"</c> (the default — Development/Test/CI, sends nothing) or <c>"Resend"</c>
    /// (Production).</summary>
    public string Provider { get; init; } = "Log";

    /// <summary>Required only when <see cref="Provider"/> is <c>"Resend"</c> — a secret, supplied
    /// only via environment variable/user-secrets, never committed (CLAUDE.md §13).</summary>
    public string? ApiKey { get; init; }

    public string FromAddress { get; init; } = "no-reply@flowops.local";

    public string FromName { get; init; } = "FlowOps";

    /// <summary>
    /// The one trusted source for every link an email contains (ADR-0035 Decision 2) — Application
    /// combines this with a known, fixed application-relative path (e.g. <c>/Tickets/Details/{id}</c>)
    /// to build a link. Never derived from a request's Host header, forwarded headers, or any other
    /// client-supplied input, unlike <c>Url.Page</c>'s own per-request scheme/host resolution — this
    /// is deliberately independent of that mechanism, since email composition happens outside any
    /// HTTP request context.
    /// </summary>
    public string BaseUrl { get; init; } = "http://localhost:8080";
}
