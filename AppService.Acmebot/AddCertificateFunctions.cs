using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using AppService.Acmebot.Contracts;
using AppService.Acmebot.Models;

using DurableTask.TypedProxy;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Management.WebSites.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;

using Newtonsoft.Json;

namespace AppService.Acmebot
{
    public class AddCertificateFunctions
    {
        [FunctionName(nameof(AddCertificate))]
        public async Task AddCertificate([OrchestrationTrigger] IDurableOrchestrationContext context, ILogger log)
        {
            var request = context.GetInput<AddCertificateRequest>();

            var activity = context.CreateActivityProxy<ISharedFunctions>();

            var site = await activity.GetSite((request.ResourceGroupName, request.SiteName, request.SlotName));

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
                await activity.Dns01Precondition(request.Domains);
            }
            else
            {
                await activity.Http01Precondition(site);
            }

            // 新しく ACME Order を作成する
            var orderDetails = await activity.Order(request.Domains);

            IList<AcmeChallengeResult> challengeResults;

            // ACME Challenge を実行
            if (useDns01Auth)
            {
                // DNS-01 を使う
                challengeResults = await activity.Dns01Authorization(orderDetails.Payload.Authorizations);

                // Azure DNS で正しくレコードが引けるか確認
                await activity.CheckDnsChallenge(challengeResults);
            }
            else
            {
                // HTTP-01 を使う
                challengeResults = await activity.Http01Authorization((site, orderDetails.Payload.Authorizations));

                // HTTP で正しくアクセスできるか確認
                await activity.CheckHttpChallenge(challengeResults);
            }

            // ACME Answer を実行
            await activity.AnswerChallenges(challengeResults);

            // Order のステータスが ready になるまで 60 秒待機
            await activity.CheckIsReady(orderDetails);

            // Order の最終処理を実行し PFX を作成
            var (thumbprint, pfxBlob) = await activity.FinalizeOrder((request.Domains, orderDetails));

            await activity.UpdateCertificate((site, $"{request.Domains[0]}-{thumbprint}", pfxBlob));

            foreach (var hostNameSslState in hostNameSslStates)
            {
                hostNameSslState.Thumbprint = thumbprint;
                hostNameSslState.SslState = request.UseIpBasedSsl ?? false ? SslState.IpBasedEnabled : SslState.SniEnabled;
                hostNameSslState.ToUpdate = true;
            }

            await activity.UpdateSiteBinding(site);

            // クリーンアップ処理を実行
            await activity.CleanupVirtualApplication(site);
        }

        [FunctionName(nameof(AddCertificate_HttpStart))]
        public async Task<IActionResult> AddCertificate_HttpStart(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "add-certificate")] HttpRequest req,
            [DurableClient] IDurableClient starter,
            ILogger log)
        {
            if (!req.HttpContext.User.Identity.IsAuthenticated)
            {
                return new UnauthorizedObjectResult("Need to activate EasyAuth.");
            }

            var request = JsonConvert.DeserializeObject<AddCertificateRequest>(await req.ReadAsStringAsync());

            if (string.IsNullOrEmpty(request.ResourceGroupName))
            {
                return new BadRequestObjectResult($"{nameof(request.ResourceGroupName)} is empty.");
            }

            if (string.IsNullOrEmpty(request.SiteName))
            {
                return new BadRequestObjectResult($"{nameof(request.SiteName)} is empty.");
            }

            if (request.Domains == null || request.Domains.Length == 0)
            {
                return new BadRequestObjectResult($"{nameof(request.Domains)} is empty.");
            }

            // Function input comes from the request content.
            var instanceId = await starter.StartNewAsync(nameof(AddCertificate), request);

            log.LogInformation($"Started orchestration with ID = '{instanceId}'.");

            return starter.CreateCheckStatusResponse(req, instanceId, true);
        }
    }
}
