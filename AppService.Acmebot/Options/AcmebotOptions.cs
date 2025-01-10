using System;
using System.ComponentModel.DataAnnotations;

namespace AppService.Acmebot.Options;

public class ValidationWebAppOptions
{
    [Required]
    public string ResourceGroupName { get; set; }

    [Required]
    public string WebAppName { get; set; }
}

public class AcmebotOptions
{
    [Required]
    public Uri Endpoint { get; set; }

    [Required]
    public string Contacts { get; set; }

    [Required]
    public string SubscriptionId { get; set; }

    [Url]
    public string Webhook { get; set; }

    [Required]
    public string Environment { get; set; } = "AzureCloud";

    public string PreferredChain { get; set; }

    [Range(0, 365)]
    public int RenewBeforeExpiry { get; set; } = 30;

    public ExternalAccountBindingOptions ExternalAccountBinding { get; set; }

    [Required]
    [Url]
    public string KeyVaultUri { get; set; }

    [Required]
    public ValidationWebAppOptions ValidationWebApp { get; set; }
}
