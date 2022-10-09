using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AppService.Acmebot.Models;

using DurableTask.TypedProxy;

using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Extensions.Logging;

namespace AppService.Acmebot.Functions;

public class RenewCertificates
{
    [FunctionName($"{nameof(RenewCertificates)}_{nameof(Orchestrator)}")]
    public async Task Orchestrator([OrchestrationTrigger] IDurableOrchestrationContext context, ILogger log)
    {
        var activity = context.CreateActivityProxy<ISharedActivity>();

        // 期限切れまで 30 日以内の証明書を取得する
        var certificates = await activity.GetExpiringCertificates(context.CurrentUtcDateTime);

        // 更新対象となる証明書がない場合は終わる
        if (certificates.Count == 0)
        {
            log.LogInformation("Certificates are not found");

            return;
        }

        foreach (var certificate in certificates)
        {
            log.LogInformation($"Expiring certificate : {certificate.SubjectName} - {certificate.ExpirationOn}");
        }

        // スロットリング対策として 600 秒以内でジッターを追加する
        var jitter = (uint)context.NewGuid().GetHashCode() % 600;

        await context.CreateTimer(context.CurrentUtcDateTime.AddSeconds(jitter), CancellationToken.None);

        // リソースグループ単位で証明書の更新を行う
        var resourceGroups = await activity.GetResourceGroups();

        foreach (var resourceGroup in resourceGroups)
        {
            // App Service を取得
            var webSites = await activity.GetWebSites(resourceGroup.Name);

            // サイト単位で証明書の更新を行う
            foreach (var webSite in webSites)
            {
                var webSiteSlots = await activity.GetWebSiteSlots((resourceGroup.Name, webSite.Name));

                foreach (var webSiteSlot in webSiteSlots.Prepend(webSite).Where(x => x.IsRunning))
                {
                    // 期限切れが近い証明書がバインドされているか確認
                    var boundCertificates = certificates.Where(x => webSiteSlot.HostNames.Any(xs => xs.Thumbprint == x.Thumbprint))
                                                        .ToArray();

                    // 対象となる証明書が存在しない場合はスキップ
                    if (boundCertificates.Length == 0)
                    {
                        continue;
                    }

                    try
                    {
                        // 証明書の更新処理を開始
                        await context.CallSubOrchestratorAsync($"{nameof(RenewCertificates)}_{nameof(SubOrchestrator)}", (webSiteSlot, boundCertificates));
                    }
                    catch (Exception ex)
                    {
                        // 失敗した場合はログに詳細を書き出して続きを実行する
                        log.LogError($"Failed sub orchestration with Certificates = {string.Join(",", boundCertificates.Select(x => x.Thumbprint))}");
                        log.LogError(ex.Message);
                    }
                }
            }
        }
    }

    [FunctionName($"{nameof(RenewCertificates)}_{nameof(SubOrchestrator)}")]
    public async Task SubOrchestrator([OrchestrationTrigger] IDurableOrchestrationContext context, ILogger log)
    {
        var (webSite, certificates) = context.GetInput<(WebSiteItem, CertificateItem[])>();

        var activity = context.CreateActivityProxy<ISharedActivity>();

        log.LogInformation($"Site name: {webSite.Name}");

        try
        {
            // 証明書単位で更新を行う
            foreach (var certificate in certificates)
            {
                log.LogInformation($"Subject name: {certificate.SubjectName}");

                // IDN に対して証明書を発行すると SANs に Punycode 前の DNS 名が入るので除外
                var dnsNames = certificate.HostNames
                                          .Where(x => !x.Contains(" (") && webSite.HostNames.Any(xs => xs.Name == x))
                                          .ToArray();

                // 更新対象の DNS 名が空の時はログを出して終了
                if (dnsNames.Length == 0)
                {
                    log.LogWarning($"DnsNames are empty. Certificate HostNames: {string.Join(",", certificate.HostNames)}, Site HostNames: {string.Join(",", webSite.HostNames.Select(x => x.Name))}");

                    continue;
                }

                var forceDns01Challenge = certificate.Tags.TryGetValue("ForceDns01Challenge", out var value) && bool.Parse(value);

                // 証明書を発行し Azure にアップロード
                var newCertificate = await context.CallSubOrchestratorWithRetryAsync<CertificateItem>(nameof(SharedOrchestrator.IssueCertificate), _retryOptions, (webSite, dnsNames, forceDns01Challenge));

                await activity.UpdateSiteBinding((webSite.Id, dnsNames, newCertificate.Thumbprint, null));

                // 証明書の更新が完了後に Webhook を送信する
                await activity.SendCompletedEvent((webSite, newCertificate.ExpirationOn, dnsNames));
            }
        }
        finally
        {
            // クリーンアップ処理を実行
            await activity.CleanupVirtualApplication(webSite.Id);
        }
    }

    [FunctionName($"{nameof(RenewCertificates)}_{nameof(Timer)}")]
    public async Task Timer([TimerTrigger("0 0 0 * * *")] TimerInfo timer, [DurableClient] IDurableClient starter, ILogger log)
    {
        // Function input comes from the request content.
        var instanceId = await starter.StartNewAsync($"{nameof(RenewCertificates)}_{nameof(Orchestrator)}");

        log.LogInformation($"Started orchestration with ID = '{instanceId}'.");
    }

    private readonly RetryOptions _retryOptions = new(TimeSpan.FromHours(3), 2)
    {
        Handle = ex => ex.InnerException?.InnerException is RetriableOrchestratorException
    };
}
