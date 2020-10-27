using System.Collections.Generic;

using Newtonsoft.Json;

namespace AppService.Acmebot.Models
{
    public class ResourceGroupInformation
    {
        [JsonProperty("name")]
        public string Name { get; set; }
    }

    public class SiteInformation
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("slots")]
        public IList<SlotInformation> Slots { get; set; }
    }

    public class SlotInformation
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("dnsNames")]
        public IReadOnlyList<DnsNameInformation> DnsNames { get; set; }
    }

    public class DnsNameInformation
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("issuer")]
        public string Issuer { get; set; }
    }
}
