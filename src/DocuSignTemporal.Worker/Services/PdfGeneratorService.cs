using DocuSignTemporal.Core.Models;
using iText.Kernel.Pdf;
using iText.Layout;
using iText.Layout.Element;
using iText.Layout.Properties;
using iText.Kernel.Font;
using iText.IO.Font.Constants;
using iText.Kernel.Colors;

namespace DocuSignTemporal.Worker.Services;

public interface IPdfGeneratorService
{
    Task<byte[]> GenerateAsync(DocumentType documentType, List<DocumentAttribute> attributes, string documentName);
}

/// <summary>
/// Generates dynamic PDFs for all 13 document types using iText7.
/// Each document type has its own renderer method with dynamic attribute injection.
/// Signing anchor strings (/sig1/, /date1/ etc.) are embedded as invisible text
/// so DocuSign can auto-position tabs.
/// </summary>
public class PdfGeneratorService : IPdfGeneratorService
{
    public async Task<byte[]> GenerateAsync(
        DocumentType documentType,
        List<DocumentAttribute> attributes,
        string documentName)
    {
        return await Task.Run(() =>
        {
            using var ms = new MemoryStream();
            using var writer = new PdfWriter(ms);
            using var pdf = new PdfDocument(writer);
            using var document = new Document(pdf);

            var attrs = attributes.ToDictionary(a => a.Key, a => a.Value);

            var renderer = GetRenderer(documentType);
            renderer(document, attrs, documentName);

            document.Close();
            return ms.ToArray();
        });
    }

    private static Action<Document, Dictionary<string, string>, string> GetRenderer(DocumentType type) =>
        type switch
        {
            DocumentType.NDA => RenderNDA,
            DocumentType.EmploymentContract => RenderEmploymentContract,
            DocumentType.ServiceAgreement => RenderServiceAgreement,
            DocumentType.ConfidentialityAgreement => RenderConfidentialityAgreement,
            DocumentType.IndependentContractorAgreement => RenderContractorAgreement,
            DocumentType.PurchaseOrder => RenderPurchaseOrder,
            DocumentType.LeaseAgreement => RenderLeaseAgreement,
            DocumentType.PartnershipAgreement => RenderPartnershipAgreement,
            DocumentType.LoanAgreement => RenderLoanAgreement,
            DocumentType.InsuranceForm => RenderInsuranceForm,
            DocumentType.TaxForm => RenderTaxForm,
            DocumentType.ComplianceForm => RenderComplianceForm,
            DocumentType.TermsAndConditions => RenderTermsAndConditions,
            _ => throw new NotSupportedException($"Document type {type} not supported")
        };

    // ── Document Renderers ────────────────────────────────────────────────────

