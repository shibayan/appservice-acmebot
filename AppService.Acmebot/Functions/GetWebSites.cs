using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using AppService.Acmebot.Internal;
using AppService.Acmebot.Models;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;

namespace AppService.Acmebot.Functions;

public class GetWebSites
{
    [Function($"{nameof(GetWebSites)}_{nameof(Orchestrator)}")]
    public async Task<IReadOnlyList<WebSiteItem>> Orchestrator([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var resourceGroupName = context.GetInput<string>();

        try
        {
            // App Service を取得
            return await context.GetWebSites(resourceGroupName);
        }
        catch
        {
            return Array.Empty<WebSiteItem>();
        }
    }

    [Function($"{nameof(GetWebSites)}_{nameof(HttpStart)}")]
    public async Task<IActionResult> HttpStart(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "api/group/{resourceGroupName}/site")] HttpRequest req,
        string resourceGroupName,
        [DurableClient] DurableTaskClient starter,
        FunctionContext functionContext)
    {
        var log = functionContext.GetLogger<GetWebSites>();

        if (!req.IsAppAuthorized())
        {
            return new UnauthorizedResult();
        }

        // Function input comes from the request content.
        var instanceId = await starter.ScheduleNewOrchestrationInstanceAsync($"{nameof(GetWebSites)}_{nameof(Orchestrator)}", resourceGroupName);

        log.LogInformation($"Started orchestration with ID = '{instanceId}'.");

        return await starter.WaitForCompletionResultAsync(instanceId, TimeSpan.FromMinutes(1));
    }
}
