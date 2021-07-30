using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

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

namespace AppService.Acmebot.Functions
{
    public class GetSites : HttpFunctionBase
    {
        public GetSites(IHttpContextAccessor httpContextAccessor, AzureEnvironment environment)
            : base(httpContextAccessor)
        {
            _environment = environment;
        }

        private readonly AzureEnvironment _environment;

        [FunctionName(nameof(GetSites) + "_" + nameof(Orchestrator))]
        public async Task<IReadOnlyList<SiteItem>> Orchestrator([OrchestrationTrigger] IDurableOrchestrationContext context)
        {
            var resourceGroup = context.GetInput<string>();

            var activity = context.CreateActivityProxy<ISharedActivity>();

            var result = new List<SiteItem>();

            var certificates = await activity.GetAllCertificates();

            // App Service を取得
            var sites = await activity.GetSites((resourceGroup, true));

            foreach (var site in sites.ToLookup(x => x.SplitName().appName))
            {
                var siteInformation = new SiteItem { Name = site.Key, Slots = new List<SlotItem>() };

                foreach (var slot in site)
                {
                    var (_, slotName) = slot.SplitName();

                    var hostNameSslStates = slot.HostNameSslStates
                                                .Where(x => !x.Name.EndsWith(_environment.AppService) && !x.Name.EndsWith(_environment.TrafficManager));

                    var slotInformation = new SlotItem
                    {
                        Name = slotName ?? "production",
                        DnsNames = hostNameSslStates.Select(x => new DnsNameItem
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

        [FunctionName(nameof(GetSites) + "_" + nameof(HttpStart))]
        public async Task<IActionResult> HttpStart(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sites/{resourceGroup}")] HttpRequest req,
            string resourceGroup,
            [DurableClient] IDurableClient starter,
            ILogger log)
        {
            if (!User.IsAppAuthorized())
            {
                return Unauthorized();
            }

            // Function input comes from the request content.
            var instanceId = await starter.StartNewAsync(nameof(GetSites) + "_" + nameof(Orchestrator), null, resourceGroup);

            log.LogInformation($"Started orchestration with ID = '{instanceId}'.");

            return await starter.WaitForCompletionOrCreateCheckStatusResponseAsync(req, instanceId, TimeSpan.FromMinutes(1), returnInternalServerErrorOnFailure: true);
        }
    }
}
