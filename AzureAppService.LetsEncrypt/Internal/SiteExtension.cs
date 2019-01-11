﻿using System.Linq;

using Microsoft.Azure.Management.WebSites.Models;

namespace AzureAppService.LetsEncrypt.Internal
{
    internal static class SiteExtension
    {
        public static bool IsSlot(this Site site)
        {
            return site.Name.Contains('/');
        }

        public static string SiteName(this Site site)
        {
            return site.SplitName().Item1;
        }

        public static string SlotName(this Site site)
        {
            return site.SplitName().Item2;
        }

        public static (string, string) SplitName(this Site site)
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
