using Newtonsoft.Json;

namespace AzureAppService.LetsEncrypt.Internal
{
    internal class WebhookPayload
    {
        [JsonProperty("isSuccess")]
        public bool IsSuccess { get; set; }

        [JsonProperty("action")]
        public string Action { get; set; }

        [JsonProperty("resourceGroup")]
        public string ResourceGroup { get; set; }

        [JsonProperty("siteName")]
        public string SiteName { get; set; }

        [JsonProperty("slotName")]
        public string SlotName { get; set; }

        [JsonProperty("hostNames")]
        public string[] HostNames { get; set; }
    }
}
