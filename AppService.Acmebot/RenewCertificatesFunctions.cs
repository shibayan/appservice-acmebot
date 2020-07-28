﻿using System;
using System.Linq;
using System.Threading.Tasks;

using AppService.Acmebot.Contracts;
using AppService.Acmebot.Internal;

using DurableTask.TypedProxy;

using Microsoft.Azure.Management.WebSites.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Extensions.Logging;

namespace AppService.Acmebot
{
    public class RenewCertificatesFunctions
    {
        [FunctionName(nameof(RenewCertificates))]
        public async Task RenewCertificates([OrchestrationTrigger] IDurableOrchestrationContext context, ILogger log)
        {
            var activity = context.CreateActivityProxy<ISharedFunctions>();

            // 期限切れまで 30 日以内の証明書を取得する
            var certificates = await activity.GetCertificates(context.CurrentUtcDateTime);

            foreach (var certificate in certificates)
            {
                log.LogInformation($"{certificate.SubjectName} - {certificate.ExpirationDate}");
            }

            // 更新対象となる証明書がない場合は終わる
            if (certificates.Count == 0)
            {
                log.LogInformation("Certificates are not found");

                return;
            }

            // App Service を取得
            var sites = await activity.GetSites();

            // サイト単位で証明書の更新を行う
            foreach (var site in sites)
            {
                // 期限切れが近い証明書がバインドされているか確認
                var boundCertificates = certificates.Where(x => site.HostNameSslStates.Any(xs => xs.Thumbprint == x.Thumbprint))
                                                    .ToArray();

                // 対象となる証明書が存在しない場合はスキップ
                if (boundCertificates.Length == 0)
                {
                    continue;
                }

                try
                {
                    // 証明書の更新処理を開始
                    await context.CallSubOrchestratorAsync(nameof(RenewSiteCertificates), (site, boundCertificates));
                }
                catch (Exception ex)
                {
                    // 失敗した場合はログに詳細を書き出して続きを実行する
                    log.LogError($"Failed sub orchestration with Certificates = {string.Join(",", boundCertificates.Select(x => x.Thumbprint))}");
                    log.LogError(ex.Message);
                }
            }
        }

        [FunctionName(nameof(RenewSiteCertificates))]
        public async Task RenewSiteCertificates([OrchestrationTrigger] IDurableOrchestrationContext context, ILogger log)
        {
            var (site, certificates) = context.GetInput<(Site, Certificate[])>();

            var activity = context.CreateActivityProxy<ISharedFunctions>();

            log.LogInformation($"Site name: {site.Name}");

            try
            {
                // 証明書単位で更新を行う
                foreach (var certificate in certificates)
                {
                    log.LogInformation($"Subject name: {certificate.SubjectName}");

                    // IDN に対して証明書を発行すると SANs に Punycode 前の DNS 名が入るので除外
                    var dnsNames = certificate.HostNames
                                              .Where(x => !x.Contains(" (") && site.HostNames.Contains(x))
                                              .ToArray();

                    var forceDns01Challenge = certificate.Tags.TryGetValue("ForceDns01Challenge", out var value) ? bool.Parse(value) : false;

                    // 証明書を発行し Azure にアップロード
                    var newCertificate = await context.CallSubOrchestratorAsync<Certificate>(nameof(SharedFunctions.IssueCertificate), (site, dnsNames, forceDns01Challenge));

                    foreach (var hostNameSslState in site.HostNameSslStates.Where(x => dnsNames.Contains(Punycode.Encode(x.Name))))
                    {
                        hostNameSslState.Thumbprint = newCertificate.Thumbprint;
                        hostNameSslState.ToUpdate = true;
                    }

                    await activity.UpdateSiteBinding(site);

                    // 証明書の更新が完了後に Webhook を送信する
                    await activity.SendCompletedEvent((site, newCertificate.ExpirationDate, dnsNames));
                }
            }
            finally
            {
                // クリーンアップ処理を実行
                await activity.CleanupVirtualApplication(site);
            }
        }

        [FunctionName(nameof(RenewCertificates_Timer))]
        public async Task RenewCertificates_Timer(
            [TimerTrigger("0 0 0 * * 1,3,5")] TimerInfo timer,
            [DurableClient] IDurableClient starter,
            ILogger log)
        {
            // Function input comes from the request content.
            var instanceId = await starter.StartNewAsync(nameof(RenewCertificates), null);

            log.LogInformation($"Started orchestration with ID = '{instanceId}'.");
        }
    }
}
