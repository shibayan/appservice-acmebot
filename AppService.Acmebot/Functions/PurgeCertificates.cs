using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using DurableTask.TypedProxy;

using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Extensions.Logging;

namespace AppService.Acmebot.Functions
{
    public class PurgeCertificates
    {
        [FunctionName(nameof(PurgeCertificates) + "_" + nameof(Orchestrator))]
        public async Task Orchestrator([OrchestrationTrigger] IDurableOrchestrationContext context, ILogger log)
        {
            var activity = context.CreateActivityProxy<ISharedActivity>();

            // 期限切れまで 30 日以内の証明書を取得する
            var certificates = await activity.GetExpiringCertificates(context.CurrentUtcDateTime);

            foreach (var certificate in certificates)
            {
                log.LogInformation($"{certificate.SubjectName} - {certificate.ExpirationDate}");
            }

            // 対象となる証明書がない場合は終わる
            if (certificates.Count == 0)
            {
                log.LogInformation("Certificates are not found");

                return;
            }

            var resourceGroups = await activity.GetResourceGroups();

            foreach (var resourceGroup in resourceGroups)
            {
                // App Service を取得
                var sites = await activity.GetSites((resourceGroup.Name, false));

                // App Service にバインド済み証明書のサムプリントを取得
                var boundCertificates = sites.SelectMany(x => x.HostNameSslStates.Select(xs => xs.Thumbprint))
                                             .ToArray();

                var tasks = new List<Task>();

                // バインドされていない証明書を削除
                foreach (var certificate in certificates.Where(x => !boundCertificates.Contains(x.Thumbprint)))
                {
                    tasks.Add(activity.DeleteCertificate(certificate));
                }

                // アクティビティの完了を待つ
                await Task.WhenAll(tasks);
            }
        }

        [FunctionName(nameof(PurgeCertificates) + "_" + nameof(Timer))]
        public async Task Timer([TimerTrigger("0 0 0 * * 1,3,5")] TimerInfo timer, [DurableClient] IDurableClient starter, ILogger log)
        {
            // Function input comes from the request content.
            var instanceId = await starter.StartNewAsync(nameof(PurgeCertificates) + "_" + nameof(Orchestrator));

            log.LogInformation($"Started orchestration with ID = '{instanceId}'.");
        }
    }
}
