using System.ComponentModel.DataAnnotations;

namespace AppService.Acmebot.Options
{
    public class AcmebotOptions
    {
        [Required]
        public string Endpoint { get; set; } = "https://acme-v02.api.letsencrypt.org/";

        [Required]
        public string Contacts { get; set; }

        [Required]
        public string SubscriptionId { get; set; }

        [Url]
        public string Webhook { get; set; }

        [Required]
        public string Environment { get; set; } = "AzureCloud";

        public string PreferredChain { get; set; }

        public ExternalAccountBindingOptions ExternalAccountBinding { get; set; }
    }
}
