using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AppService.Acmebot.Models;

using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;

namespace AppService.Acmebot.Functions;

public class RenewCertificates
{
    [Function($"{nameof(RenewCertificates)}_{nameof(Orchestrator)}")]
    public async Task Orchestrator([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var log = context.CreateReplaySafeLogger<RenewCertificates>();

        // 期限切れまで 30 日以内の証明書を取得する
        var certificates = await context.GetExpiringCertificates(context.CurrentUtcDateTime);

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
        var resourceGroups = await context.GetResourceGroups();

        foreach (var resourceGroup in resourceGroups)
        {
            // App Service を取得
            var webSites = await context.GetWebSites(resourceGroup.Name);

            // サイト単位で証明書の更新を行う
            foreach (var webSite in webSites)
            {
                var webSiteSlots = await context.GetWebSiteSlots((resourceGroup.Name, webSite.Name));

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

    [Function($"{nameof(RenewCertificates)}_{nameof(SubOrchestrator)}")]
    public async Task SubOrchestrator([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var log = context.CreateReplaySafeLogger<RenewCertificates>();

        var (webSite, certificates) = context.GetInput<(WebSiteItem, CertificateItem[])>();

        log.LogInformation($"Site name: {webSite.Name}");

        try
        {
            // 証明書単位で更新を行う
            foreach (var certificate in certificates)
            {
                try
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
                    var newCertificate = await context.CallSubOrchestratorAsync<CertificateItem>(nameof(SharedOrchestrator.IssueCertificate), (webSite, dnsNames, forceDns01Challenge), _issueRetryOptions);

                    await context.UpdateSiteBinding((webSite.Id, dnsNames, newCertificate.Thumbprint, null));

                    // 証明書の更新が完了後に Webhook を送信する
                    await context.SendCompletedEvent((webSite, newCertificate.ExpirationOn, dnsNames));
                }
                catch (Exception ex)
                {
                    // Observed on a domain where the DNS setup had been changed to point to a completely different system.
                    // Do not let individual renewal failures block other renewals.
                    log.LogError($"Failed renewal in sub orchestration for Certificate = {certificate.Thumbprint}");
                    log.LogError(ex.Message);

                    // Replaces the in-process lifecycle-notification webhook on orchestration failure.
                    await context.SendFailedEvent(($"{nameof(RenewCertificates)}_{nameof(SubOrchestrator)}", $"{certificate.SubjectName}: {ex.Message}"));
                }
            }
        }
        finally
        {
            // クリーンアップ処理を実行
            await context.CleanupVirtualApplication(webSite.Id);
        }
    }

    [Function($"{nameof(RenewCertificates)}_{nameof(Timer)}")]
    public async Task Timer([TimerTrigger("0 0 0 * * *")] TimerInfo timer, [DurableClient] DurableTaskClient starter, FunctionContext functionContext)
    {
        var log = functionContext.GetLogger<RenewCertificates>();

        // Function input comes from the request content.
        var instanceId = await starter.ScheduleNewOrchestrationInstanceAsync($"{nameof(RenewCertificates)}_{nameof(Orchestrator)}");

        log.LogInformation($"Started orchestration with ID = '{instanceId}'.");
    }

    private static readonly TaskOptions _issueRetryOptions = TaskOptions.FromRetryPolicy(new RetryPolicy(2, TimeSpan.FromHours(3))
    {
        HandleFailure = failure => failure.IsCausedBy<RetriableOrchestratorException>()
    });
}
