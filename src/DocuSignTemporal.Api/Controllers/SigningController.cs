using DocuSignTemporal.Core.Models;
using DocuSignTemporal.Worker.Workflows;
using Microsoft.AspNetCore.Mvc;
using Temporalio.Client;
using System.Text.Json;

namespace DocuSignTemporal.Api.Controllers;

[ApiController]
[Route("api/signing")]
[Produces("application/json")]
public class SigningController : ControllerBase
{
    private readonly ITemporalClient _temporal;
    private readonly ILogger<SigningController> _logger;
    private const string TaskQueue = "docusign-signing-queue";

    public SigningController(ITemporalClient temporal, ILogger<SigningController> logger)
    {
        _temporal = temporal;
        _logger = logger;
    }

    // ────────────────────────────────────────────────────────────────────────
    // POST /api/signing/start
    // Start a single document signing workflow
    // ────────────────────────────────────────────────────────────────────────
    [HttpPost("start")]
    [ProducesResponseType(typeof(StartSigningResponse), 202)]
    public async Task<IActionResult> StartSigning([FromBody] DocumentSigningRequest request)
    {
        var workflowId = $"sign-{request.DocumentType}-{request.RequestId}";

        _logger.LogInformation(
            "Starting signing workflow | WorkflowId: {WorkflowId} | Doc: {DocType}",
            workflowId, request.DocumentType);

        var handle = await _temporal.StartWorkflowAsync(
            (DocumentSigningWorkflow wf) => wf.RunAsync(request),
            new WorkflowOptions
            {
                Id = workflowId,
                TaskQueue = TaskQueue
            });

        return Accepted(new StartSigningResponse
        {
            WorkflowId = workflowId,
            RequestId = request.RequestId,
            RunId = handle.ResultRunId,
            StatusUrl = Url.Action(nameof(GetStatus), new { workflowId })!
        });
    }

    // ────────────────────────────────────────────────────────────────────────
    // POST /api/signing/batch
    // Start batch signing of all 13 documents
    // ────────────────────────────────────────────────────────────────────────
    [HttpPost("batch")]
    [ProducesResponseType(typeof(StartSigningResponse), 202)]
    public async Task<IActionResult> StartBatch([FromBody] BatchSigningRequest request)
    {
        var workflowId = $"batch-{request.BatchId}";

        _logger.LogInformation(
            "Starting batch signing workflow | WorkflowId: {WorkflowId} | Docs: {Count}",
            workflowId, request.Documents.Count);

        var handle = await _temporal.StartWorkflowAsync(
            (BatchSigningWorkflow wf) => wf.RunAsync(request),
            new WorkflowOptions
            {
                Id = workflowId,
                TaskQueue = TaskQueue
            });

        return Accepted(new StartSigningResponse
        {
            WorkflowId = workflowId,
            RequestId = request.BatchId,
            RunId = handle.ResultRunId,
            StatusUrl = Url.Action(nameof(GetBatchStatus), new { workflowId })!
        });
    }

