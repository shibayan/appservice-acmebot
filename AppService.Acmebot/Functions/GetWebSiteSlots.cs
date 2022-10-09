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

namespace AppService.Acmebot.Functions;

public class GetWebSiteSlots : HttpFunctionBase
{
    public GetWebSiteSlots(IHttpContextAccessor httpContextAccessor)
        : base(httpContextAccessor)
    {
    }

    [FunctionName($"{nameof(GetWebSiteSlots)}_{nameof(Orchestrator)}")]
    public async Task<IReadOnlyList<WebSiteItem>> Orchestrator([OrchestrationTrigger] IDurableOrchestrationContext context)
    {
        var (resourceGroupName, webSiteName) = context.GetInput<(string, string)>();

        var activity = context.CreateActivityProxy<ISharedActivity>();

        var certificates = await activity.GetAllCertificates();

        // App Service を取得
        var site = await activity.GetWebSite((resourceGroupName, webSiteName, "production"));
        var sites = await activity.GetWebSiteSlots((resourceGroupName, webSiteName));

        var webSites = sites.Prepend(site).Where(x => x.IsRunning && x.HasCustomDomain).ToArray();

        foreach (var hostName in webSites.SelectMany(x => x.HostNames))
        {
            hostName.Issuer = certificates.FirstOrDefault(x => x.Thumbprint == hostName.Thumbprint)?.Issuer ?? "None";
        }

        return webSites;
    }

    [FunctionName($"{nameof(GetWebSiteSlots)}_{nameof(HttpStart)}")]
    public async Task<IActionResult> HttpStart(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "api/group/{resourceGroupName}/site/{webSiteName}/slot")] HttpRequest req,
        string resourceGroupName,
        string webSiteName,
        [DurableClient] IDurableClient starter,
        ILogger log)
    {
        if (!User.IsAppAuthorized())
        {
            return Unauthorized();
        }

        // Function input comes from the request content.
        var instanceId = await starter.StartNewAsync($"{nameof(GetWebSiteSlots)}_{nameof(Orchestrator)}", null, (resourceGroupName, webSiteName));

        log.LogInformation($"Started orchestration with ID = '{instanceId}'.");

        return await starter.WaitForCompletionOrCreateCheckStatusResponseAsync(req, instanceId, TimeSpan.FromMinutes(1), returnInternalServerErrorOnFailure: true);
    }
}
