using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Threading.Tasks;

using ACMESharp.Protocol;
using ACMESharp.Protocol.Resources;

using Microsoft.Azure.Management.WebSites.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace AzureAppService.LetsEncrypt
{
    public static class AddCertificate
    {
        [FunctionName("AddCertificate")]
        public static async Task RunOrchestrator([OrchestrationTrigger] DurableOrchestrationContext context, ILogger log)
        {
            var request = context.GetInput<AddCertificateRequest>();

            var site = await context.CallActivityAsync<Site>(nameof(SharedFunctions.GetSite), (request.ResourceGroupName, request.SiteName, request.SlotName));

            if (site == null)
            {
                log.LogError($"{request.SiteName} is not found");
                return;
            }

            var hostNameSslStates = site.HostNameSslStates
                                        .Where(x => request.Domains.Contains(x.Name))
                                        .ToArray();

            if (hostNameSslStates.Length != request.Domains.Length)
            {
                foreach (var hostName in request.Domains.Except(hostNameSslStates.Select(x => x.Name)))
                {
                    log.LogError($"{hostName} is not found");
                }
                return;
            }

            // ワイルドカード、コンテナ、Linux の場合は DNS-01 を利用する
            var useDns01Auth = request.Domains.Any(x => x.StartsWith("*")) || site.Kind.Contains("container") || site.Kind.Contains("linux");

            // 前提条件をチェック
            if (useDns01Auth)
            {
                await context.CallActivityAsync(nameof(SharedFunctions.Dns01Precondition), request.Domains);
            }
            else
            {
                await context.CallActivityAsync(nameof(SharedFunctions.Http01Precondition), site);
            }

            // 新しく ACME Order を作成する
            var orderDetails = await context.CallActivityAsync<OrderDetails>(nameof(SharedFunctions.Order), request.Domains);

            // 複数の Authorizations を処理する
            var challenges = new List<Challenge>();

            foreach (var authorization in orderDetails.Payload.Authorizations)
            {
                // ACME Challenge を実行
                if (useDns01Auth)
                {
                    challenges.Add(await context.CallActivityAsync<Challenge>(nameof(SharedFunctions.Dns01Authorization), authorization));
                }
                else
                {
                    challenges.Add(await context.CallActivityAsync<Challenge>(nameof(SharedFunctions.Http01Authorization), (site, authorization)));
                }
            }

            // ACME Answer を実行
            await context.CallActivityAsync(nameof(SharedFunctions.AnswerChallenges), challenges);

            // Order のステータスが ready になるまで 60 秒待機
            await context.CallActivityWithRetryAsync(nameof(SharedFunctions.CheckIsReady), new RetryOptions(TimeSpan.FromSeconds(5), 12), orderDetails);

            // Order の最終処理を実行し PFX を作成
            var (thumbprint, pfxBlob) = await context.CallActivityAsync<(string, byte[])>(nameof(SharedFunctions.FinalizeOrder), (request.Domains, orderDetails));

            await context.CallActivityAsync(nameof(SharedFunctions.UpdateCertificate), (site, $"{request.Domains[0]}-{thumbprint}", pfxBlob));

            foreach (var hostNameSslState in hostNameSslStates)
            {
                hostNameSslState.Thumbprint = thumbprint;
                hostNameSslState.SslState = request.UseIpBasedSsl ?? false ? SslState.IpBasedEnabled : SslState.SniEnabled;
                hostNameSslState.ToUpdate = true;
            }

            await context.CallActivityAsync(nameof(SharedFunctions.UpdateSiteBinding), site);
        }

        [FunctionName("AddCertificate_HttpStart")]
        public static async Task<HttpResponseMessage> HttpStart(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "add-certificate")] HttpRequestMessage req,
            [OrchestrationClient] DurableOrchestrationClient starter,
            ClaimsPrincipal principal,
            ILogger log)
        {
            if (!principal.Identity.IsAuthenticated)
            {
                return req.CreateErrorResponse(HttpStatusCode.Unauthorized, $"Need to activate EasyAuth.");
            }

            var request = await req.Content.ReadAsAsync<AddCertificateRequest>();

            if (string.IsNullOrEmpty(request.ResourceGroupName))
            {
                return req.CreateErrorResponse(HttpStatusCode.BadRequest, $"{nameof(request.ResourceGroupName)} is empty.");
            }

            if (string.IsNullOrEmpty(request.SiteName))
            {
                return req.CreateErrorResponse(HttpStatusCode.BadRequest, $"{nameof(request.SiteName)} is empty.");
            }

            if (request.Domains == null || request.Domains.Length == 0)
            {
                return req.CreateErrorResponse(HttpStatusCode.BadRequest, $"{nameof(request.Domains)} is empty.");
            }

            // Function input comes from the request content.
            var instanceId = await starter.StartNewAsync("AddCertificate", request);

            log.LogInformation($"Started orchestration with ID = '{instanceId}'.");

            return starter.CreateCheckStatusResponse(req, instanceId);
        }
    }

    public class AddCertificateRequest
    {
        public string ResourceGroupName { get; set; }
        public string SiteName { get; set; }
        public string SlotName { get; set; }
        public string[] Domains { get; set; }
        public bool? UseIpBasedSsl { get; set; }
    }
}