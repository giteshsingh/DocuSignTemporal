namespace DocuSignTemporal.Worker.Activities;

public class DocuSignOptions
{
    public string IntegrationKey { get; set; } = string.Empty;   // OAuth client ID
    public string UserId { get; set; } = string.Empty;           // Impersonated user GUID
    public string AccountId { get; set; } = string.Empty;        // DocuSign account ID
    public string AuthServer { get; set; } = "account-d.docusign.com"; // demo or prod
    public string BaseUrl { get; set; } = "https://demo.docusign.net/restapi";
    public string RsaPrivateKey { get; set; } = string.Empty;    // RSA private key text
}
