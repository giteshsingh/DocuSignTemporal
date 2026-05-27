using DocuSignTemporal.Core.Interfaces;
using DocuSignTemporal.Core.Models;
using Temporalio.Workflows;
using Microsoft.Extensions.Logging;

namespace DocuSignTemporal.Worker.Workflows;

/// <summary>
/// Orchestrates the full lifecycle of a single document signing request:
/// 1. Generate PDF from template with dynamic attributes
/// 2. Create DocuSign envelope and send to signers
/// 3. Wait for signing completion via webhook signals
/// 4. Download signed document and notify stakeholders
/// </summary>
[Workflow]
public class DocumentSigningWorkflow : IDocumentSigningWorkflow
{
    private SigningStatus _currentStatus = SigningStatus.Pending;
    private DocuSignWebhookEvent? _lastEvent;
    private readonly TaskCompletionSource<DocuSignWebhookEvent> _signingCompleted = new();

    [WorkflowRun]
    public async Task<DocumentSigningResult> RunAsync(DocumentSigningRequest request)
    {
        var logger = Workflow.Logger;
        logger.LogInformation(
            "Starting DocumentSigningWorkflow for {DocumentType} | RequestId: {RequestId}",
            request.DocumentType, request.RequestId);

        var activityOptions = new ActivityOptions
        {
            StartToCloseTimeout = TimeSpan.FromMinutes(5),
            RetryPolicy = new Temporalio.Common.RetryPolicy
            {
                MaximumAttempts = 3,
                InitialInterval = TimeSpan.FromSeconds(2),
                BackoffCoefficient = 2,
                MaximumInterval = TimeSpan.FromSeconds(30)
            }
        };

        try
        {
            // ── Step 1: Generate PDF ──────────────────────────────────────────
            _currentStatus = SigningStatus.Pending;
            logger.LogInformation("Generating PDF for {DocumentType}", request.DocumentType);

            var pdfBase64 = await Workflow.ExecuteActivityAsync(
                (IDocuSignActivities act) => act.GeneratePdfFromTemplateAsync(request),
                activityOptions);

            // ── Step 2: Create DocuSign Envelope ──────────────────────────────
            logger.LogInformation("Creating DocuSign envelope for RequestId: {RequestId}", request.RequestId);

            var envelopeId = await Workflow.ExecuteActivityAsync(
                (IDocuSignActivities act) => act.CreateDocuSignEnvelopeAsync(request, pdfBase64),
                activityOptions);

            _currentStatus = SigningStatus.Sent;
            logger.LogInformation("Envelope created: {EnvelopeId}", envelopeId);

            // ── Step 3: Wait for Signing (with expiration timeout) ────────────
            var expirationTimeout = TimeSpan.FromDays(request.ExpirationDays);
            logger.LogInformation(
                "Waiting for signing completion. Envelope: {EnvelopeId}, Timeout: {Days}d",
                envelopeId, request.ExpirationDays);

            var signingEvent = await WaitForSigningWithTimeoutAsync(envelopeId, expirationTimeout);

            if (signingEvent is null)
            {
                // Timed out — void the envelope
                await Workflow.ExecuteActivityAsync(
                    (IDocuSignActivities act) => act.VoidEnvelopeAsync(envelopeId, "Signing period expired"),
                    activityOptions);

                _currentStatus = SigningStatus.Expired;
                return BuildFailureResult(request.RequestId, envelopeId, request.DocumentType,
                    SigningStatus.Expired, "Signing period expired");
            }

            // Map event status
            _currentStatus = MapStatus(signingEvent.Status);

            if (_currentStatus is SigningStatus.Declined or SigningStatus.Voided)
            {
                return BuildFailureResult(request.RequestId, envelopeId, request.DocumentType,
                    _currentStatus, $"Document was {signingEvent.Status}");
            }

            // ── Step 4: Download Signed Document ──────────────────────────────
            logger.LogInformation("Downloading signed document for envelope {EnvelopeId}", envelopeId);

            var signedDocBase64 = await Workflow.ExecuteActivityAsync(
                (IDocuSignActivities act) => act.DownloadSignedDocumentAsync(envelopeId),
                activityOptions);

            // ── Step 5: Send Notification ─────────────────────────────────────
            var result = new DocumentSigningResult
            {
                RequestId = request.RequestId,
                EnvelopeId = envelopeId,
                DocumentType = request.DocumentType,
                Status = SigningStatus.Completed,
                CompletedAt = Workflow.UtcNow,
                SignedDocumentBase64 = signedDocBase64,
                SignerCompletions = signingEvent.RecipientEvents
                    .Select(r => new SignerCompletionInfo(r.Email, r.Email, r.SignedDateTime, r.Status))
                    .ToList()
            };

            await Workflow.ExecuteActivityAsync(
                (IDocuSignActivities act) => act.SendCompletionNotificationAsync(result),
                activityOptions);

            _currentStatus = SigningStatus.Completed;
            logger.LogInformation("DocumentSigningWorkflow completed for envelope {EnvelopeId}", envelopeId);

            return result;
        }
        catch (Exception ex)
        {
            _currentStatus = SigningStatus.Failed;
            logger.LogError(ex, "DocumentSigningWorkflow failed for RequestId: {RequestId}", request.RequestId);
            return BuildFailureResult(request.RequestId, string.Empty, request.DocumentType,
                SigningStatus.Failed, ex.Message);
        }
    }

