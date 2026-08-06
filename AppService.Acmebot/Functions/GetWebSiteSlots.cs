using System;
using System.Collections.Generic;
using System.Linq;
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

public class GetWebSiteSlots
{
    [Function($"{nameof(GetWebSiteSlots)}_{nameof(Orchestrator)}")]
    public async Task<IReadOnlyList<WebSiteItem>> Orchestrator([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var (resourceGroupName, webSiteName) = context.GetInput<(string, string)>();

        var certificates = await context.GetAllCertificates();

        // App Service を取得
        var site = await context.GetWebSite((resourceGroupName, webSiteName, "production"));
        var sites = await context.GetWebSiteSlots((resourceGroupName, webSiteName));

        var webSites = sites.Prepend(site).Where(x => x.IsRunning && x.HasCustomDomain).ToArray();

        foreach (var hostName in webSites.SelectMany(x => x.HostNames))
        {
            hostName.Issuer = certificates.FirstOrDefault(x => x.Thumbprint == hostName.Thumbprint)?.Issuer ?? "None";
        }

        return webSites;
    }

    [Function($"{nameof(GetWebSiteSlots)}_{nameof(HttpStart)}")]
    public async Task<IActionResult> HttpStart(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "api/group/{resourceGroupName}/site/{webSiteName}/slot")] HttpRequest req,
        string resourceGroupName,
        string webSiteName,
        [DurableClient] DurableTaskClient starter,
        FunctionContext functionContext)
    {
        var log = functionContext.GetLogger<GetWebSiteSlots>();

        if (!req.IsAppAuthorized())
        {
            return new UnauthorizedResult();
        }

        // Function input comes from the request content.
        var instanceId = await starter.ScheduleNewOrchestrationInstanceAsync($"{nameof(GetWebSiteSlots)}_{nameof(Orchestrator)}", (resourceGroupName, webSiteName));

        log.LogInformation($"Started orchestration with ID = '{instanceId}'.");

        return await starter.WaitForCompletionResultAsync(instanceId, TimeSpan.FromMinutes(1));
    }
}