    // ────────────────────────────────────────────────────────────────────────
    // GET /api/signing/{workflowId}/status
    // Query current status of a signing workflow
    // ────────────────────────────────────────────────────────────────────────
    [HttpGet("{workflowId}/status")]
    [ProducesResponseType(typeof(SigningStatusResponse), 200)]
    public async Task<IActionResult> GetStatus([FromRoute] string workflowId)
    {
        try
        {
            var handle = _temporal.GetWorkflowHandle<DocumentSigningWorkflow>(workflowId);
            var status = await handle.QueryAsync(wf => wf.GetCurrentStatus());
            return Ok(new SigningStatusResponse { WorkflowId = workflowId, Status = status });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Workflow not found: {WorkflowId}", workflowId);
            return NotFound(new { error = $"Workflow {workflowId} not found" });
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // GET /api/signing/batch/{workflowId}/status
    // ────────────────────────────────────────────────────────────────────────
    [HttpGet("batch/{workflowId}/status")]
    [ProducesResponseType(typeof(BatchSigningResult), 200)]
    public async Task<IActionResult> GetBatchStatus([FromRoute] string workflowId)
    {
        try
        {
            var handle = _temporal.GetWorkflowHandle<BatchSigningWorkflow>(workflowId);
            var progress = await handle.QueryAsync(wf => wf.GetCurrentProgress());
            return Ok(progress);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Batch workflow not found: {WorkflowId}", workflowId);
            return NotFound(new { error = $"Batch workflow {workflowId} not found" });
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // GET /api/signing/{requestId}/document
    // Download the completed signed document
    // ────────────────────────────────────────────────────────────────────────
    [HttpGet("{workflowId}/document")]
    public async Task<IActionResult> GetSignedDocument([FromRoute] string workflowId)
    {
        try
        {
            var handle = _temporal.GetWorkflowHandle(workflowId);
            var result = await handle.GetResultAsync<DocumentSigningResult>();

            if (result.Status != SigningStatus.Completed || string.IsNullOrEmpty(result.SignedDocumentBase64))
                return BadRequest(new { error = "Signed document not yet available", status = result.Status });

            var bytes = Convert.FromBase64String(result.SignedDocumentBase64);
            return File(bytes, "application/pdf", $"signed-{result.DocumentType}-{result.EnvelopeId}.pdf");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not retrieve document for workflow: {WorkflowId}", workflowId);
            return NotFound(new { error = ex.Message });
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // POST /api/signing/webhook/docusign
    // Receives DocuSign Connect webhook events and signals the correct workflow
    // ────────────────────────────────────────────────────────────────────────
    [HttpPost("webhook/docusign")]
    public async Task<IActionResult> DocuSignWebhook([FromBody] JsonElement payload)
    {
        _logger.LogInformation("Received DocuSign webhook event");

        try
        {
            var envelopeId = payload
                .GetProperty("data")
                .GetProperty("envelopeId")
                .GetString() ?? string.Empty;

            var status = payload
                .GetProperty("data")
                .GetProperty("envelopeSummary")
                .GetProperty("status")
                .GetString() ?? string.Empty;

            // Extract custom fields to find our RequestId and correlate to workflow
            var requestId = ExtractCustomField(payload, "RequestId");
            var docTypeStr = ExtractCustomField(payload, "DocumentType");

            if (string.IsNullOrEmpty(requestId) || string.IsNullOrEmpty(docTypeStr))
            {
                _logger.LogWarning("Webhook missing custom fields. EnvelopeId: {Id}", envelopeId);
                return Ok(); // Acknowledge to DocuSign even if we can't correlate
            }

            if (!Enum.TryParse<DocumentType>(docTypeStr, out var docType))
            {
                _logger.LogWarning("Unknown DocumentType in webhook: {Type}", docTypeStr);
                return Ok();
            }

            var workflowId = $"sign-{docType}-{requestId}";

            var evt = new DocuSignWebhookEvent
            {
                EnvelopeId = envelopeId,
                Status = status,
                EventTime = DateTime.UtcNow,
                RecipientEvents = ExtractRecipientEvents(payload)
            };

            _logger.LogInformation(
                "Signalling workflow {WorkflowId} with envelope status: {Status}",
                workflowId, status);

            var handle = _temporal.GetWorkflowHandle<DocumentSigningWorkflow>(workflowId);
            await handle.SignalAsync(wf => wf.HandleDocuSignEventAsync(evt));

            return Ok(new { acknowledged = true, workflowId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing DocuSign webhook");
            return StatusCode(500, new { error = "Webhook processing failed" });
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string ExtractCustomField(JsonElement payload, string fieldName)
    {
        try
        {
            var fields = payload
                .GetProperty("data")
                .GetProperty("envelopeSummary")
                .GetProperty("customFields")
                .GetProperty("textCustomFields");

            foreach (var field in fields.EnumerateArray())
            {
                if (field.GetProperty("name").GetString() == fieldName)
                    return field.GetProperty("value").GetString() ?? string.Empty;
            }
        }
        catch { /* field not present */ }
        return string.Empty;
    }

    private static List<RecipientEvent> ExtractRecipientEvents(JsonElement payload)
    {
        var events = new List<RecipientEvent>();
        try
        {
            var recipients = payload
                .GetProperty("data")
                .GetProperty("envelopeSummary")
                .GetProperty("recipients")
                .GetProperty("signers");

            foreach (var signer in recipients.EnumerateArray())
            {
                events.Add(new RecipientEvent(
                    RecipientId: signer.TryGetProperty("recipientId", out var rid) ? rid.GetString()! : string.Empty,
                    Email: signer.TryGetProperty("email", out var em) ? em.GetString()! : string.Empty,
                    Status: signer.TryGetProperty("status", out var st) ? st.GetString()! : string.Empty,
                    SignedDateTime: signer.TryGetProperty("signedDateTime", out var dt) && dt.ValueKind != JsonValueKind.Null
                        ? dt.GetDateTime() : null
                ));
            }
        }
        catch { /* recipients not present */ }
        return events;
    }
}

// ── Response DTOs ─────────────────────────────────────────────────────────────

public record StartSigningResponse
{
    public string WorkflowId { get; init; } = string.Empty;
    public string RequestId { get; init; } = string.Empty;
    public string RunId { get; init; } = string.Empty;
    public string StatusUrl { get; init; } = string.Empty;
}

public record SigningStatusResponse
{
    public string WorkflowId { get; init; } = string.Empty;
    public SigningStatus Status { get; init; }
}
