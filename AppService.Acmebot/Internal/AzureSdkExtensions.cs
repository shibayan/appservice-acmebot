﻿using System.Collections.Generic;
using System.Threading.Tasks;

using Azure.ResourceManager.AppService;
using Azure.ResourceManager.Dns;
using Azure.ResourceManager.Dns.Models;
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

    public static async Task<IReadOnlyList<WebSiteData>> ListAllAsync(this WebSiteCollection collection)
    {
        var sites = new List<WebSiteData>();

        var result = collection.GetAllAsync(true);

        await foreach (var webSite in result)
        {
            sites.Add(webSite.Data);
        }

        return sites;
    }

    public static async Task<IReadOnlyList<CertificateData>> ListAllCertificatesAsync(this SubscriptionResource subscription)
    {
        var certificates = new List<CertificateData>();

        var result = subscription.GetCertificatesAsync();

        await foreach (var certificate in result)
        {
            certificates.Add(certificate.Data);
        }

        return certificates;
    }

    public static async Task<IReadOnlyList<Zone>> ListAllAsync(this ZonesOperations operations)
    {
        var zones = new List<Zone>();

        var result = operations.ListAsync();

        await foreach (var zone in result)
        {
            zones.Add(zone);
        }

        return zones;
    }

    public static async Task<RecordSet> GetOrDefaultAsync(this RecordSetsOperations operations, string resourceGroupName, string zoneName, string relativeRecordSetName, RecordType recordType)
    {
        try
        {
            return await operations.GetAsync(resourceGroupName, zoneName, relativeRecordSetName, recordType);
        }
        catch
        {
            return null;
        }
    }

    public static (string appName, string slotName) SplitName(this WebSiteData webSite)
    {
        var index = webSite.Name.IndexOf('/');

        if (index == -1)
        {
            return (webSite.Name, null);
        }

        return (webSite.Name[..index], webSite.Name[(index + 1)..]);
    }
}
