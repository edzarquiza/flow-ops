using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace FlowOps.Infrastructure.Email;

/// <summary>
/// Phase 30 (ADR-0035): the Production sender (<c>FlowOps:Email:Provider = "Resend"</c>) — a plain
/// <see cref="HttpClient"/> call against Resend's REST API (<c>POST /emails</c>). No SDK package:
/// Resend's API is one small JSON POST, and <see cref="HttpClient"/>/<c>System.Text.Json</c> (both
/// already part of the shared framework) are all it needs — adding a dependency for this would be
/// the "infrastructure merely because it is possible" this phase's own scope rule forbids.
/// </summary>
public sealed class ResendEmailSender : IEmailSender
{
    private readonly HttpClient _httpClient;
    private readonly EmailOptions _options;
    private readonly ILogger<ResendEmailSender> _logger;

    public ResendEmailSender(HttpClient httpClient, EmailOptions options, ILogger<ResendEmailSender> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
    }

    public async Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        var payload = new ResendRequest(
            From: $"{_options.FromName} <{_options.FromAddress}>",
            To: [message.ToEmail],
            Subject: message.Subject,
            Html: message.HtmlBody,
            Text: message.TextBody);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "emails") { Content = JsonContent.Create(payload) };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey);

            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return EmailSendResult.Success();
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            // The recipient address and subject are logged (both already ordinary application
            // data elsewhere), but never the API key and never the message body — the same
            // "log what identifies the operation, never the content" discipline CLAUDE.md §13
            // already applies to every other logged action.
            _logger.LogWarning(
                "Email delivery failed: Resend returned {StatusCode} for {ToEmail}. {ResponseBody}",
                (int)response.StatusCode,
                message.ToEmail,
                body);
            return EmailSendResult.Failed($"Resend returned {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Email delivery failed: could not reach Resend for {ToEmail}.", message.ToEmail);
            return EmailSendResult.Failed("Could not reach the email provider.");
        }
    }

    private sealed record ResendRequest(
        [property: JsonPropertyName("from")] string From,
        [property: JsonPropertyName("to")] IReadOnlyList<string> To,
        [property: JsonPropertyName("subject")] string Subject,
        [property: JsonPropertyName("html")] string Html,
        [property: JsonPropertyName("text")] string Text);
}
