using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AppService.Acmebot.Models;

using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;

namespace AppService.Acmebot.Functions;

public class SharedOrchestrator
{
    [Function(nameof(IssueCertificate))]
    public async Task<CertificateItem> IssueCertificate([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var (webSite, dnsNames, forceDns01Challenge) = context.GetInput<(WebSiteItem, string[], bool)>();

        // ワイルドカード、コンテナ、Linux の場合は DNS-01 を利用する
        var useDns01Auth = forceDns01Challenge || dnsNames.Any(x => x.StartsWith("*")) || webSite.Kind.Contains("container") || webSite.Kind.Contains("linux");

        // 前提条件をチェック
        if (useDns01Auth)
        {
            await context.Dns01Precondition(dnsNames);
        }
        else
        {
            await context.Http01Precondition(webSite.Id);
        }

        // 新しく ACME Order を作成する
        var orderDetails = await context.Order(dnsNames);

        // 既に確認済みの場合は Challenge をスキップする
        if (orderDetails.Payload.Status != "ready")
        {
            // 複数の Authorizations を処理する
            IReadOnlyList<AcmeChallengeResult> challengeResults;

            // ACME Challenge を実行
            if (useDns01Auth)
            {
                challengeResults = await context.Dns01Authorization(orderDetails.Payload.Authorizations);

                // DNS レコードの変更が伝搬するまで 10 秒遅延させる
                await context.CreateTimer(context.CurrentUtcDateTime.AddSeconds(10), CancellationToken.None);

                // Azure DNS で正しくレコードが引けるか確認
                await context.CheckDnsChallenge(challengeResults);
            }
            else
            {
                challengeResults = await context.Http01Authorization((webSite.Id, orderDetails.Payload.Authorizations));

                // HTTP で正しくアクセスできるか確認
                await context.CheckHttpChallenge(challengeResults);
            }

            // ACME Answer を実行
            await context.AnswerChallenges(challengeResults);

            // Order のステータスが ready になるまで 60 秒待機
            await context.CheckIsReady((orderDetails, challengeResults));

            if (useDns01Auth)
            {
                // 作成した DNS レコードを削除
                await context.CleanupDnsChallenge(challengeResults);
            }
        }

        // CSR を作成し Finalize を実行
        var (finalize, rsaParameters) = await context.FinalizeOrder((dnsNames, orderDetails));

        // Finalize の時点でステータスが valid の時点はスキップ
        if (finalize.Payload.Status != "valid")
        {
            // Finalize 後のステータスが valid になるまで 60 秒待機
            finalize = await context.CheckIsValid(finalize);
        }

        // 証明書をダウンロードし App Service へアップロード
        var certificate = await context.UploadCertificate((webSite.Id, dnsNames[0], forceDns01Challenge, finalize, rsaParameters));

        return certificate;
    }
}
