using System.Collections.Generic;

using Newtonsoft.Json;

namespace AppService.Acmebot.Models;

public class WebSiteItem
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; }

    [JsonProperty("slotName")]
    public string SlotName { get; set; }

    [JsonProperty("hostNames")]
    public IReadOnlyList<HostNameItem> HostNames { get; set; }

    [JsonProperty("dnsNames")]
    public IReadOnlyList<DnsNameItem> DnsNames { get; set; }

    [JsonProperty("slots")]
    public IList<WebSiteItem> Slots { get; set; }
}
