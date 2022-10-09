using System.Collections.Generic;
using System.Linq;

using AppService.Acmebot.Internal;

using Azure.ResourceManager.AppService;

using Newtonsoft.Json;

namespace AppService.Acmebot.Models;

public class WebSiteItem
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("kind")]
    public string Kind { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; }

    [JsonProperty("slotName")]
    public string SlotName { get; set; }

    [JsonProperty("hostNames")]
    public IReadOnlyList<HostNameItem> HostNames { get; set; }

    [JsonProperty("isRunning")]
    public bool IsRunning { get; set; }

    [JsonProperty("hasCustomDomain")]
    public bool HasCustomDomain { get; set; }

    public static WebSiteItem Create(WebSiteData webSiteData, AzureEnvironment environment)
    {
        var index = webSiteData.Name.IndexOf('/');

        return new WebSiteItem
        {
            Id = webSiteData.Id,
            Kind = webSiteData.Kind,
            Name = index == -1 ? webSiteData.Name : webSiteData.Name[..index],
            SlotName = index == -1 ? "production" : webSiteData.Name[(index + 1)..],
            HostNames = webSiteData.HostNameSslStates
                                   .Where(x => !x.Name.EndsWith(environment.AppService) && !x.Name.EndsWith(environment.TrafficManager))
                                   .Select(x => new HostNameItem { Name = x.Name, Thumbprint = x.Thumbprint?.ToString() })
                                   .ToArray(),
            IsRunning = webSiteData.State == "Running",
            HasCustomDomain = webSiteData.HostNames.Any(x => !x.EndsWith(environment.AppService) && !x.EndsWith(environment.TrafficManager))
        };
    }
}
