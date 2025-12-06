namespace CallTranscription.Functions.Common;

public class WebhookAuthOptions
{
    public string HeaderName { get; set; } = "x-acs-webhook-key";
    public string? Key { get; set; }
    public string? PublicBaseUrl { get; set; }
    public bool EnforceKey { get; set; } = false;
}
