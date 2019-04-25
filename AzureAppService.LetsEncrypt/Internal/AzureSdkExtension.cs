using System.Threading;
using System.Threading.Tasks;

using Microsoft.Azure.Management.WebSites;
using Microsoft.Azure.Management.WebSites.Models;

namespace AzureAppService.LetsEncrypt.Internal
{
    internal static class AzureSdkExtension
    {
        public static Task<SiteConfigResource> GetConfigurationAsync(this IWebAppsOperations operations, Site site, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (site.IsSlot())
            {
                var (siteName, slotName) = site.SplitName();

                return operations.GetConfigurationSlotAsync(site.ResourceGroup, siteName, slotName, cancellationToken);
            }

            return operations.GetConfigurationAsync(site.ResourceGroup, site.Name, cancellationToken);
        }

        public static Task<SiteConfigResource> UpdateConfigurationAsync(this IWebAppsOperations operations, Site site, SiteConfigResource siteConfig, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (site.IsSlot())
            {
                var (siteName, slotName) = site.SplitName();

                return operations.UpdateConfigurationSlotAsync(site.ResourceGroup, siteName, siteConfig, slotName, cancellationToken);
            }

            return operations.UpdateConfigurationAsync(site.ResourceGroup, site.Name, siteConfig, cancellationToken);
        }

        public static Task<User> ListPublishingCredentialsAsync(this IWebAppsOperations operations, Site site, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (site.IsSlot())
            {
                var (siteName, slotName) = site.SplitName();

                return operations.ListPublishingCredentialsSlotAsync(site.ResourceGroup, siteName, slotName, cancellationToken);
            }

            return operations.ListPublishingCredentialsAsync(site.ResourceGroup, site.Name, cancellationToken);
        }

        public static Task<Site> CreateOrUpdateAsync(this IWebAppsOperations operations, Site site, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (site.IsSlot())
            {
                var (siteName, slotName) = site.SplitName();

                return operations.CreateOrUpdateSlotAsync(site.ResourceGroup, siteName, site, slotName, cancellationToken);
            }

            return operations.CreateOrUpdateAsync(site.ResourceGroup, site.Name, site, cancellationToken);
        }
    }
}
