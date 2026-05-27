using DocuSignTemporal.Core.Interfaces;
using DocuSignTemporal.Core.Models;
using DocuSign.eSign.Api;
using DocuSign.eSign.Client;
using DocuSign.eSign.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Temporalio.Activities;
using DocuSignTemporal.Worker.Services;

namespace DocuSignTemporal.Worker.Activities;

/// <summary>
/// Implements all DocuSign API interactions as Temporal activities.
/// Each method is idempotent and handles its own error propagation.
/// </summary>
public class DocuSignActivities : IDocuSignActivities
{
    private readonly DocuSignOptions _options;
    private readonly ILogger<DocuSignActivities> _logger;
    private readonly IPdfGeneratorService _pdfGenerator;
    private readonly IEmailNotificationService _emailService;

    public DocuSignActivities(
        IOptions<DocuSignOptions> options,
        ILogger<DocuSignActivities> logger,
        IPdfGeneratorService pdfGenerator,
        IEmailNotificationService emailService)
    {
        _options = options.Value;
        _logger = logger;
        _pdfGenerator = pdfGenerator;
        _emailService = emailService;
    }

    // ── Activity: Generate PDF ────────────────────────────────────────────────

    [Activity]
    public async Task<string> GeneratePdfFromTemplateAsync(DocumentSigningRequest request)
    {
        _logger.LogInformation(
            "Generating PDF for {DocumentType} with {AttrCount} attributes",
            request.DocumentType, request.Attributes.Count);

        var pdfBytes = await _pdfGenerator.GenerateAsync(
            request.DocumentType,
            request.Attributes,
            request.DocumentName);

        return Convert.ToBase64String(pdfBytes);
    }

    // ── Activity: Create DocuSign Envelope ───────────────────────────────────

    [Activity]
    public async Task<string> CreateDocuSignEnvelopeAsync(
        DocumentSigningRequest request, string pdfBase64)
    {
        _logger.LogInformation(
            "Creating DocuSign envelope for {DocumentType} | RequestId: {RequestId}",
            request.DocumentType, request.RequestId);

        var apiClient = await GetAuthenticatedClientAsync();
        var envelopesApi = new EnvelopesApi(apiClient);

        // Build document
        var document = new Document
        {
            DocumentBase64 = pdfBase64,
            Name = request.DocumentName,
            FileExtension = "pdf",
            DocumentId = "1"
        };

        // Build recipients
        var signers = request.Signers.Select((signer, i) =>
        {
            var tabs = BuildSigningTabs(request.DocumentType);
            return new Signer
            {
                Email = signer.Email,
                Name = signer.Name,
                RecipientId = (i + 1).ToString(),
                RoutingOrder = signer.RoutingOrder.ToString(),
                Tabs = tabs,
                ClientUserId = null // null = remote signing via email
            };
        }).ToList();

        // Build envelope
        var envelopeDefinition = new EnvelopeDefinition
        {
            EmailSubject = request.EmailSubject,
            EmailBlurb = request.EmailBody,
            Documents = new List<Document> { document },
            Recipients = new Recipients { Signers = signers },
            Status = "sent", // "sent" immediately dispatches; "created" = draft

            // Custom metadata for correlation
            CustomFields = new CustomFields
            {
                TextCustomFields = new List<TextCustomField>
                {
                    new() { Name = "RequestId", Value = request.RequestId, Show = "false" },
                    new() { Name = "DocumentType", Value = request.DocumentType.ToString(), Show = "false" }
                }
            },

            // Event notifications (webhook)
            EventNotification = BuildEventNotification(request.NotificationWebhookUrl)
        };

        var summary = await envelopesApi.CreateEnvelopeAsync(_options.AccountId, envelopeDefinition);

        _logger.LogInformation(
            "Envelope created: {EnvelopeId} | Status: {Status}",
            summary.EnvelopeId, summary.Status);

        return summary.EnvelopeId;
    }

    // ── Activity: Poll Envelope Status ───────────────────────────────────────

    [Activity]
    public async Task<SigningStatus> GetEnvelopeStatusAsync(string envelopeId)
    {
        var apiClient = await GetAuthenticatedClientAsync();
        var envelopesApi = new EnvelopesApi(apiClient);

        var envelope = await envelopesApi.GetEnvelopeAsync(_options.AccountId, envelopeId);

        _logger.LogInformation(
            "Envelope {EnvelopeId} status: {Status}", envelopeId, envelope.Status);

        return envelope.Status?.ToLower() switch
        {
            "completed" => SigningStatus.Completed,
            "declined" => SigningStatus.Declined,
            "voided" => SigningStatus.Voided,
            "delivered" => SigningStatus.Delivered,
            "sent" => SigningStatus.Sent,
            _ => SigningStatus.Pending
        };
    }

    // ── Activity: Download Signed Document ───────────────────────────────────

    [Activity]
    public async Task<string> DownloadSignedDocumentAsync(string envelopeId)
    {
        _logger.LogInformation("Downloading signed document for envelope {EnvelopeId}", envelopeId);

        var apiClient = await GetAuthenticatedClientAsync();
        var envelopesApi = new EnvelopesApi(apiClient);

        // Download the combined signed PDF
        using var docStream = await envelopesApi.GetDocumentAsync(
            _options.AccountId, envelopeId, "combined");

        using var ms = new MemoryStream();
        await docStream.CopyToAsync(ms);
        var base64 = Convert.ToBase64String(ms.ToArray());

        _logger.LogInformation(
            "Downloaded signed document for envelope {EnvelopeId} | Size: {Bytes} bytes",
            envelopeId, ms.Length);

        return base64;
    }

