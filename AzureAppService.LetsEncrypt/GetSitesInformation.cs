﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using AzureAppService.LetsEncrypt.Internal;

using Microsoft.Azure.Management.WebSites.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;

using Newtonsoft.Json;

namespace AzureAppService.LetsEncrypt
{
    public static class GetSitesInformation
    {
        [FunctionName("GetSitesInformation")]
        public static async Task<IList<ResourceGroupInformation>> RunOrchestrator([OrchestrationTrigger] DurableOrchestrationContext context)
        {
            // App Service を取得
            var sites = await context.CallActivityAsync<IList<Site>>(nameof(SharedFunctions.GetSites), null);

            var result = new List<ResourceGroupInformation>();

            foreach (var item in sites.ToLookup(x => x.ResourceGroup))
            {
                var resourceGroup = new ResourceGroupInformation
                {
                    Name = item.Key,
                    Sites = new List<SiteInformation>()
                };

                foreach (var site in item.ToLookup(x => x.SiteName()))
                {
                    var siteInformation = new SiteInformation
                    {
                        Name = site.Key,
                        Slots = new List<SlotInformation>()
                    };

                    foreach (var slot in site)
                    {
                        var slotInformation = new SlotInformation
                        {
                            Name = slot.SlotName() ?? "production",
                            Domains = slot.HostNameSslStates
                                          .Where(x => x.SslState == SslState.Disabled && !x.Name.EndsWith(".azurewebsites.net"))
                                          .Select(x => x.Name)
                                          .ToArray()
                        };

                        if (slotInformation.Domains.Count != 0)
                        {
                            siteInformation.Slots.Add(slotInformation);
                        }
                    }

                    if (siteInformation.Slots.Count != 0)
                    {
                        resourceGroup.Sites.Add(siteInformation);
                    }
                }

                if (resourceGroup.Sites.Count != 0)
                {
                    result.Add(resourceGroup);
                }
            }

            return result;
        }

        [FunctionName("GetSitesInformation_HttpStart")]
        public static async Task<HttpResponseMessage> HttpStart(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "get-sites-information")] HttpRequestMessage req,
            [OrchestrationClient] DurableOrchestrationClient starter,
            ILogger log)
        {
            if (!req.Headers.Contains("X-MS-CLIENT-PRINCIPAL-ID"))
            {
                return req.CreateErrorResponse(HttpStatusCode.Unauthorized, $"Need to activate EasyAuth.");
            }

            // Function input comes from the request content.
            var instanceId = await starter.StartNewAsync("GetSitesInformation", null);

            log.LogInformation($"Started orchestration with ID = '{instanceId}'.");

            return await starter.WaitForCompletionOrCreateCheckStatusResponseAsync(req, instanceId, TimeSpan.FromSeconds(30));
        }
    }

    public class ResourceGroupInformation
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("sites")]
        public IList<SiteInformation> Sites { get; set; }
    }

    public class SiteInformation
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("slots")]
        public IList<SlotInformation> Slots { get; set; }
    }

    public class SlotInformation
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("domains")]
        public IList<string> Domains { get; set; }
    }
}