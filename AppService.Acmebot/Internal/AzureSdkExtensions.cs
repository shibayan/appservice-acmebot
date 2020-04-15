using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Azure.Management.Dns;
using Microsoft.Azure.Management.Dns.Models;
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

        public static async Task<IList<Site>> ListAllAsync(this IWebAppsOperations operations)
        {
            var sites = new List<Site>();

            var list = await operations.ListAsync();

            sites.AddRange(list);

            while (list.NextPageLink != null)
            {
                list = await operations.ListNextAsync(list.NextPageLink);

                sites.AddRange(list);
            }

            return sites;
        }

        public static async Task<IList<Certificate>> ListAllAsync(this ICertificatesOperations operations)
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

        public static async Task<IList<Zone>> ListAllAsync(this IZonesOperations operations)
        {
            var zones = new List<Zone>();

            var list = await operations.ListAsync();

            zones.AddRange(list);

            while (list.NextPageLink != null)
            {
                list = await operations.ListNextAsync(list.NextPageLink);

                zones.AddRange(list);
            }

            return zones;
        }

        public static async Task<RecordSet> GetOrDefaultAsync(this IRecordSetsOperations operations, string resourceGroupName, string zoneName, string relativeRecordSetName, RecordType recordType)
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