    // ── Activity: Send Completion Notification ────────────────────────────────

    [Activity]
    public async Task SendCompletionNotificationAsync(DocumentSigningResult result)
    {
        _logger.LogInformation(
            "Sending completion notification for {DocumentType} | Envelope: {EnvelopeId}",
            result.DocumentType, result.EnvelopeId);

        await _emailService.SendSigningCompletedAsync(result);
    }

    // ── Activity: Void Envelope ───────────────────────────────────────────────

    [Activity]
    public async Task<bool> VoidEnvelopeAsync(string envelopeId, string reason)
    {
        try
        {
            var apiClient = await GetAuthenticatedClientAsync();
            var envelopesApi = new EnvelopesApi(apiClient);

            await envelopesApi.UpdateAsync(_options.AccountId, envelopeId, new Envelope
            {
                Status = "voided",
                VoidedReason = reason
            });

            _logger.LogInformation("Voided envelope {EnvelopeId}: {Reason}", envelopeId, reason);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to void envelope {EnvelopeId}", envelopeId);
            return false;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<ApiClient> GetAuthenticatedClientAsync()
    {
        var apiClient = new ApiClient(_options.BaseUrl);

        // JWT Grant Authentication
        var tokenResponse = apiClient.RequestJWTUserToken(
            _options.IntegrationKey,
            _options.UserId,
            _options.AuthServer,
            System.Text.Encoding.UTF8.GetBytes(_options.RsaPrivateKey),
            1); // 1 hour token

        apiClient.Configuration.DefaultHeader["Authorization"] = $"Bearer {tokenResponse.access_token}";
        return apiClient;
    }

    /// <summary>
    /// Builds document-type-specific signing tabs (signature fields, date fields, initials).
    /// </summary>
    private static Tabs BuildSigningTabs(DocumentType documentType)
    {
        return documentType switch
        {
            DocumentType.NDA => new Tabs
            {
                SignHereTabs = new List<SignHere>
                {
                    new() { AnchorString = "/sig1/", AnchorXOffset = "0", AnchorYOffset = "0" },
                    new() { AnchorString = "/sig2/", AnchorXOffset = "0", AnchorYOffset = "0" }
                },
                DateSignedTabs = new List<DateSigned>
                {
                    new() { AnchorString = "/date1/", AnchorXOffset = "0" }
                },
                InitialHereTabs = new List<InitialHere>
                {
                    new() { AnchorString = "/init1/" }
                }
            },

            DocumentType.EmploymentContract => new Tabs
            {
                SignHereTabs = new List<SignHere>
                {
                    new() { AnchorString = "/employee_sig/", AnchorXOffset = "0" },
                    new() { AnchorString = "/employer_sig/", AnchorXOffset = "0" }
                },
                DateSignedTabs = new List<DateSigned>
                {
                    new() { AnchorString = "/emp_date/", AnchorXOffset = "0" }
                }
            },

            DocumentType.TaxForm or DocumentType.ComplianceForm => new Tabs
            {
                SignHereTabs = new List<SignHere>
                {
                    new() { AnchorString = "/signature/", AnchorXOffset = "0" }
                },
                DateSignedTabs = new List<DateSigned>
                {
                    new() { AnchorString = "/sign_date/", AnchorXOffset = "0" }
                },
                CheckboxTabs = new List<Checkbox>
                {
                    new() { AnchorString = "/certify/", TabLabel = "CertifyCheckbox" }
                }
            },

            // Default: single signature + date
            _ => new Tabs
            {
                SignHereTabs = new List<SignHere>
                {
                    new() { AnchorString = "/signature/", AnchorXOffset = "0", AnchorYOffset = "0" }
                },
                DateSignedTabs = new List<DateSigned>
                {
                    new() { AnchorString = "/date_signed/", AnchorXOffset = "0" }
                }
            }
        };
    }

    private static EventNotification BuildEventNotification(string webhookUrl)
    {
        if (string.IsNullOrEmpty(webhookUrl))
            return null!;

        return new EventNotification
        {
            Url = webhookUrl,
            LoggingEnabled = "true",
            RequireAcknowledgment = "true",
            UseSoapInterface = "false",
            IncludeCertificateWithSoap = "false",
            SignMessageWithX509Cert = "false",
            IncludeDocuments = "true",
            IncludeEnvelopeVoidReason = "true",
            IncludeTimeZone = "true",
            IncludeSenderAccountAsCustomField = "true",
            IncludeDocumentFields = "true",
            IncludeCertificateOfCompletion = "false",
            EnvelopeEvents = new List<EnvelopeEvent>
            {
                new() { EnvelopeEventStatusCode = "sent" },
                new() { EnvelopeEventStatusCode = "delivered" },
                new() { EnvelopeEventStatusCode = "completed" },
                new() { EnvelopeEventStatusCode = "declined" },
                new() { EnvelopeEventStatusCode = "voided" }
            },
            RecipientEvents = new List<DocuSign.eSign.Model.RecipientEvent>
            {
                new() { RecipientEventStatusCode = "Sent" },
                new() { RecipientEventStatusCode = "Delivered" },
                new() { RecipientEventStatusCode = "Completed" },
                new() { RecipientEventStatusCode = "Declined" }
            }
        };
    }
}
