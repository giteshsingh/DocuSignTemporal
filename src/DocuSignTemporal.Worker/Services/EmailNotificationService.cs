using DocuSignTemporal.Core.Models;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Mail;

namespace DocuSignTemporal.Worker.Services;

public interface IEmailNotificationService
{
    Task SendSigningCompletedAsync(DocumentSigningResult result);
    Task SendBatchCompletedAsync(BatchSigningResult result, string notifyEmail);
}

public class EmailNotificationService : IEmailNotificationService
{
    private readonly EmailOptions _options;

    public EmailNotificationService(IOptions<EmailOptions> options)
    {
        _options = options.Value;
    }

    public async Task SendSigningCompletedAsync(DocumentSigningResult result)
    {
        if (string.IsNullOrEmpty(_options.NotifyEmail)) return;

        var subject = result.Status == SigningStatus.Completed
            ? $"✅ Document Signed: {result.DocumentType}"
            : $"⚠️ Signing Update: {result.DocumentType} — {result.Status}";

        var signers = result.SignerCompletions.Count > 0
            ? string.Join("\n", result.SignerCompletions
                .Select(s => $"  • {s.Name} ({s.Email}): {s.Status} at {s.SignedAt:u}"))
            : "  (No signer details available)";

        var body = $"""
            Document Signing Notification
            ==============================
            Document Type : {result.DocumentType}
            Envelope ID   : {result.EnvelopeId}
            Status        : {result.Status}
            Completed At  : {result.CompletedAt:u}

            Signers:
            {signers}

            {(result.Status == SigningStatus.Completed
                ? "The signed document is available. Download it via the API:\nGET /api/signing/{result.RequestId}/document"
                : $"Note: {result.ErrorMessage}")}
            """;

        await SendEmailAsync(subject, body);
    }

    public async Task SendBatchCompletedAsync(BatchSigningResult result, string notifyEmail)
    {
        var email = notifyEmail.Length > 0 ? notifyEmail : _options.NotifyEmail;
        if (string.IsNullOrEmpty(email)) return;

        var subject = $"📋 Batch Signing Complete: {result.CompletedDocuments}/{result.TotalDocuments} documents signed";

        var lines = result.Results.Select(r =>
            $"  [{(r.Status == SigningStatus.Completed ? "✓" : "✗")}] {r.DocumentType,-35} {r.Status}");

        var body = $"""
            Batch Signing Summary
            ======================
            Batch ID   : {result.BatchId}
            Total Docs : {result.TotalDocuments}
            Completed  : {result.CompletedDocuments}
            Failed     : {result.FailedDocuments}
            Finished   : {result.CompletedAt:u}

            Document Results:
            {string.Join("\n", lines)}
            """;

        await SendEmailAsync(subject, body, email);
    }

    private async Task SendEmailAsync(string subject, string body, string? toOverride = null)
    {
        using var client = new SmtpClient(_options.SmtpHost, _options.SmtpPort)
        {
            EnableSsl = true,
            Credentials = new NetworkCredential(_options.SmtpUser, _options.SmtpPassword)
        };

        var message = new MailMessage(
            from: _options.FromEmail,
            to: toOverride ?? _options.NotifyEmail,
            subject: subject,
            body: body);

        await client.SendMailAsync(message);
    }
}

public class EmailOptions
{
    public string SmtpHost { get; set; } = "smtp.gmail.com";
    public int SmtpPort { get; set; } = 587;
    public string SmtpUser { get; set; } = string.Empty;
    public string SmtpPassword { get; set; } = string.Empty;
    public string FromEmail { get; set; } = string.Empty;
    public string NotifyEmail { get; set; } = string.Empty;
}
