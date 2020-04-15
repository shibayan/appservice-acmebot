using System.Linq;

using Microsoft.Azure.Management.WebSites.Models;

namespace AppService.Acmebot.Internal
{
    internal static class SiteExtensions
    {
        public static bool IsSlot(this Site site)
        {
            return site.Name.Contains('/');
        }

        public static (string appName, string slotName) SplitName(this Site site)
        {
            var index = site.Name.IndexOf('/');

            if (index == -1)
            {
                return (site.Name, null);
            }

            return (site.Name.Substring(0, index), site.Name.Substring(index + 1));
        }

        public static string ScmSiteUrl(this Site site)
        {
            return site.HostNameSslStates.First(x => x.HostType == HostType.Repository).Name;
        }
    }
}
