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

public class GetResourceGroups
{
    [Function($"{nameof(GetResourceGroups)}_{nameof(Orchestrator)}")]
    public async Task<IReadOnlyList<ResourceGroupItem>> Orchestrator([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        try
        {
            // リソースグループを取得
            return await context.GetResourceGroups();
        }
        catch
        {
            return Array.Empty<ResourceGroupItem>();
        }
    }

    [Function($"{nameof(GetResourceGroups)}_{nameof(HttpStart)}")]
    public async Task<IActionResult> HttpStart(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "api/group")] HttpRequest req,
        [DurableClient] DurableTaskClient starter,
        FunctionContext functionContext)
    {
        var log = functionContext.GetLogger<GetResourceGroups>();

        if (!req.IsAppAuthorized())
        {
            return new UnauthorizedResult();
        }

        // Function input comes from the request content.
        var instanceId = await starter.ScheduleNewOrchestrationInstanceAsync($"{nameof(GetResourceGroups)}_{nameof(Orchestrator)}");

        log.LogInformation($"Started orchestration with ID = '{instanceId}'.");

        return await starter.WaitForCompletionResultAsync(instanceId, TimeSpan.FromMinutes(1));
    }
}
