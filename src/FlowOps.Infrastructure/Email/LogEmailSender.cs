using Microsoft.Extensions.Logging;

namespace FlowOps.Infrastructure.Email;

/// <summary>
/// Phase 30 (ADR-0035): the default sender for Development/Test (<c>FlowOps:Email:Provider</c>
/// unset or <c>"Log"</c>) — sends nothing, over any network, ever. Writes one clear
/// <c>LogInformation</c> line naming the recipient and subject, so local development is fully
/// usable with zero email credentials, and so the log is never mistaken for "delivered": the
/// message text says "not sent," not "sent." Also what CI runs against — see this ADR's own
/// "never depend on Resend in automated tests" decision.
/// </summary>
public sealed class LogEmailSender : IEmailSender
{
    private readonly ILogger<LogEmailSender> _logger;

    public LogEmailSender(ILogger<LogEmailSender> logger) => _logger = logger;

    public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Email not sent (Provider=Log): to {ToEmail}, subject {Subject}.",
            message.ToEmail,
            message.Subject);

        return Task.FromResult(EmailSendResult.Success());
    }
}
