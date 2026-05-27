# DocuSign + Temporal Signing Platform (.NET 8)

A production-ready sample for orchestrating **13 document types** through DocuSign
e-signatures using Temporal durable workflows — with webhook-driven completion signals,
dynamic PDF generation, and email notifications.

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                          CLIENT / YOUR APP                                  │
│  POST /api/signing/start   POST /api/signing/batch                          │
└────────────────────┬────────────────────┬───────────────────────────────────┘
                     │                    │
                     ▼                    ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                        DocuSignTemporal.Api                                 │
│                                                                             │
│  SigningController                                                          │
│  ├── POST /start          → StartWorkflow(DocumentSigningWorkflow)          │
│  ├── POST /batch          → StartWorkflow(BatchSigningWorkflow)             │
│  ├── GET  /{id}/status    → Query(GetCurrentStatus)                         │
│  ├── GET  /{id}/document  → GetResult → stream PDF                         │
│  └── POST /webhook/docusign → Signal(HandleDocuSignEventAsync)             │
└────────────────────┬────────────────────────────────────────────────────────┘
                     │  Temporal gRPC (port 7233)
                     ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                     Temporal Server (self-hosted / Temporal Cloud)          │
│                                                                             │
│  Task Queue: docusign-signing-queue                                         │
│                                                                             │
│  BatchSigningWorkflow                                                       │
│  └── spawns N × DocumentSigningWorkflow (child workflows, parallel)         │
│       ├── Activity: GeneratePdfFromTemplateAsync                            │
│       ├── Activity: CreateDocuSignEnvelopeAsync                             │
│       ├── [WAIT] Signal or poll for completion                              │
│       ├── Activity: DownloadSignedDocumentAsync                             │
│       └── Activity: SendCompletionNotificationAsync                         │
└────────────────────┬─────────────────────────────────────────────────────  │
                     │ Workers poll this queue                                │
                     ▼                                                        │
┌─────────────────────────────────────────────────────────────────────────────┐
│                     DocuSignTemporal.Worker                                 │
│                                                                             │
│  DocuSignActivities                                                         │
│  ├── PdfGeneratorService  (iText7 — 13 doc type renderers)                 │
│  ├── DocuSign eSign SDK   (JWT auth, envelope create/download)              │
│  └── EmailNotificationService  (SMTP on completion)                        │
└─────────────────────────────────────────────────────────────────────────────┘
         │                          │
         ▼                          ▼
  DocuSign eSign API         Your SMTP Server
  (create envelope,          (completion emails)
   download signed PDF,
   poll status)
         │
         │ DocuSign Connect (webhook)
         ▼
  POST /api/signing/webhook/docusign
  → extracted envelopeId + custom RequestId
  → Signal → DocumentSigningWorkflow
```

---

## Projects

| Project | Type | Purpose |
|---------|------|---------|
| `DocuSignTemporal.Core` | Class Library | Shared models, enums, workflow/activity interfaces |
| `DocuSignTemporal.Worker` | Worker Service | Temporal worker — runs workflows + activities |
| `DocuSignTemporal.Api` | ASP.NET Core Web API | REST endpoints + DocuSign webhook receiver |

---

## Document Types Supported (all 13)

| # | Enum | Signing Fields |
|---|------|----------------|
| 1 | NDA | 2× signature, 1× date, 1× initials |
| 2 | EmploymentContract | Employee + employer signatures, date |
| 3 | ServiceAgreement | Client + provider signatures |
| 4 | ConfidentialityAgreement | Single signature + date |
| 5 | IndependentContractorAgreement | Single signature + date |
| 6 | PurchaseOrder | Single signature + date |
| 7 | LeaseAgreement | Lessor + lessee signatures |
| 8 | PartnershipAgreement | 2× partner signatures |
| 9 | LoanAgreement | Borrower + lender signatures |
| 10 | InsuranceForm | Single signature + date |
| 11 | TaxForm | Signature + date + certification checkbox |
| 12 | ComplianceForm | Signature + date + certification checkbox |
| 13 | TermsAndConditions | Single signature + date |

Signing tab positions are placed using **DocuSign anchor strings** (`/sig1/`, `/date1/` etc.)
embedded as white invisible text in the generated PDF.

---

## Workflow State Machine

```
PENDING
   │
   ▼ (PDF generated, envelope created)
SENT ────────────────────────────────────────────────────────┐
   │                                                          │
   ▼ (recipient opens email)                          [timeout after N days]
DELIVERED                                                     │
   │                                                          ▼
   ▼ (all signers complete)                              EXPIRED (envelope voided)
COMPLETED ──► download signed PDF ──► notify via email
   │
   │ (any signer declines)
DECLINED
   │
   │ (admin voids)
