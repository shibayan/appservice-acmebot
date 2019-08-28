using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

using Microsoft.Azure.Management.WebSites.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace AppService.Acmebot
{
    public class AddCertificate
    {
        [FunctionName("AddCertificate")]
        public async Task RunOrchestrator([OrchestrationTrigger] DurableOrchestrationContext context, ILogger log)
        {
            var request = context.GetInput<AddCertificateRequest>();

            var proxy = context.CreateActivityProxy<ISharedFunctions>();

            var site = await proxy.GetSite((request.ResourceGroupName, request.SiteName, request.SlotName));

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
                await proxy.Dns01Precondition(request.Domains);
            }
            else
            {
                await proxy.Http01Precondition(site);
            }

            // 新しく ACME Order を作成する
            var orderDetails = await proxy.Order(request.Domains);

            // 複数の Authorizations を処理する
            var challenges = new List<ChallengeResult>();

            foreach (var authorization in orderDetails.Payload.Authorizations)
            {
                ChallengeResult result;

                // ACME Challenge を実行
                if (useDns01Auth)
                {
                    // DNS-01 を使う
                    result = await proxy.Dns01Authorization((authorization, context.ParentInstanceId ?? context.InstanceId));

                    // Azure DNS で正しくレコードが引けるか確認
                    await proxy.CheckDnsChallenge(result);
                }
                else
                {
                    // HTTP-01 を使う
                    result = await proxy.Http01Authorization((site, authorization));

                    // HTTP で正しくアクセスできるか確認
                    await proxy.CheckHttpChallenge(result);
                }

                challenges.Add(result);
            }

            // ACME Answer を実行
            await proxy.AnswerChallenges(challenges);

            // Order のステータスが ready になるまで 60 秒待機
            await proxy.CheckIsReady(orderDetails);

            // Order の最終処理を実行し PFX を作成
            var (thumbprint, pfxBlob) = await proxy.FinalizeOrder((request.Domains, orderDetails));

            await proxy.UpdateCertificate((site, $"{request.Domains[0]}-{thumbprint}", pfxBlob));

            foreach (var hostNameSslState in hostNameSslStates)
            {
                hostNameSslState.Thumbprint = thumbprint;
                hostNameSslState.SslState = request.UseIpBasedSsl ?? false ? SslState.IpBasedEnabled : SslState.SniEnabled;
                hostNameSslState.ToUpdate = true;
            }

            await proxy.UpdateSiteBinding(site);
        }

        [FunctionName("AddCertificate_HttpStart")]
        public async Task<HttpResponseMessage> HttpStart(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "add-certificate")] HttpRequestMessage req,
            [OrchestrationClient] DurableOrchestrationClient starter,
            ILogger log)
        {
            if (!req.Headers.Contains("X-MS-CLIENT-PRINCIPAL-ID"))
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

            return await starter.WaitForCompletionOrCreateCheckStatusResponseAsync(req, instanceId, TimeSpan.FromMinutes(5));
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