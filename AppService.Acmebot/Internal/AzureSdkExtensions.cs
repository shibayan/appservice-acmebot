using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Azure.ResourceManager.Dns;
using Azure.ResourceManager.Dns.Models;
using Azure.ResourceManager.Resources;

using Microsoft.Azure.Management.WebSites;
using Microsoft.Azure.Management.WebSites.Models;

namespace AppService.Acmebot.Internal
{
    internal static class AzureSdkExtensions
    {
        public static Task<SiteConfigResource> GetConfigurationAsync(this IWebAppsOperations operations, Site site, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (site.IsSlot())
            {
                var (appName, slotName) = site.SplitName();

                return operations.GetConfigurationSlotAsync(site.ResourceGroup, appName, slotName, cancellationToken);
            }

            return operations.GetConfigurationAsync(site.ResourceGroup, site.Name, cancellationToken);
        }

        public static Task<SiteConfigResource> UpdateConfigurationAsync(this IWebAppsOperations operations, Site site, SiteConfigResource siteConfig, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (site.IsSlot())
            {
                var (appName, slotName) = site.SplitName();

                return operations.UpdateConfigurationSlotAsync(site.ResourceGroup, appName, siteConfig, slotName, cancellationToken);
            }

            return operations.UpdateConfigurationAsync(site.ResourceGroup, site.Name, siteConfig, cancellationToken);
        }

        public static Task<User> ListPublishingCredentialsAsync(this IWebAppsOperations operations, Site site, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (site.IsSlot())
            {
                var (appName, slotName) = site.SplitName();

                return operations.ListPublishingCredentialsSlotAsync(site.ResourceGroup, appName, slotName, cancellationToken);
            }

            return operations.ListPublishingCredentialsAsync(site.ResourceGroup, site.Name, cancellationToken);
        }

        public static Task<Site> CreateOrUpdateAsync(this IWebAppsOperations operations, Site site, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (site.IsSlot())
            {
                var (appName, slotName) = site.SplitName();

                return operations.CreateOrUpdateSlotAsync(site.ResourceGroup, appName, site, slotName, cancellationToken);
            }

            return operations.CreateOrUpdateAsync(site.ResourceGroup, site.Name, site, cancellationToken);
        }

        public static async Task<IReadOnlyList<ResourceGroup>> ListAllAsync(this ResourceGroupCollection operations)
        {
            var resourceGroups = new List<ResourceGroup>();

            var result = operations.GetAllAsync();

            await foreach (var resourceGroup in result)
            {
                resourceGroups.Add(resourceGroup);
            }

            return resourceGroups;
        }

#if false
        public static async Task<IReadOnlyList<Site>> ListByResourceGroupAllAsync(this IWebAppsOperations operations, string resourceGroupName)
        {
            var sites = new List<Site>();

            var list = await operations.ListByResourceGroupAsync(resourceGroupName, true);

            sites.AddRange(list);

            while (list.NextPageLink != null)
            {
                list = await operations.ListByResourceGroupNextAsync(list.NextPageLink);

                sites.AddRange(list);
            }

            return sites;
        }
#else
        public static async Task<IReadOnlyList<Site>> ListByResourceGroupAllAsync(this IWebAppsOperations operations, string resourceGroupName)
        {
            var sites = new List<Site>();

            var list = await operations.ListByResourceGroupAsync(resourceGroupName);

            sites.AddRange(list);

            while (list.NextPageLink != null)
            {
                list = await operations.ListByResourceGroupNextAsync(list.NextPageLink);

                sites.AddRange(list);
            }

            var slots = new List<Site>();

            foreach (var site in sites)
            {
                var listSlots = await operations.ListSlotsAsync(resourceGroupName, site.Name);

                slots.AddRange(listSlots);

                while (listSlots.NextPageLink != null)
                {
                    listSlots = await operations.ListSlotsNextAsync(listSlots.NextPageLink);

                    slots.AddRange(listSlots);
                }
            }

            return sites.Concat(slots).OrderBy(x => x.Name).ToArray();
        }
#endif

        public static async Task<IReadOnlyList<Certificate>> ListAllAsync(this ICertificatesOperations operations)
        {
            var certificates = new List<Certificate>();

            var list = await operations.ListAsync();

            certificates.AddRange(list);

            while (list.NextPageLink != null)
            {
                list = await operations.ListNextAsync(list.NextPageLink);

                certificates.AddRange(list);
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
                return await operations.GetAsync(resourceGroupName, zoneName, relativeRecordSetName, RecordType.TXT);
            }
            catch
            {
                return null;
            }
        }
    }
}
