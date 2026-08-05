using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
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

public class AddCertificate
{
    [Function($"{nameof(AddCertificate)}_{nameof(Orchestrator)}")]
    public async Task Orchestrator([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var log = context.CreateReplaySafeLogger<AddCertificate>();

        var request = context.GetInput<AddCertificateRequest>();

        var webSite = await context.GetWebSite((request.ResourceGroupName, request.WebSiteName, request.SlotName));

        if (webSite is null)
        {
            log.LogError($"{request.WebSiteName} is not found");
            return;
        }

        var hostNames = webSite.HostNames
                               .Where(x => request.DnsNames.Contains(x.Name))
                               .ToArray();

        if (hostNames.Length != request.DnsNames.Length)
        {
            foreach (var dnsName in request.DnsNames.Except(hostNames.Select(x => x.Name)))
            {
                log.LogError($"{dnsName} is not found");
            }

            return;
        }

        var asciiDnsNames = request.DnsNames.Select(Punycode.Encode).ToArray();

        try
        {
            // 証明書を発行し Azure にアップロード
            var certificate = await context.CallSubOrchestratorAsync<CertificateItem>(nameof(SharedOrchestrator.IssueCertificate), (webSite, asciiDnsNames, request.ForceDns01Challenge ?? false));

            // App Service のホスト名に証明書をセットする
            await context.UpdateSiteBinding((webSite.Id, request.DnsNames, certificate.Thumbprint, request.UseIpBasedSsl));

            // 証明書の更新が完了後に Webhook を送信する
            await context.SendCompletedEvent((webSite, certificate.ExpirationOn, asciiDnsNames));
        }
        catch (Exception ex)
        {
            // Replaces the in-process lifecycle-notification webhook on orchestration failure.
            await context.SendFailedEvent(($"{nameof(AddCertificate)}_{nameof(Orchestrator)}", ex.Message));

            throw;
        }
        finally
        {
            // クリーンアップ処理を実行
            await context.CleanupVirtualApplication(webSite.Id);
        }
    }

    [Function($"{nameof(AddCertificate)}_{nameof(HttpStart)}")]
    public async Task<IActionResult> HttpStart(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "api/certificate")] HttpRequest req,
        [DurableClient] DurableTaskClient starter,
        FunctionContext functionContext)
    {
        var log = functionContext.GetLogger<AddCertificate>();

        if (!req.IsAppAuthorized())
        {
            return new UnauthorizedResult();
        }

        AddCertificateRequest request;

        try
        {
            request = await req.ReadFromJsonAsync<AddCertificateRequest>();
        }
        catch (Exception)
        {
            return new BadRequestObjectResult("Request body is not valid JSON.");
        }

        if (request is null)
        {
            return new BadRequestObjectResult("Request body is required.");
        }

        var validationResults = new List<ValidationResult>();

        if (!Validator.TryValidateObject(request, new ValidationContext(request), validationResults, validateAllProperties: true))
        {
            return new BadRequestObjectResult(validationResults.Select(x => x.ErrorMessage));
        }

        // Function input comes from the request content.
        var instanceId = await starter.ScheduleNewOrchestrationInstanceAsync($"{nameof(AddCertificate)}_{nameof(Orchestrator)}", request);

        log.LogInformation($"Started orchestration with ID = '{instanceId}'.");

        return new AcceptedResult($"/api/certificate/{instanceId}", null);
    }

    [Function($"{nameof(AddCertificate)}_{nameof(HttpPoll)}")]
    public async Task<IActionResult> HttpPoll(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "api/certificate/{instanceId}")] HttpRequest req,
        string instanceId,
        [DurableClient] DurableTaskClient starter)
    {
        if (!req.IsAppAuthorized())
        {
            return new UnauthorizedResult();
        }

        var metadata = await starter.GetInstanceAsync(instanceId);

        if (metadata is null)
        {
            return new BadRequestResult();
        }

        if (metadata.RuntimeStatus == OrchestrationRuntimeStatus.Failed)
        {
            return new ObjectResult(metadata.FailureDetails?.ErrorMessage ?? "Orchestration failed.")
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }

        if (metadata.RuntimeStatus is OrchestrationRuntimeStatus.Running or OrchestrationRuntimeStatus.Pending or OrchestrationRuntimeStatus.Suspended)
        {
            return new AcceptedResult($"/api/certificate/{instanceId}", null);
        }

        return new OkResult();
    }
}
