using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using AppService.Acmebot.Contracts;
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
    public class GetResourceGroupsFunctions : HttpFunctionBase
    {
        public GetResourceGroupsFunctions(IHttpContextAccessor httpContextAccessor)
            : base(httpContextAccessor)
        {
        }

        [FunctionName(nameof(GetResourceGroupsInformation))]
        public async Task<IList<ResourceGroupInformation>> GetResourceGroupsInformation([OrchestrationTrigger] IDurableOrchestrationContext context)
        {
            var activity = context.CreateActivityProxy<ISharedFunctions>();

            var resourceGroups = await activity.GetResourceGroups();

            return resourceGroups.Select(x => new ResourceGroupInformation { Name = x.Name }).ToArray();
        }

        [FunctionName(nameof(GetResourceGroupsInformation_HttpStart))]
        public async Task<IActionResult> GetResourceGroupsInformation_HttpStart(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "get-resource-groups")] HttpRequest req,
            [DurableClient] IDurableClient starter,
            ILogger log)
        {
            if (!User.Identity.IsAuthenticated)
            {
                return Unauthorized();
            }

            // Function input comes from the request content.
            var instanceId = await starter.StartNewAsync(nameof(GetResourceGroupsInformation), null);

            log.LogInformation($"Started orchestration with ID = '{instanceId}'.");

            return await starter.WaitForCompletionOrCreateCheckStatusResponseAsync(req, instanceId, TimeSpan.FromSeconds(60));
        }
    }
}
