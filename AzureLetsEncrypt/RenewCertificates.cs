using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using ACMESharp.Protocol;

using Microsoft.Azure.Management.WebSites.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;

namespace AzureLetsEncrypt
{
    public static class RenewCertificates
    {
        [FunctionName("RenewCertificates")]
        public static async Task RunOrchestrator([OrchestrationTrigger] DurableOrchestrationContext context, ILogger log)
        {
            // 期限切れまで 30 日以内の証明書を取得する
            var certificates = await context.CallActivityAsync<IList<Certificate>>(nameof(SharedFunctions.GetCertificates), context.CurrentUtcDateTime);

            foreach (var certificate in certificates)
            {
                log.LogInformation($"{certificate.SubjectName} - {certificate.ExpirationDate}");
            }

            // 更新対象となる証明書がない場合は終わる
            if (certificates.Count == 0)
            {
                return;
            }

            // App Service を取得
            var sites = await context.CallActivityAsync<IList<Site>>(nameof(SharedFunctions.GetSites), null);

            // サイト単位で証明書の更新を行う
            foreach (var site in sites)
            {
                // 期限切れが近い証明書がバインドされているか確認
                var hostNames = site.HostNameSslStates
                                            .Where(x => !x.Name.EndsWith(".azurewebsites.net") && certificates.Any(xs => xs.Thumbprint == x.Thumbprint))
                                            .Select(x => x.Name)
                                            .ToArray();

                // 対象となる証明書が存在しない場合はスキップ
                if (hostNames.Length == 0)
                {
                    continue;
                }

                // 証明書の更新処理を開始
                await context.CallSubOrchestratorAsync(nameof(RenewSiteCertificates), (site, hostNames));
            }
        }

        [FunctionName(nameof(RenewSiteCertificates))]
        public static async Task RenewSiteCertificates([OrchestrationTrigger] DurableOrchestrationContext context, ILogger log)
        {
            var (site, hostNames) = context.GetInput<(Site, string[])>();

            log.LogInformation($"Site name: {site.Name}");

            foreach (var hostNameSslState in site.HostNameSslStates.Where(x => hostNames.Contains(x.Name)))
            {
                var orderDetails = await context.CallActivityAsync<OrderDetails>(nameof(SharedFunctions.Order), hostNameSslState.Name);

                var authzUrl = orderDetails.Payload.Authorizations.First();

                // ACME Challenge を実行
                if (hostNameSslState.Name.StartsWith("*") || site.Kind.Contains("container") || site.Kind.Contains("linux"))
                {
                    // ワイルドカード、コンテナ、Linux の場合は DNS-01 を利用する
                    await context.CallActivityAsync(nameof(SharedFunctions.Dns01Authorization), (site, hostNameSslState.Name, authzUrl));
                }
                else
                {
                    // それ以外は HTTP-01 を利用する
                    await context.CallActivityAsync(nameof(SharedFunctions.UpdateSettings), site);

                    await context.CallActivityAsync(nameof(SharedFunctions.Http01Authorization), (site, authzUrl));
                }

                if (!await context.CallActivityAsync<bool>(nameof(SharedFunctions.WaitChallenge), orderDetails))
                {
                    log.LogError($"Cannot generate certificate: {hostNameSslState.Name}");
                    continue;
                }

                var (thumbprint, pfxBlob) = await context.CallActivityAsync<(string, byte[])>(nameof(SharedFunctions.FinalizeOrder), (hostNameSslState, orderDetails));

                await context.CallActivityAsync(nameof(SharedFunctions.UpdateCertificate), (site, $"{hostNameSslState.Name}-{thumbprint}", pfxBlob));

                hostNameSslState.Thumbprint = thumbprint;
                hostNameSslState.ToUpdate = true;
            }

            await context.CallActivityAsync(nameof(SharedFunctions.UpdateSiteBinding), site);
        }

        [FunctionName("RenewCertificates_Timer")]
        public static async Task TimerStart([TimerTrigger("0 0 0 * * *")] TimerInfo timer, [OrchestrationClient] DurableOrchestrationClient starter, ILogger log)
        {
            // Function input comes from the request content.
            var instanceId = await starter.StartNewAsync("RenewCertificates", null);

            log.LogInformation($"Started orchestration with ID = '{instanceId}'.");
        }
    }
}