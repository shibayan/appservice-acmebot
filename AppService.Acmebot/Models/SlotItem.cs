using System.Collections.Generic;

using Newtonsoft.Json;

namespace AppService.Acmebot.Models
{
    public class SlotItem
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("dnsNames")]
        public IReadOnlyList<DnsNameItem> DnsNames { get; set; }
    }
}
