using FlowOps.Infrastructure.Email;

namespace FlowOps.Application.Tests.Email;

/// <summary>
/// Phase 30 (ADR-0035): the test double every Application.Tests class uses in place of a real
/// <see cref="IEmailSender"/> — records every message it was asked to send, for direct assertion,
/// with no network call and no dependency on Resend (CI never talks to a real provider). Mirrors
/// <c>TicketTestData.FixedTimeProvider</c>'s own "small, caller-inspectable fake" shape.
/// </summary>
internal sealed class RecordingEmailSender : IEmailSender
{
    private readonly List<EmailMessage> _sent = [];

    /// <summary>When set, every subsequent <see cref="SendAsync"/> call fails (returns
    /// <see cref="EmailSendResult.Failed(string)"/>) instead of recording the message — for
    /// proving a send failure never rolls back the business operation that triggered it.</summary>
    public bool FailNextSends { get; set; }

    public IReadOnlyList<EmailMessage> SentMessages => _sent;

    /// <summary>Resets recorded messages mid-test, so a test can seed state (e.g. an initial
    /// assignment) through the same service and then assert only on what a later action sent.</summary>
    public void Clear() => _sent.Clear();

    public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        if (FailNextSends)
        {
            return Task.FromResult(EmailSendResult.Failed("Simulated failure for a test."));
        }

        _sent.Add(message);
        return Task.FromResult(EmailSendResult.Success());
    }
}

/// <summary>
/// Phase 30: the throwaway email dependencies most existing tests pass without caring about email
/// at all — <see cref="Sender"/> hands back a fresh, unshared <see cref="RecordingEmailSender"/>
/// every time (so one test constructing several services never has them silently share a mailbox),
/// while <see cref="Options"/> is one shared, immutable set of harmless defaults. A test that
/// actually wants to assert on sent email constructs its own <see cref="RecordingEmailSender"/>
/// directly instead of using this.
/// </summary>
internal static class TestEmail
{
    public static RecordingEmailSender Sender => new();

    public static readonly EmailOptions Options = new()
    {
        Provider = "Log",
        FromAddress = "no-reply@flowops.test",
        FromName = "FlowOps",
        BaseUrl = "https://flowops.test",
    };
}
