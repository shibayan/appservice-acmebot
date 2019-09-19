using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using AppService.Acmebot.Contracts;

using DurableTask.Core;

using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;

namespace AppService.Acmebot
{
    public class PurgeFunctions
    {
        [FunctionName(nameof(PurgeCertificates))]
        public async Task PurgeCertificates([OrchestrationTrigger] DurableOrchestrationContext context, ILogger log)
        {
            var proxy = context.CreateActivityProxy<ISharedFunctions>();

            // 期限切れまで 30 日以内の証明書を取得する
            var certificates = await proxy.GetCertificates(context.CurrentUtcDateTime);

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

            // App Service を取得
            var sites = await proxy.GetSites();

            // App Service にバインド済み証明書のサムプリントを取得
            var boundCertificates = sites.SelectMany(x => x.HostNameSslStates.Select(xs => xs.Thumbprint))
                                         .ToArray();

            var tasks = new List<Task>();

            // バインドされていない証明書を削除
            foreach (var certificate in certificates.Where(x => !boundCertificates.Contains(x.Thumbprint)))
            {
                tasks.Add(proxy.DeleteCertificate(certificate));
            }

            // アクティビティの完了を待つ
            await Task.WhenAll(tasks);
        }

        [FunctionName(nameof(PurgeCertificates_Timer))]
        public async Task PurgeCertificates_Timer(
            [TimerTrigger("0 0 6 * * 0")] TimerInfo timer,
            [OrchestrationClient] DurableOrchestrationClient starter,
            ILogger log)
        {
            // Function input comes from the request content.
            var instanceId = await starter.StartNewAsync(nameof(PurgeCertificates), null);

            log.LogInformation($"Started orchestration with ID = '{instanceId}'.");
        }

        [FunctionName(nameof(PurgeInstanceHistory_Timer))]
        public Task PurgeInstanceHistory_Timer(
            [TimerTrigger("0 0 6 * * 0")] TimerInfo timer,
            [OrchestrationClient] DurableOrchestrationClient starter)
        {
            return starter.PurgeInstanceHistoryAsync(
                DateTime.MinValue,
                DateTime.UtcNow.AddDays(-30),
                new []
                {
                    OrchestrationStatus.Completed,
                    OrchestrationStatus.Failed
                });
        }
    }
}