using System;
using System.Collections.Generic;
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

namespace AppService.Acmebot.Functions;

public class GetWebSites : HttpFunctionBase
{
    public GetWebSites(IHttpContextAccessor httpContextAccessor)
        : base(httpContextAccessor)
    {
    }

    [FunctionName($"{nameof(GetWebSites)}_{nameof(Orchestrator)}")]
    public async Task<IReadOnlyList<WebSiteItem>> Orchestrator([OrchestrationTrigger] IDurableOrchestrationContext context)
    {
        var resourceGroupName = context.GetInput<string>();

        var activity = context.CreateActivityProxy<ISharedActivity>();

        try
        {
            // App Service を取得
            return await activity.GetWebSites(resourceGroupName);
        }
        catch
        {
            return Array.Empty<WebSiteItem>();
        }
    }

    [FunctionName($"{nameof(GetWebSites)}_{nameof(HttpStart)}")]
    public async Task<IActionResult> HttpStart(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "api/group/{resourceGroupName}/site")] HttpRequest req,
        string resourceGroupName,
        [DurableClient] IDurableClient starter,
        ILogger log)
    {
        if (!User.IsAppAuthorized())
        {
            return Unauthorized();
        }

        // Function input comes from the request content.
        var instanceId = await starter.StartNewAsync($"{nameof(GetWebSites)}_{nameof(Orchestrator)}", null, resourceGroupName);

        log.LogInformation($"Started orchestration with ID = '{instanceId}'.");

        return await starter.WaitForCompletionOrCreateCheckStatusResponseAsync(req, instanceId, TimeSpan.FromMinutes(1), returnInternalServerErrorOnFailure: true);
    }
}
