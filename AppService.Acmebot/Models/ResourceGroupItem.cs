using Azure.ResourceManager.Resources;

using Newtonsoft.Json;

namespace AppService.Acmebot.Models;

public class ResourceGroupItem
{
    [JsonProperty("name")]
    public string Name { get; set; }

    public static ResourceGroupItem Create(ResourceGroupData resourceGroup)
    {
        return new ResourceGroupItem
        {
            Name = resourceGroup.Name
        };
    }
}
