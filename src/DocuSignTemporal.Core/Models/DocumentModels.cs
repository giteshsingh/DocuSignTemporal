namespace DocuSignTemporal.Core.Models;

// ─── Document Types ────────────────────────────────────────────────────────────

public enum DocumentType
{
    NDA = 1,
    EmploymentContract = 2,
    ServiceAgreement = 3,
    ConfidentialityAgreement = 4,
    IndependentContractorAgreement = 5,
    PurchaseOrder = 6,
    LeaseAgreement = 7,
    PartnershipAgreement = 8,
    LoanAgreement = 9,
    InsuranceForm = 10,
    TaxForm = 11,
    ComplianceForm = 12,
    TermsAndConditions = 13
}

public enum SigningStatus
{
    Pending,
    Sent,
    Delivered,
    Completed,
    Declined,
    Voided,
    Expired,
    Failed
}

// ─── Core DTOs ─────────────────────────────────────────────────────────────────

public record SignerInfo(
    string Name,
    string Email,
    string Role,
    int RoutingOrder = 1
);

public record DocumentAttribute(string Key, string Value);

public record SigningField(
    string TabLabel,
    string AnchorText,
    int PageNumber = 1,
    int XPosition = 0,
    int YPosition = 0
);

// ─── Workflow Input/Output ─────────────────────────────────────────────────────

public record DocumentSigningRequest
{
    public string RequestId { get; init; } = Guid.NewGuid().ToString();
    public DocumentType DocumentType { get; init; }
    public string DocumentName { get; init; } = string.Empty;
    public string PdfTemplatePath { get; init; } = string.Empty;
    public List<SignerInfo> Signers { get; init; } = new();
    public List<DocumentAttribute> Attributes { get; init; } = new();
    public string EmailSubject { get; init; } = "Please sign the document";
    public string EmailBody { get; init; } = "Please review and sign the attached document.";
    public int ExpirationDays { get; init; } = 7;
    public string NotificationWebhookUrl { get; init; } = string.Empty;
    public string CallbackEmail { get; init; } = string.Empty;
}

public record DocumentSigningResult
{
    public string RequestId { get; init; } = string.Empty;
    public string EnvelopeId { get; init; } = string.Empty;
    public DocumentType DocumentType { get; init; }
    public SigningStatus Status { get; init; }
    public DateTime CompletedAt { get; init; }
    public string SignedDocumentBase64 { get; init; } = string.Empty;
    public string SignedDocumentUrl { get; init; } = string.Empty;
    public List<SignerCompletionInfo> SignerCompletions { get; init; } = new();
    public string ErrorMessage { get; init; } = string.Empty;
}

public record SignerCompletionInfo(
    string Name,
    string Email,
    DateTime? SignedAt,
    string Status
);

// ─── Batch Workflow ────────────────────────────────────────────────────────────

public record BatchSigningRequest
{
    public string BatchId { get; init; } = Guid.NewGuid().ToString();
    public List<DocumentSigningRequest> Documents { get; init; } = new();
    public string NotifyEmail { get; init; } = string.Empty;
    public bool WaitForAll { get; init; } = true;
}

public record BatchSigningResult
{
    public string BatchId { get; init; } = string.Empty;
    public int TotalDocuments { get; init; }
    public int CompletedDocuments { get; init; }
    public int FailedDocuments { get; init; }
    public List<DocumentSigningResult> Results { get; init; } = new();
    public DateTime CompletedAt { get; init; }
}

// ─── DocuSign Events ──────────────────────────────────────────────────────────

public record DocuSignWebhookEvent
{
    public string EnvelopeId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTime EventTime { get; init; }
    public List<RecipientEvent> RecipientEvents { get; init; } = new();
}

public record RecipientEvent(
    string RecipientId,
    string Email,
    string Status,
    DateTime? SignedDateTime
);
