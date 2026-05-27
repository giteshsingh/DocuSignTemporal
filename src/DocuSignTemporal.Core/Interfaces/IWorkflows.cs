using DocuSignTemporal.Core.Models;
using Temporalio.Activities;
using Temporalio.Workflows;

namespace DocuSignTemporal.Core.Interfaces;

// ─── Workflow Interfaces ───────────────────────────────────────────────────────

[Workflow]
public interface IDocumentSigningWorkflow
{
    [WorkflowRun]
    Task<DocumentSigningResult> RunAsync(DocumentSigningRequest request);

    [WorkflowSignal]
    Task HandleDocuSignEventAsync(DocuSignWebhookEvent evt);

    [WorkflowQuery]
    SigningStatus GetCurrentStatus();
}

[Workflow]
public interface IBatchSigningWorkflow
{
    [WorkflowRun]
    Task<BatchSigningResult> RunAsync(BatchSigningRequest request);

    [WorkflowQuery]
    BatchSigningResult GetCurrentProgress();
}

// ─── Activity Interfaces ──────────────────────────────────────────────────────


public interface IDocuSignActivities
{
    [Activity]
    Task<string> GeneratePdfFromTemplateAsync(DocumentSigningRequest request);

    [Activity]
    Task<string> CreateDocuSignEnvelopeAsync(DocumentSigningRequest request, string pdfBase64);

    [Activity]
    Task<SigningStatus> GetEnvelopeStatusAsync(string envelopeId);

    [Activity]
    Task<string> DownloadSignedDocumentAsync(string envelopeId);

    [Activity]
    Task SendCompletionNotificationAsync(DocumentSigningResult result);

    [Activity]
    Task<bool> VoidEnvelopeAsync(string envelopeId, string reason);
}
