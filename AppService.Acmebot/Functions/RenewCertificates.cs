using System;
using System.Collections.Generic;
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

        var startedAt = context.CurrentUtcDateTime;

        // 期限切れまで 30 日以内の証明書を取得する
        var certificates = await context.GetExpiringCertificates(context.CurrentUtcDateTime);

        // 更新対象となる証明書がない場合は終わる
        if (certificates.Count == 0)
        {
            log.LogInformation("Certificates are not found");

            // The digest includes the full inventory count so a silently empty
            // certificate list (seen once with an api-version regression) is
            // distinguishable from a genuinely quiet night.
            var inventory = await context.GetAllCertificates();

            await SendRenewalSummarySafe(context, log, new RenewalRunSummary
            {
                ExpiringCount = 0,
                InventoryCount = inventory.Count,
                DurationMinutes = (context.CurrentUtcDateTime - startedAt).TotalMinutes,
                Results = Array.Empty<CertificateRenewalResult>()
            });

            return;
        }

        foreach (var certificate in certificates)
        {
            log.LogInformation($"Expiring certificate : {certificate.SubjectName} - {certificate.ExpirationOn}");
        }

        // スロットリング対策として 600 秒以内でジッターを追加する
        var jitter = (uint)context.NewGuid().GetHashCode() % 600;

        await context.CreateTimer(context.CurrentUtcDateTime.AddSeconds(jitter), CancellationToken.None);

        try
        {
            // Settle the ACME account once, BEFORE the fan-out: parallel cert
            // orchestrations must only ever read an existing account, never race
            // into registering one (see EnsureAcmeAccount).
            await context.EnsureAcmeAccount();
        }
        catch (Exception ex)
        {
            log.LogError($"ACME account preflight failed: {ex.Message}");

            await SendRenewalSummarySafe(context, log, new RenewalRunSummary
            {
                ExpiringCount = certificates.Count,
                DurationMinutes = (context.CurrentUtcDateTime - startedAt).TotalMinutes,
                Results = new[]
                {
                    new CertificateRenewalResult
                    {
                        DnsNames = "(entire run)",
                        Outcome = CertificateRenewalResult.Failed,
                        Detail = $"ACME account preflight failed: {ex.Message}"
                    }
                }
            });

            return;
        }

        // リソースグループ単位で証明書の更新を行う
        var resourceGroups = await context.GetResourceGroups();

        var results = new List<CertificateRenewalResult>();

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
                        var subResults = await context.CallSubOrchestratorAsync<IReadOnlyList<CertificateRenewalResult>>($"{nameof(RenewCertificates)}_{nameof(SubOrchestrator)}", (webSiteSlot, boundCertificates));

                        results.AddRange(subResults);
                    }
                    catch (Exception ex)
                    {
                        // 失敗した場合はログに詳細を書き出して続きを実行する
                        log.LogError($"Failed sub orchestration with Certificates = {string.Join(",", boundCertificates.Select(x => x.Thumbprint))}");
                        log.LogError(ex.Message);

                        results.Add(new CertificateRenewalResult
                        {
                            DnsNames = string.Join(",", boundCertificates.Select(x => x.SubjectName)),
                            Outcome = CertificateRenewalResult.Failed,
                            Detail = $"site fan-out failure on {webSiteSlot.Name}: {ex.Message}"
                        });
                    }
                }
            }
        }

        await SendRenewalSummarySafe(context, log, new RenewalRunSummary
        {
            ExpiringCount = certificates.Count,
            DurationMinutes = (context.CurrentUtcDateTime - startedAt).TotalMinutes,
            Results = results
        });
    }

    private static async Task SendRenewalSummarySafe(TaskOrchestrationContext context, ILogger log, RenewalRunSummary summary)
    {
        try
        {
            await context.SendRenewalSummary(summary);
        }
        catch (Exception ex)
        {
            // The digest is best-effort and must never fail the run itself.
            log.LogError($"Failed to send renewal digest: {ex.Message}");
        }
    }

    [Function($"{nameof(RenewCertificates)}_{nameof(SubOrchestrator)}")]
    public async Task<IReadOnlyList<CertificateRenewalResult>> SubOrchestrator([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var log = context.CreateReplaySafeLogger<RenewCertificates>();

        var (webSite, certificates) = context.GetInput<(WebSiteItem, CertificateItem[])>();

        log.LogInformation($"Site name: {webSite.Name}");

        // 証明書単位で更新を行う (in parallel, so one slow or retrying renewal never
        // delays the others; still-valid certificates are scheduled before expired ones)
        var ordered = certificates
            .OrderBy(x => x.ExpirationOn < context.CurrentUtcDateTime)
            .ThenBy(x => x.ExpirationOn)
            .ToArray();

        var renewals = ordered
            .Select(certificate => context.CallSubOrchestratorAsync<CertificateRenewalResult>($"{nameof(RenewCertificates)}_{nameof(CertOrchestrator)}", (webSite, certificate)))
            .ToArray();

        try
        {
            await Task.WhenAll(renewals);
        }
        catch (Exception ex)
        {
            // Individual failures are handled inside CertOrchestrator and reported per
            // task below; this only catches unexpected fan-out failures.
            log.LogError($"Unexpected renewal fan-out failure for site {webSite.Name}: {ex.Message}");
        }
        finally
        {
            // クリーンアップ処理を実行
            await context.CleanupVirtualApplication(webSite.Id);
        }

        var results = new List<CertificateRenewalResult>(ordered.Length);

        for (var i = 0; i < ordered.Length; i++)
        {
            results.Add(renewals[i].IsCompletedSuccessfully
                ? renewals[i].Result
                : new CertificateRenewalResult
                {
                    DnsNames = ordered[i].SubjectName,
                    Outcome = CertificateRenewalResult.Failed,
                    Detail = renewals[i].Exception?.GetBaseException().Message ?? "unexpected fan-out failure"
                });
        }

        return results;
    }

    [Function($"{nameof(RenewCertificates)}_{nameof(CertOrchestrator)}")]
    public async Task<CertificateRenewalResult> CertOrchestrator([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var log = context.CreateReplaySafeLogger<RenewCertificates>();

        var (webSite, certificate) = context.GetInput<(WebSiteItem, CertificateItem)>();

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

                return new CertificateRenewalResult
                {
                    DnsNames = certificate.SubjectName,
                    Outcome = CertificateRenewalResult.Skipped,
                    Detail = "no certificate host names bound to the site"
                };
            }

            // Fast-skip renewals that cannot or should not proceed (grace-period or
            // disabled tenants, DNS no longer pointing at us) before the ACME dance.
            var skipReason = await context.CheckRenewalPreconditions(dnsNames);

            if (skipReason is not null)
            {
                log.LogWarning($"Skipping renewal for {string.Join(",", dnsNames)}: {skipReason}");

                return new CertificateRenewalResult
                {
                    DnsNames = string.Join(",", dnsNames),
                    Outcome = CertificateRenewalResult.Skipped,
                    Detail = skipReason
                };
            }

            var forceDns01Challenge = certificate.Tags.TryGetValue("ForceDns01Challenge", out var value) && bool.Parse(value);

            // 証明書を発行し Azure にアップロード
            var newCertificate = await context.CallSubOrchestratorAsync<CertificateItem>(nameof(SharedOrchestrator.IssueCertificate), (webSite, dnsNames, forceDns01Challenge), _issueRetryOptions);

            await context.UpdateSiteBinding((webSite.Id, dnsNames, newCertificate.Thumbprint, null));

            return new CertificateRenewalResult
            {
                DnsNames = string.Join(",", dnsNames),
                Outcome = CertificateRenewalResult.Renewed,
                Detail = $"expires {newCertificate.ExpirationOn:yyyy-MM-dd}"
            };
        }
        catch (Exception ex)
        {
            // Observed on a domain where the DNS setup had been changed to point to a completely different system.
            // Do not let individual renewal failures block other renewals.
            log.LogError($"Failed renewal in sub orchestration for Certificate = {certificate.Thumbprint}");
            log.LogError(ex.Message);

            // The per-run digest replaces the per-certificate completed/failed webhooks
            // in the renewal flow (AddCertificate keeps its immediate events).
            return new CertificateRenewalResult
            {
                DnsNames = certificate.SubjectName,
                Outcome = CertificateRenewalResult.Failed,
                Detail = ex.Message
            };
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
