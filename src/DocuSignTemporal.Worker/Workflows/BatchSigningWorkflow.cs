using DocuSignTemporal.Core.Interfaces;
using DocuSignTemporal.Core.Models;
using Temporalio.Workflows;
using Microsoft.Extensions.Logging;

namespace DocuSignTemporal.Worker.Workflows;

/// <summary>
/// Orchestrates batch signing of all 13 document types in parallel.
/// Each document gets its own child workflow, enabling independent 
/// retries and status tracking per document.
/// </summary>
[Workflow]
public class BatchSigningWorkflow : IBatchSigningWorkflow
{
    private readonly List<DocumentSigningResult> _completedResults = new();
    private int _failedCount = 0;

    [WorkflowRun]
    public async Task<BatchSigningResult> RunAsync(BatchSigningRequest request)
    {
        var logger = Workflow.Logger;
        logger.LogInformation(
            "Starting BatchSigningWorkflow | BatchId: {BatchId} | Documents: {Count}",
            request.BatchId, request.Documents.Count);

        var childOptions = new ChildWorkflowOptions
        {
            // Each child has its own workflow ID for targeted signal delivery
            TaskQueue = "docusign-signing-queue"
        };

        // Launch all document workflows concurrently
        var childTasks = request.Documents.Select(async (doc, index) =>
        {
            var childWorkflowId = $"{request.BatchId}-doc-{(int)doc.DocumentType}";

            try
            {
                logger.LogInformation(
                    "Spawning child workflow for {DocumentType} | WorkflowId: {WorkflowId}",
                    doc.DocumentType, childWorkflowId);

                var result = await Workflow.ExecuteChildWorkflowAsync(
                    (IDocumentSigningWorkflow wf) => wf.RunAsync(doc),
                    new ChildWorkflowOptions
                    {
                        Id = childWorkflowId,
                        TaskQueue = "docusign-signing-queue"
                    });

                lock (_completedResults)
                    _completedResults.Add(result);

                return result;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Child workflow failed for {DocumentType} | WorkflowId: {WorkflowId}",
                    doc.DocumentType, childWorkflowId);

                Interlocked.Increment(ref _failedCount);

                var failResult = new DocumentSigningResult
                {
                    RequestId = doc.RequestId,
                    DocumentType = doc.DocumentType,
                    Status = SigningStatus.Failed,
                    ErrorMessage = ex.Message,
                    CompletedAt = Workflow.UtcNow
                };

                lock (_completedResults)
                    _completedResults.Add(failResult);

                return failResult;
            }
        }).ToList();

        if (request.WaitForAll)
        {
            await Task.WhenAll(childTasks);
        }
        else
        {
            // Wait for at least one to complete
            await Task.WhenAny(childTasks);
        }

        var allResults = await Task.WhenAll(childTasks);

        var batchResult = new BatchSigningResult
        {
            BatchId = request.BatchId,
            TotalDocuments = request.Documents.Count,
            CompletedDocuments = allResults.Count(r => r.Status == SigningStatus.Completed),
            FailedDocuments = allResults.Count(r => r.Status is SigningStatus.Failed
                or SigningStatus.Declined or SigningStatus.Expired),
            Results = allResults.ToList(),
            CompletedAt = Workflow.UtcNow
        };

        logger.LogInformation(
            "BatchSigningWorkflow completed | BatchId: {BatchId} | Completed: {Completed}/{Total}",
            request.BatchId, batchResult.CompletedDocuments, batchResult.TotalDocuments);

        return batchResult;
    }

    [WorkflowQuery]
    public BatchSigningResult GetCurrentProgress()
    {
        lock (_completedResults)
        {
            return new BatchSigningResult
            {
                CompletedDocuments = _completedResults.Count(r => r.Status == SigningStatus.Completed),
                FailedDocuments = _failedCount,
                Results = _completedResults.ToList()
            };
        }
    }
}
