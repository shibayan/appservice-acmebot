using System;
using System.ComponentModel.DataAnnotations;

namespace AppService.Acmebot.Options;

public class AcmebotOptions
{
    [Required]
    public Uri Endpoint { get; set; }

    [Required]
    public string Contacts { get; set; }

    [Required]
    public string SubscriptionId { get; set; }

    [Required]
    public string VaultBaseUrl { get; set; }

    [Required]
    public string StorageSasUri { get; set; }

    // Comma-separated CNAME targets a hostname must resolve through for a renewal to
    // proceed (e.g. "idp.criipto.io"). Empty/absent disables the DNS precondition check.
    public string RequiredCnameTargets { get; set; }

    [Url]
    public string Webhook { get; set; }

    [Required]
    public string Environment { get; set; } = "AzureCloud";

    public string PreferredChain { get; set; }

    [Range(0, 365)]
    public int RenewBeforeExpiry { get; set; } = 30;

    public ExternalAccountBindingOptions ExternalAccountBinding { get; set; }
}