VOIDED
```

---

## Getting Started

### Prerequisites

- .NET 8 SDK
- [Temporal CLI](https://docs.temporal.io/cli) or Temporal Cloud account
- DocuSign Developer account (free at [developers.docusign.com](https://developers.docusign.com))

### 1. Start Temporal (local dev)

```bash
temporal server start-dev
```

### 2. Configure DocuSign

In `appsettings.json` (or user secrets):

```json
{
  "DocuSign": {
    "IntegrationKey": "<your-integration-key>",
    "UserId":         "<your-docusign-user-guid>",
    "AccountId":      "<your-account-id>",
    "AuthServer":     "account-d.docusign.com",
    "BaseUrl":        "https://demo.docusign.net/restapi",
    "RsaPrivateKey":  "-----BEGIN RSA PRIVATE KEY-----\n...\n-----END RSA PRIVATE KEY-----"
  }
}
```

**DocuSign JWT setup steps:**
1. Create an app at [DocuSign Admin](https://admindemo.docusign.com/api-integrator-key)
2. Enable JWT Grant, generate RSA key pair, paste private key above
3. Grant consent: `https://account-d.docusign.com/oauth/auth?response_type=code&scope=impersonation%20signature&client_id=<IntegrationKey>&redirect_uri=https://localhost`

### 3. Configure DocuSign Connect (webhooks)

In DocuSign Admin → Connect → Add Configuration:
- URL: `https://your-api-host/api/signing/webhook/docusign`
- Events: Envelope Sent, Delivered, Completed, Declined, Voided
- Data: Include Custom Fields, Recipients, Documents

### 4. Run

```bash
# Terminal 1 — Worker
cd src/DocuSignTemporal.Worker
dotnet run

# Terminal 2 — API
cd src/DocuSignTemporal.Api
dotnet run
```

---

## API Usage

### Sign a Single Document

```http
POST /api/signing/start
Content-Type: application/json

{
  "documentType": 1,
  "documentName": "NDA - Acme Corp",
  "emailSubject": "Please sign the NDA",
  "signers": [
    { "name": "Jane Smith", "email": "jane@acme.com", "role": "Signer", "routingOrder": 1 },
    { "name": "Bob Jones",  "email": "bob@vendor.com", "role": "Counterparty", "routingOrder": 2 }
  ],
  "attributes": [
    { "key": "Party1Name", "value": "Acme Corp" },
    { "key": "Party2Name", "value": "Vendor LLC" },
    { "key": "EffectiveDate", "value": "June 1, 2026" },
    { "key": "Purpose", "value": "evaluation of software licensing" }
  ],
  "expirationDays": 7,
  "notificationWebhookUrl": "https://your-api/api/signing/webhook/docusign",
  "callbackEmail": "admin@yourcompany.com"
}
```

**Response:**
```json
{
  "workflowId": "sign-NDA-abc-123",
  "requestId": "abc-123",
  "runId": "run-xyz",
  "statusUrl": "/api/signing/sign-NDA-abc-123/status"
}
```

### Sign All 13 Documents in Batch

```http
POST /api/signing/batch
Content-Type: application/json

{
  "batchId": "onboarding-2026-001",
  "notifyEmail": "hr@company.com",
  "waitForAll": true,
  "documents": [
    { "documentType": 1, "documentName": "NDA",                   "signers": [...], "attributes": [...] },
    { "documentType": 2, "documentName": "Employment Contract",    "signers": [...], "attributes": [...] },
    { "documentType": 3, "documentName": "Service Agreement",      "signers": [...], "attributes": [...] },
    ... (all 13)
  ]
}
```

### Check Status

```http
GET /api/signing/{workflowId}/status
→ { "status": "Completed" }
```

### Download Signed PDF

```http
GET /api/signing/{workflowId}/document
→ 200 application/pdf  (streams the signed document)
```

---

## Key Design Decisions

### Why Temporal?

| Challenge | Temporal Solution |
|-----------|-------------------|
| DocuSign signing can take hours/days | Workflow sleeps durably — no memory state lost |
| Webhooks can be missed or delayed | 5-min fallback polling activity if signal not received |
| 13 documents must all complete | BatchWorkflow uses `Task.WhenAll` over child workflows |
| Retries on transient DocuSign API errors | Activity retry policy (3× with exponential backoff) |
| Envelope expired before signing | `ExpirationDays` timeout + automatic void + notification |
| Correlate webhook → workflow | Custom fields in envelope + `WorkflowSignal` by workflow ID |

### Webhook → Signal Correlation

Each DocuSign envelope gets custom metadata fields:
- `RequestId` → maps directly to the `workflowId` pattern `sign-{DocumentType}-{RequestId}`
- `DocumentType` → helps reconstruct the workflow ID

The webhook endpoint extracts these, reconstructs the workflow ID, and sends a Temporal signal.
The workflow unblocks immediately without polling.

### PDF Anchor Strings

DocuSign auto-positions signature tabs using anchor text. Each template embeds invisible
white text like `/sig1/`, `/date1/`, `/employee_sig/` — DocuSign scans for these strings
and places the appropriate tab type automatically.

---

## Production Checklist

- [ ] Move `RsaPrivateKey` to Azure Key Vault / AWS Secrets Manager
- [ ] Add HMAC signature verification on the webhook endpoint
- [ ] Store signed PDFs to Azure Blob / S3 instead of returning base64
- [ ] Add Temporal namespace + worker authentication (mTLS for Temporal Cloud)
- [ ] Configure Temporal Search Attributes for workflow queries (e.g. by envelope ID)
- [ ] Add distributed tracing (OpenTelemetry) across API + Worker
- [ ] Rate-limit the webhook endpoint
- [ ] Add idempotency checks (re-delivered webhooks should be no-ops)