    [WorkflowSignal]
    public Task HandleDocuSignEventAsync(DocuSignWebhookEvent evt)
    {
        _lastEvent = evt;
        var status = MapStatus(evt.Status);

        if (status is SigningStatus.Completed or SigningStatus.Declined or SigningStatus.Voided)
        {
            _signingCompleted.TrySetResult(evt);
        }

        return Task.CompletedTask;
    }

    [WorkflowQuery]
    public SigningStatus GetCurrentStatus() => _currentStatus;

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<DocuSignWebhookEvent?> WaitForSigningWithTimeoutAsync(
        string envelopeId, TimeSpan timeout)
    {
        // Poll every 5 minutes as a fallback, but primarily rely on webhook signals
        var pollDeadline = Workflow.UtcNow.Add(timeout);

        while (Workflow.UtcNow < pollDeadline)
        {
            // Wait up to 5 minutes OR until a signal arrives
            var waitTask = _signingCompleted.Task;
            var delayTask = Workflow.DelayAsync(TimeSpan.FromMinutes(5));

            if (_signingCompleted.Task.IsCompleted)
                return await _signingCompleted.Task;

            await Task.WhenAny(waitTask, delayTask);

            if (_signingCompleted.Task.IsCompleted)
                return await _signingCompleted.Task;

            // Fallback poll (handles missed webhooks)
            var polledStatus = await Workflow.ExecuteActivityAsync(
                (IDocuSignActivities act) => act.GetEnvelopeStatusAsync(envelopeId),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromMinutes(2) });

            if (polledStatus is SigningStatus.Completed or SigningStatus.Declined or SigningStatus.Voided)
            {
                _currentStatus = polledStatus;
                return _lastEvent ?? new DocuSignWebhookEvent
                {
                    EnvelopeId = envelopeId,
                    Status = polledStatus.ToString(),
                    EventTime = Workflow.UtcNow
                };
            }
        }

        return null; // timed out
    }

    private static SigningStatus MapStatus(string status) => status.ToLower() switch
    {
        "completed" => SigningStatus.Completed,
        "declined" => SigningStatus.Declined,
        "voided" => SigningStatus.Voided,
        "delivered" => SigningStatus.Delivered,
        "sent" => SigningStatus.Sent,
        _ => SigningStatus.Pending
    };

    private static DocumentSigningResult BuildFailureResult(
        string requestId, string envelopeId, DocumentType docType,
        SigningStatus status, string error) => new()
    {
        RequestId = requestId,
        EnvelopeId = envelopeId,
        DocumentType = docType,
        Status = status,
        CompletedAt = DateTime.UtcNow,
        ErrorMessage = error
    };
}
