using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;

namespace AppService.Acmebot.Functions;

public class PurgeCertificates
{
    [Function($"{nameof(PurgeCertificates)}_{nameof(Orchestrator)}")]
    public async Task Orchestrator([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var log = context.CreateReplaySafeLogger<PurgeCertificates>();

        // 期限切れまで 30 日以内の証明書を取得する
        var certificates = await context.GetExpiringCertificates(context.CurrentUtcDateTime);

        foreach (var certificate in certificates)
        {
            log.LogInformation($"{certificate.SubjectName} - {certificate.ExpirationOn}");
        }

        // 対象となる証明書がない場合は終わる
        if (certificates.Count == 0)
        {
            log.LogInformation("Certificates are not found");

            return;
        }

        var boundCertificates = new List<string>();

        var resourceGroups = await context.GetResourceGroups();

        foreach (var resourceGroup in resourceGroups)
        {
            // App Service を取得
            var webSites = await context.GetWebSites(resourceGroup.Name);

            foreach (var webSite in webSites)
            {
                // App Service にバインド済み証明書のサムプリントを取得
                boundCertificates.AddRange(webSite.HostNames.Select(x => x.Thumbprint));

                // Deployment Slot を取得
                var webSiteSlots = await context.GetWebSiteSlots((resourceGroup.Name, webSite.Name));

                // Deployment Slot にバインド済み証明書のサムプリントを取得
                boundCertificates.AddRange(webSiteSlots.SelectMany(x => x.HostNames.Select(xs => xs.Thumbprint)));
            }
        }

        log.LogInformation($"Certificates = {string.Join(",", certificates.Select(x => x.Thumbprint))}");
        log.LogInformation($"Bound certificates = {string.Join(",", boundCertificates)}");

        // バインドされていない証明書を削除
        var tasks = certificates.Where(x => !boundCertificates.Contains(x.Thumbprint)).Select(x => context.DeleteCertificate(x.Id));

        // アクティビティの完了を待つ
        await Task.WhenAll(tasks);
    }

    [Function($"{nameof(PurgeCertificates)}_{nameof(Timer)}")]
    public async Task Timer([TimerTrigger("0 0 0 1 * *")] TimerInfo timer, [DurableClient] DurableTaskClient starter, FunctionContext functionContext)
    {
        var log = functionContext.GetLogger<PurgeCertificates>();

        // Function input comes from the request content.
        var instanceId = await starter.ScheduleNewOrchestrationInstanceAsync($"{nameof(PurgeCertificates)}_{nameof(Orchestrator)}");

        log.LogInformation($"Started orchestration with ID = '{instanceId}'.");
    }
}
