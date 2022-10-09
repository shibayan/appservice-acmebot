using System.Collections.Generic;
using System.Threading.Tasks;

using Azure.ResourceManager.Dns;
using Azure.ResourceManager.Resources;

namespace AppService.Acmebot.Internal;

internal static class AzureSdkExtensions
{
    public static async Task<IReadOnlyList<string>> ListAllAsync(this ResourceGroupCollection operations)
    {
        var resourceGroups = new List<string>();

        var result = operations.GetAllAsync();

        await foreach (var resourceGroup in result)
        {
            resourceGroups.Add(resourceGroup.Data.Name);
        }

        return resourceGroups;
    }

    public static async Task<IReadOnlyList<DnsZoneResource>> ListAllDnsZonesAsync(this SubscriptionResource subscription)
    {
        var dnsZones = new List<DnsZoneResource>();

        var result = subscription.GetDnsZonesByDnszoneAsync();

        await foreach (var dnsZone in result)
        {
            dnsZones.Add(dnsZone);
        }

        return dnsZones;
    }
}