    private static void RenderNDA(Document doc, Dictionary<string, string> attrs, string name)
    {
        var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA_BOLD);
        var bodyFont = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);

        var party1 = attrs.GetValueOrDefault("Party1Name", "[Party 1 Name]");
        var party2 = attrs.GetValueOrDefault("Party2Name", "[Party 2 Name]");
        var effectiveDate = attrs.GetValueOrDefault("EffectiveDate", DateTime.Today.ToString("MMMM dd, yyyy"));
        var purpose = attrs.GetValueOrDefault("Purpose", "evaluation of potential business opportunities");
        var duration = attrs.GetValueOrDefault("DurationYears", "2");

        doc.Add(new Paragraph("NON-DISCLOSURE AGREEMENT")
            .SetFont(font).SetFontSize(18).SetTextAlignment(TextAlignment.CENTER).SetMarginBottom(20));

        doc.Add(new Paragraph(
            $"This Non-Disclosure Agreement (\"Agreement\") is entered into as of {effectiveDate}, " +
            $"between {party1} and {party2} (collectively the \"Parties\").")
            .SetFont(bodyFont).SetFontSize(11).SetMarginBottom(15));

        doc.Add(new Paragraph("1. PURPOSE")
            .SetFont(font).SetFontSize(12).SetMarginBottom(5));
        doc.Add(new Paragraph(
            $"The Parties wish to explore a business opportunity related to {purpose}. " +
            "In connection with this opportunity, each Party may disclose Confidential Information to the other.")
            .SetFont(bodyFont).SetFontSize(11).SetMarginBottom(10));

        doc.Add(new Paragraph("2. CONFIDENTIAL INFORMATION")
            .SetFont(font).SetFontSize(12).SetMarginBottom(5));
        doc.Add(new Paragraph(
            "\"Confidential Information\" means any information disclosed by either Party that is designated " +
            "as confidential or that reasonably should be understood to be confidential given the nature of the " +
            "information and the circumstances of disclosure.")
            .SetFont(bodyFont).SetFontSize(11).SetMarginBottom(10));

        doc.Add(new Paragraph($"3. TERM\nThis Agreement shall remain in effect for {duration} years from the Effective Date.")
            .SetFont(bodyFont).SetFontSize(11).SetMarginBottom(20));

        // Signature block with DocuSign anchor strings
        AddSignatureBlock(doc, bodyFont, party1, party2);
    }

    private static void RenderEmploymentContract(Document doc, Dictionary<string, string> attrs, string name)
    {
        var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA_BOLD);
        var bodyFont = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);

        var employeeName = attrs.GetValueOrDefault("EmployeeName", "[Employee Name]");
        var position = attrs.GetValueOrDefault("Position", "[Position Title]");
        var startDate = attrs.GetValueOrDefault("StartDate", "[Start Date]");
        var salary = attrs.GetValueOrDefault("AnnualSalary", "[Salary]");
        var department = attrs.GetValueOrDefault("Department", "[Department]");
        var reportingTo = attrs.GetValueOrDefault("ReportingTo", "[Manager Name]");
        var workLocation = attrs.GetValueOrDefault("WorkLocation", "[Office Location]");

        doc.Add(new Paragraph("EMPLOYMENT AGREEMENT")
            .SetFont(font).SetFontSize(18).SetTextAlignment(TextAlignment.CENTER).SetMarginBottom(20));

        doc.Add(new Paragraph(
            $"This Employment Agreement is entered into between the Company and {employeeName} (\"Employee\").")
            .SetFont(bodyFont).SetFontSize(11).SetMarginBottom(15));

        doc.Add(new Paragraph("TERMS OF EMPLOYMENT").SetFont(font).SetFontSize(12).SetMarginBottom(8));

        var table = new Table(2).UseAllAvailableWidth();
        AddTableRow(table, "Position:", position, bodyFont);
        AddTableRow(table, "Department:", department, bodyFont);
        AddTableRow(table, "Start Date:", startDate, bodyFont);
        AddTableRow(table, "Annual Salary:", $"${salary}", bodyFont);
        AddTableRow(table, "Reporting To:", reportingTo, bodyFont);
        AddTableRow(table, "Work Location:", workLocation, bodyFont);
        doc.Add(table.SetMarginBottom(20));

        doc.Add(new Paragraph("ACKNOWLEDGEMENT")
            .SetFont(font).SetFontSize(12).SetMarginBottom(5));
        doc.Add(new Paragraph(
            "By signing below, Employee acknowledges receipt and understanding of this Employment Agreement.")
            .SetFont(bodyFont).SetFontSize(11).SetMarginBottom(20));

        // Dual-anchor signature block
        doc.Add(new Paragraph($"Employee Signature: /employee_sig/ Date: /emp_date/")
            .SetFont(bodyFont).SetFontSize(10).SetFontColor(ColorConstants.WHITE));
        doc.Add(new Paragraph("_________________________________  _______________")
            .SetFont(bodyFont).SetFontSize(11));
        doc.Add(new Paragraph($"Employee: {employeeName}            Date")
            .SetFont(bodyFont).SetFontSize(10).SetMarginBottom(15));

        doc.Add(new Paragraph($"Employer Signature: /employer_sig/")
            .SetFont(bodyFont).SetFontSize(10).SetFontColor(ColorConstants.WHITE));
        doc.Add(new Paragraph("_________________________________  _______________")
            .SetFont(bodyFont).SetFontSize(11));
        doc.Add(new Paragraph("Authorized Signatory                Date")
            .SetFont(bodyFont).SetFontSize(10));
    }

    private static void RenderServiceAgreement(Document doc, Dictionary<string, string> attrs, string name)
    {
        var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA_BOLD);
        var bodyFont = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);

        var clientName = attrs.GetValueOrDefault("ClientName", "[Client Name]");
        var serviceName = attrs.GetValueOrDefault("ServiceDescription", "[Service Description]");
        var startDate = attrs.GetValueOrDefault("StartDate", "[Start Date]");
        var endDate = attrs.GetValueOrDefault("EndDate", "[End Date]");
        var value = attrs.GetValueOrDefault("ContractValue", "[Value]");
        var paymentTerms = attrs.GetValueOrDefault("PaymentTerms", "Net 30");
        var deliverables = attrs.GetValueOrDefault("Deliverables", "As outlined in Statement of Work");

        doc.Add(new Paragraph("SERVICE AGREEMENT")
            .SetFont(font).SetFontSize(18).SetTextAlignment(TextAlignment.CENTER).SetMarginBottom(20));

        doc.Add(new Paragraph($"This Service Agreement is entered into between the Company and {clientName}.")
            .SetFont(bodyFont).SetFontSize(11).SetMarginBottom(15));

        doc.Add(new Paragraph("1. SERVICES").SetFont(font).SetFontSize(12).SetMarginBottom(5));
        doc.Add(new Paragraph($"The Company shall provide: {serviceName}")
            .SetFont(bodyFont).SetFontSize(11).SetMarginBottom(10));

        doc.Add(new Paragraph("2. TERM").SetFont(font).SetFontSize(12).SetMarginBottom(5));
        doc.Add(new Paragraph($"Services will be performed from {startDate} through {endDate}.")
            .SetFont(bodyFont).SetFontSize(11).SetMarginBottom(10));

        doc.Add(new Paragraph("3. COMPENSATION").SetFont(font).SetFontSize(12).SetMarginBottom(5));
        doc.Add(new Paragraph($"Total Contract Value: ${value}. Payment Terms: {paymentTerms}.")
            .SetFont(bodyFont).SetFontSize(11).SetMarginBottom(10));

        doc.Add(new Paragraph("4. DELIVERABLES").SetFont(font).SetFontSize(12).SetMarginBottom(5));
        doc.Add(new Paragraph(deliverables).SetFont(bodyFont).SetFontSize(11).SetMarginBottom(20));

        AddSignatureBlock(doc, bodyFont, clientName, "Service Provider");
    }

    // ── Remaining 10 document types (abbreviated pattern) ─────────────────────

    private static void RenderConfidentialityAgreement(Document doc, Dictionary<string, string> attrs, string name) =>
        RenderGenericAgreement(doc, attrs, "CONFIDENTIALITY AGREEMENT",
            $"This Confidentiality Agreement governs the disclosure of information between the parties " +
            $"named herein, effective {attrs.GetValueOrDefault("EffectiveDate", "[Date]")}.");

    private static void RenderContractorAgreement(Document doc, Dictionary<string, string> attrs, string name) =>
        RenderGenericAgreement(doc, attrs, "INDEPENDENT CONTRACTOR AGREEMENT",
            $"This Agreement is entered into between the Company and {attrs.GetValueOrDefault("ContractorName", "[Contractor]")} " +
            $"for services described as: {attrs.GetValueOrDefault("ServiceScope", "[Scope]")}.");

    private static void RenderPurchaseOrder(Document doc, Dictionary<string, string> attrs, string name) =>
        RenderGenericAgreement(doc, attrs, "PURCHASE ORDER",
            $"PO #{attrs.GetValueOrDefault("PONumber", "[PO Number]")} | " +
            $"Vendor: {attrs.GetValueOrDefault("VendorName", "[Vendor]")} | " +
            $"Amount: ${attrs.GetValueOrDefault("Amount", "[Amount]")}");

    private static void RenderLeaseAgreement(Document doc, Dictionary<string, string> attrs, string name) =>
        RenderGenericAgreement(doc, attrs, "LEASE AGREEMENT",
            $"This Lease Agreement is between {attrs.GetValueOrDefault("LessorName", "[Lessor]")} and " +
            $"{attrs.GetValueOrDefault("LesseeName", "[Lessee]")} for the property at " +
            $"{attrs.GetValueOrDefault("PropertyAddress", "[Address]")}.");

    private static void RenderPartnershipAgreement(Document doc, Dictionary<string, string> attrs, string name) =>
        RenderGenericAgreement(doc, attrs, "PARTNERSHIP AGREEMENT",
            $"This Partnership Agreement is entered into between " +
            $"{attrs.GetValueOrDefault("Partner1", "[Partner 1]")} and {attrs.GetValueOrDefault("Partner2", "[Partner 2]")}.");

    private static void RenderLoanAgreement(Document doc, Dictionary<string, string> attrs, string name) =>
        RenderGenericAgreement(doc, attrs, "LOAN AGREEMENT",
            $"Loan Amount: ${attrs.GetValueOrDefault("LoanAmount", "[Amount]")} | " +
            $"Interest Rate: {attrs.GetValueOrDefault("InterestRate", "[Rate]")}% | " +
            $"Term: {attrs.GetValueOrDefault("TermMonths", "[X]")} months.");

    private static void RenderInsuranceForm(Document doc, Dictionary<string, string> attrs, string name) =>
        RenderGenericAgreement(doc, attrs, "INSURANCE APPLICATION FORM",
            $"Applicant: {attrs.GetValueOrDefault("ApplicantName", "[Applicant]")} | " +
            $"Coverage Type: {attrs.GetValueOrDefault("CoverageType", "[Type]")}");

    private static void RenderTaxForm(Document doc, Dictionary<string, string> attrs, string name) =>
        RenderGenericAgreement(doc, attrs, "TAX AUTHORIZATION FORM",
            $"Taxpayer: {attrs.GetValueOrDefault("TaxpayerName", "[Name]")} | " +
            $"Tax Year: {attrs.GetValueOrDefault("TaxYear", DateTime.Today.Year.ToString())} | " +
            $"TIN: {attrs.GetValueOrDefault("TIN", "[TIN]")}",
            useCheckbox: true);

    private static void RenderComplianceForm(Document doc, Dictionary<string, string> attrs, string name) =>
        RenderGenericAgreement(doc, attrs, "COMPLIANCE CERTIFICATION FORM",
            $"Employee: {attrs.GetValueOrDefault("EmployeeName", "[Name]")} | " +
            $"Department: {attrs.GetValueOrDefault("Department", "[Dept]")} | " +
            $"Period: {attrs.GetValueOrDefault("CompliancePeriod", "[Period]")}",
            useCheckbox: true);

    private static void RenderTermsAndConditions(Document doc, Dictionary<string, string> attrs, string name) =>
        RenderGenericAgreement(doc, attrs, "TERMS AND CONDITIONS ACCEPTANCE",
            $"By signing this document, {attrs.GetValueOrDefault("SigneeName", "[Name]")} acknowledges " +
            $"having read and agreed to the Terms and Conditions effective {attrs.GetValueOrDefault("EffectiveDate", "[Date]")}.");

    // ── Shared Helpers ────────────────────────────────────────────────────────

    private static void RenderGenericAgreement(
        Document doc,
        Dictionary<string, string> attrs,
        string title,
        string body,
        bool useCheckbox = false)
    {
        var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA_BOLD);
        var bodyFont = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);

        doc.Add(new Paragraph(title)
            .SetFont(font).SetFontSize(18).SetTextAlignment(TextAlignment.CENTER).SetMarginBottom(20));

        doc.Add(new Paragraph(body)
            .SetFont(bodyFont).SetFontSize(11).SetMarginBottom(20));

        if (useCheckbox)
        {
            doc.Add(new Paragraph("□ I certify that the information provided is accurate and complete. /certify/")
                .SetFont(bodyFont).SetFontSize(11).SetMarginBottom(20));
        }

        AddSignatureBlock(doc, bodyFont);
    }

    private static void AddSignatureBlock(Document doc, PdfFont bodyFont,
        string signer1 = "Authorized Signatory", string signer2 = "Counterparty")
    {
        // Invisible anchor text that DocuSign uses to place signature tabs
        doc.Add(new Paragraph("\n\nSIGNATURES")
            .SetFont(PdfFontFactory.CreateFont(StandardFonts.HELVETICA_BOLD))
            .SetFontSize(12).SetMarginBottom(10));

        // Anchor strings (white text — invisible to human readers, detected by DocuSign)
        doc.Add(new Paragraph("/sig1/ /date1/ /init1/")
            .SetFont(bodyFont).SetFontSize(8).SetFontColor(ColorConstants.WHITE));

        doc.Add(new Paragraph($"__________________________________  _______________")
            .SetFont(bodyFont).SetFontSize(11));
        doc.Add(new Paragraph($"{signer1}                          Date")
            .SetFont(bodyFont).SetFontSize(10).SetMarginBottom(20));

        doc.Add(new Paragraph("/sig2/")
            .SetFont(bodyFont).SetFontSize(8).SetFontColor(ColorConstants.WHITE));

        doc.Add(new Paragraph($"__________________________________  _______________")
            .SetFont(bodyFont).SetFontSize(11));
        doc.Add(new Paragraph($"{signer2}                          Date")
            .SetFont(bodyFont).SetFontSize(10));
    }

    private static void AddSignatureBlock(Document doc, PdfFont bodyFont) =>
        AddSignatureBlock(doc, bodyFont, "Authorized Signatory", "Counterparty");

    private static void AddTableRow(Table table, string label, string value, PdfFont font)
    {
        table.AddCell(new Cell().Add(new Paragraph(label).SetFont(
            PdfFontFactory.CreateFont(StandardFonts.HELVETICA_BOLD)).SetFontSize(10)));
        table.AddCell(new Cell().Add(new Paragraph(value).SetFont(font).SetFontSize(10)));
    }
}
