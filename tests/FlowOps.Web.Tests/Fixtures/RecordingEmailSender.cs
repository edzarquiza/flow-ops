using FlowOps.Infrastructure.Email;

namespace FlowOps.Web.Tests.Fixtures;

/// <summary>
/// Phase 30 (ADR-0035): the Web.Tests twin of Application.Tests' own <c>RecordingEmailSender</c> —
/// records every message it was asked to send, no network call, no dependency on Resend. Kept as
/// its own small copy here rather than a cross-project reference (the same reasoning
/// <c>PlaywrightAppFactory</c>'s own doc comment already gives for not sharing test-only seed
/// helpers between the two test projects).
/// </summary>
internal sealed class RecordingEmailSender : IEmailSender
{
    private readonly List<EmailMessage> _sent = [];

    public bool FailNextSends { get; set; }

    public IReadOnlyList<EmailMessage> SentMessages => _sent;

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

/// <summary>The throwaway email dependencies most Web.Tests classes pass without caring about
/// email at all — see Application.Tests' own <c>TestEmail</c> for the identical rationale.</summary>
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
