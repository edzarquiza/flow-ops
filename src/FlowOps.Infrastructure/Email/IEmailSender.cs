namespace FlowOps.Infrastructure.Email;

/// <summary>
/// Phase 30 (ADR-0035): the one abstraction Application uses to send a transactional email —
/// deliberately narrow (send this one message), never a generic notification framework
/// (<c>INotificationService</c>/<c>IEventBus</c>/<c>IMessageBroker</c> were all considered and
/// rejected; see the ADR). Application composes the message; this interface only ever transports
/// an already-fully-composed one. Every implementation must not throw for an ordinary delivery
/// failure — it returns instead, and the caller (an Application service) decides how to react
/// (log a warning, never roll back the business operation that already committed).
/// </summary>
public interface IEmailSender
{
    Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// One outgoing email. Plain data — no template engine, no HTML framework. <paramref name="HtmlBody"/>
/// and <paramref name="TextBody"/> are both sent when supported (Resend accepts both in the same
/// request) so a plain-text mail client still renders something readable.
/// </summary>
public sealed record EmailMessage(
    string ToEmail,
    string? ToName,
    string Subject,
    string TextBody,
    string HtmlBody);

/// <summary>
/// The outcome of one send attempt. Never an exception for an ordinary delivery failure (a bad
/// API response, a network error) — those come back as <see cref="Succeeded"/> = <see langword="false"/>
/// with <see cref="Error"/> describing why, so the caller's own try/catch is reserved for genuinely
/// unexpected failures, matching every other "expected failure is a value, not an exception" shape
/// already used throughout this codebase (<c>TeamMutationResult</c>, <c>PlatformMutationResult</c>, …).
/// </summary>
public sealed record EmailSendResult(bool Succeeded, string? Error)
{
    public static EmailSendResult Success() => new(true, null);

    public static EmailSendResult Failed(string error) => new(false, error);
}
