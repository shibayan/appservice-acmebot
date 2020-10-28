using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using AppService.Acmebot.Contracts;
using AppService.Acmebot.Internal;
using AppService.Acmebot.Models;

using Azure.WebJobs.Extensions.HttpApi;

using DurableTask.TypedProxy;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace AppService.Acmebot
{
    public class GetSitesFunctions : HttpFunctionBase
    {
        public GetSitesFunctions(IHttpContextAccessor httpContextAccessor, IAzureEnvironment environment)
            : base(httpContextAccessor)
        {
            _environment = environment;
        }

        private readonly IAzureEnvironment _environment;

        [FunctionName(nameof(GetSitesInformation))]
        public async Task<IReadOnlyList<SiteInformation>> GetSitesInformation([OrchestrationTrigger] IDurableOrchestrationContext context)
        {
            var resourceGroup = context.GetInput<string>();

            var activity = context.CreateActivityProxy<ISharedFunctions>();

            var result = new List<SiteInformation>();

            var certificates = await activity.GetAllCertificates();

            // App Service を取得
            var sites = await activity.GetSites((resourceGroup, true));

            foreach (var site in sites.ToLookup(x => x.SplitName().appName))
            {
                var siteInformation = new SiteInformation { Name = site.Key, Slots = new List<SlotInformation>() };

                foreach (var slot in site)
                {
                    var (_, slotName) = slot.SplitName();

                    var hostNameSslStates = slot.HostNameSslStates
                                                .Where(x => !x.Name.EndsWith(_environment.AppService) && !x.Name.EndsWith(_environment.TrafficManager));

                    var slotInformation = new SlotInformation
                    {
                        Name = slotName ?? "production",
                        DnsNames = hostNameSslStates.Select(x => new DnsNameInformation
                        {
                            Name = x.Name,
                            Issuer = certificates.FirstOrDefault(xs => xs.Thumbprint == x.Thumbprint)?.Issuer ?? "None"
                        }).ToArray()
                    };

                    if (slotInformation.DnsNames.Count != 0)
                    {
                        siteInformation.Slots.Add(slotInformation);
                    }
                }

                if (siteInformation.Slots.Count != 0)
                {
                    result.Add(siteInformation);
                }
            }

            return result;
        }

        [FunctionName(nameof(GetSitesInformation_HttpStart))]
        public async Task<IActionResult> GetSitesInformation_HttpStart(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "get-sites/{resourceGroup}")] HttpRequest req,
            string resourceGroup,
            [DurableClient] IDurableClient starter,
            ILogger log)
        {
            if (!User.Identity.IsAuthenticated)
            {
                return Unauthorized();
            }

            // Function input comes from the request content.
            var instanceId = await starter.StartNewAsync(nameof(GetSitesInformation), null, resourceGroup);

            log.LogInformation($"Started orchestration with ID = '{instanceId}'.");

            return await starter.WaitForCompletionOrCreateCheckStatusResponseAsync(req, instanceId, TimeSpan.FromSeconds(60));
        }
    }
}
