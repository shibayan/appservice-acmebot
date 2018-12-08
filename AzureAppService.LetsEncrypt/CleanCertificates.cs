using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.Azure.Management.WebSites.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;

namespace AzureAppService.LetsEncrypt
{
    public static class CleanCertificates
    {
        [FunctionName("CleanCertificates")]
        public static async Task RunOrchestrator([OrchestrationTrigger] DurableOrchestrationContext context, ILogger log)
        {
            // 期限切れまで 30 日以内の証明書を取得する
            var certificates = await context.CallActivityAsync<IList<Certificate>>(nameof(SharedFunctions.GetCertificates), context.CurrentUtcDateTime);

            foreach (var certificate in certificates)
            {
                log.LogInformation($"{certificate.SubjectName} - {certificate.ExpirationDate}");
            }

            // 対象となる証明書がない場合は終わる
            if (certificates.Count == 0)
            {
                log.LogInformation("Certificates is not found");

                return;
            }

            // App Service を取得
            var sites = await context.CallActivityAsync<IList<Site>>(nameof(SharedFunctions.GetSites), null);

            // App Service にバインド済み証明書のサムプリントを取得
            var boundCertificates = sites.SelectMany(x => x.HostNameSslStates.Select(xs => xs.Thumbprint))
                                         .ToArray();

            var tasks = new List<Task>();

            // バインドされていない証明書を削除
            foreach (var certificate in certificates.Where(x => !boundCertificates.Contains(x.Thumbprint)))
            {
                tasks.Add(context.CallActivityAsync(nameof(SharedFunctions.DeleteCertificate), certificate));
            }

            // アクティビティの完了を待つ
            await Task.WhenAll(tasks);
        }

        [FunctionName("CleanCertificates_Timer")]
        public static async Task TimerStart([TimerTrigger("0 0 6 * * *")] TimerInfo timer, [OrchestrationClient] DurableOrchestrationClient starter, ILogger log)
        {
            // Function input comes from the request content.
            var instanceId = await starter.StartNewAsync("CleanCertificates", null);

            log.LogInformation($"Started orchestration with ID = '{instanceId}'.");
        }
    }
}