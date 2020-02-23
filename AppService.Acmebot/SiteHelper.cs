using Microsoft.Azure.Management.WebSites.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AppService.Acmebot
{
    public static class SiteHelper
    {
        public static bool needDns01Auth(IList<string> hostnames,Site site)
        {
          return  hostnames.Any(x => x.StartsWith("*")) ||
                site.Kind.Contains("container") ||
                (site.Kind.Contains("linux") && !site.Tags.ContainsKey("CertBot"));
        }
    }
}
