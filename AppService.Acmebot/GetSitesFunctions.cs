﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using AppService.Acmebot.Contracts;
using AppService.Acmebot.Internal;
using AppService.Acmebot.Models;

using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace AppService.Acmebot
{
    public class GetSitesFunctions
    {
        [FunctionName(nameof(GetSitesInformation))]
        public async Task<IList<ResourceGroupInformation>> GetSitesInformation([OrchestrationTrigger] DurableOrchestrationContext context)
        {
            var activity = context.CreateActivityProxy<ISharedFunctions>();

            // App Service を取得
            var sites = await activity.GetSites();
            var certificates = await activity.GetAllCertificates();

            var result = new List<ResourceGroupInformation>();

            foreach (var item in sites.ToLookup(x => x.ResourceGroup))
            {
                var resourceGroup = new ResourceGroupInformation
                {
                    Name = item.Key,
                    Sites = new List<SiteInformation>()
                };

                foreach (var site in item.ToLookup(x => x.SplitName().siteName))
                {
                    var siteInformation = new SiteInformation
                    {
                        Name = site.Key,
                        Slots = new List<SlotInformation>()
                    };

                    foreach (var slot in site)
                    {
                        var (_, slotName) = slot.SplitName();

                        var hostNameSslStates = slot.HostNameSslStates
                                                    .Where(x => !x.Name.EndsWith(".azurewebsites.net"));

                        var slotInformation = new SlotInformation
                        {
                            Name = slotName ?? "production",
                            Domains = hostNameSslStates.Select(x => new DomainInformation
                            {
                                Name = x.Name,
                                Issuer = certificates.FirstOrDefault(xs => xs.Thumbprint == x.Thumbprint)?.Issuer ?? "None"
                            }).ToArray()
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

        [FunctionName(nameof(GetSitesInformation_HttpStart))]
        public async Task<HttpResponseMessage> GetSitesInformation_HttpStart(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "get-sites-information")] HttpRequestMessage req,
            [OrchestrationClient] DurableOrchestrationClient starter,
            ILogger log)
        {
            if (!req.Headers.Contains("X-MS-CLIENT-PRINCIPAL-ID"))
            {
                return req.CreateErrorResponse(HttpStatusCode.Unauthorized, $"Need to activate EasyAuth.");
            }

            // Function input comes from the request content.
            var instanceId = await starter.StartNewAsync(nameof(GetSitesInformation), null);

            log.LogInformation($"Started orchestration with ID = '{instanceId}'.");

            return await starter.WaitForCompletionOrCreateCheckStatusResponseAsync(req, instanceId, TimeSpan.FromSeconds(30));
        }
    }
}