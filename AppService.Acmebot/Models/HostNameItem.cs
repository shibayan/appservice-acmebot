using Newtonsoft.Json;

namespace AppService.Acmebot.Models;

public class HostNameItem
{
    [JsonProperty("name")]
    public string Name { get; set; }

    [JsonProperty("thumbprint")]
    public string Thumbprint { get; set; }

    [JsonProperty("issuer")]
    public string Issuer { get; set; }
}
