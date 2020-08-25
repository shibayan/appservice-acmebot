﻿using System.Linq;
using System.Threading.Tasks;

using AppService.Acmebot.Contracts;
using AppService.Acmebot.Internal;
using AppService.Acmebot.Models;

using Azure.WebJobs.Extensions.HttpApi;

using DurableTask.TypedProxy;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Management.WebSites.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace AppService.Acmebot
{
    public class AddCertificateFunctions : HttpFunctionBase
    {
        public AddCertificateFunctions(IHttpContextAccessor httpContextAccessor)
            : base(httpContextAccessor)
        {
        }

        [FunctionName(nameof(AddCertificate))]
        public async Task AddCertificate([OrchestrationTrigger] IDurableOrchestrationContext context, ILogger log)
        {
            var request = context.GetInput<AddCertificateRequest>();

            var activity = context.CreateActivityProxy<ISharedFunctions>();

            var site = await activity.GetSite((request.ResourceGroupName, request.AppName, request.SlotName));

            if (site == null)
            {
                log.LogError($"{request.AppName} is not found");
                return;
            }

            var hostNameSslStates = site.HostNameSslStates
                                        .Where(x => request.DnsNames.Contains(x.Name))
                                        .ToArray();

            if (hostNameSslStates.Length != request.DnsNames.Length)
            {
                foreach (var dnsName in request.DnsNames.Except(hostNameSslStates.Select(x => x.Name)))
                {
                    log.LogError($"{dnsName} is not found");
                }
                return;
            }

            var asciiDnsNames = request.DnsNames.Select(Punycode.Encode).ToArray();

            try
            {
                // 証明書を発行し Azure にアップロード
                var certificate = await context.CallSubOrchestratorAsync<Certificate>(nameof(SharedFunctions.IssueCertificate), (site, asciiDnsNames, request.ForceDns01Challenge ?? false));

                // App Service のホスト名に証明書をセットする
                foreach (var hostNameSslState in hostNameSslStates)
                {
                    hostNameSslState.Thumbprint = certificate.Thumbprint;
                    hostNameSslState.SslState = request.UseIpBasedSsl ?? false ? SslState.IpBasedEnabled : SslState.SniEnabled;
                    hostNameSslState.ToUpdate = true;
                }

                await activity.UpdateSiteBinding(site);

                // 証明書の更新が完了後に Webhook を送信する
                await activity.SendCompletedEvent((site, certificate.ExpirationDate, asciiDnsNames));
            }
            finally
            {
                // クリーンアップ処理を実行
                await activity.CleanupVirtualApplication(site);
            }
        }

        [FunctionName(nameof(AddCertificate_HttpStart))]
        public async Task<IActionResult> AddCertificate_HttpStart(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "add-certificate")] AddCertificateRequest request,
            [DurableClient] IDurableClient starter,
            ILogger log)
        {
            if (!User.Identity.IsAuthenticated)
            {
                return Unauthorized();
            }

            if (!TryValidateModel(request))
            {
                return ValidationProblem(ModelState);
            }

            // Function input comes from the request content.
            var instanceId = await starter.StartNewAsync(nameof(AddCertificate), request);

            log.LogInformation($"Started orchestration with ID = '{instanceId}'.");

            return AcceptedAtFunction(nameof(AddCertificate_HttpPoll), new { instanceId }, null);
        }

        [FunctionName(nameof(AddCertificate_HttpPoll))]
        public async Task<IActionResult> AddCertificate_HttpPoll(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "add-certificate/{instanceId}")] HttpRequest req,
            string instanceId,
            [DurableClient] IDurableClient starter)
        {
            if (!User.Identity.IsAuthenticated)
            {
                return Unauthorized();
            }

            var status = await starter.GetStatusAsync(instanceId);

            if (status == null)
            {
                return BadRequest();
            }

            if (status.RuntimeStatus == OrchestrationRuntimeStatus.Failed)
            {
                return Problem(status.Output.ToString());
            }

            if (status.RuntimeStatus == OrchestrationRuntimeStatus.Running ||
                status.RuntimeStatus == OrchestrationRuntimeStatus.Pending ||
                status.RuntimeStatus == OrchestrationRuntimeStatus.ContinuedAsNew)
            {
                return AcceptedAtFunction(nameof(AddCertificate_HttpPoll), new { instanceId }, null);
            }

            return Ok();
        }
    }
}
