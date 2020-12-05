using Newtonsoft.Json;

namespace AppService.Acmebot.Models
{
    public class DnsNameItem
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("issuer")]
        public string Issuer { get; set; }
    }
}
