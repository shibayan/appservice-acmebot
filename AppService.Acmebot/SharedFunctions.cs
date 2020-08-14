using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

using ACMESharp.Authorizations;
using ACMESharp.Crypto;
using ACMESharp.Protocol;

using AppService.Acmebot.Contracts;
using AppService.Acmebot.Internal;
using AppService.Acmebot.Models;
using AppService.Acmebot.Options;

using DnsClient;

using DurableTask.TypedProxy;

using Microsoft.Azure.Management.Dns;
using Microsoft.Azure.Management.Dns.Models;
using Microsoft.Azure.Management.ResourceManager;
using Microsoft.Azure.Management.ResourceManager.Models;
using Microsoft.Azure.Management.WebSites;
using Microsoft.Azure.Management.WebSites.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Extensions.Options;

namespace AppService.Acmebot
{
    public class SharedFunctions : ISharedFunctions
    {
        public SharedFunctions(IHttpClientFactory httpClientFactory, IAzureEnvironment environment, LookupClient lookupClient,
                               IAcmeProtocolClientFactory acmeProtocolClientFactory, IKuduClientFactory kuduClientFactory,
                               WebSiteManagementClient webSiteManagementClient, DnsManagementClient dnsManagementClient,
                               ResourceManagementClient resourceManagementClient, WebhookClient webhookClient, IOptions<AcmebotOptions> options)
        {
            _httpClientFactory = httpClientFactory;
            _environment = environment;
            _lookupClient = lookupClient;
            _acmeProtocolClientFactory = acmeProtocolClientFactory;
            _kuduClientFactory = kuduClientFactory;
            _webSiteManagementClient = webSiteManagementClient;
            _dnsManagementClient = dnsManagementClient;
            _resourceManagementClient = resourceManagementClient;
            _webhookClient = webhookClient;
            _options = options.Value;
        }

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IAzureEnvironment _environment;
        private readonly LookupClient _lookupClient;
        private readonly IAcmeProtocolClientFactory _acmeProtocolClientFactory;
        private readonly IKuduClientFactory _kuduClientFactory;
        private readonly WebSiteManagementClient _webSiteManagementClient;
        private readonly DnsManagementClient _dnsManagementClient;
        private readonly ResourceManagementClient _resourceManagementClient;
        private readonly WebhookClient _webhookClient;
        private readonly AcmebotOptions _options;

        private const string IssuerName = "Acmebot";

        [FunctionName(nameof(IssueCertificate))]
        public async Task<Certificate> IssueCertificate([OrchestrationTrigger] IDurableOrchestrationContext context)
        {
            var (site, dnsNames, forceDns01Challenge) = context.GetInput<(Site, string[], bool)>();

            var activity = context.CreateActivityProxy<ISharedFunctions>();

            // ワイルドカード、コンテナ、Linux の場合は DNS-01 を利用する
            var useDns01Auth = forceDns01Challenge || dnsNames.Any(x => x.StartsWith("*")) || site.Kind.Contains("container") || site.Kind.Contains("linux");

            // 前提条件をチェック
            if (useDns01Auth)
            {
                await activity.Dns01Precondition(dnsNames);
            }
            else
            {
                await activity.Http01Precondition(site);
            }

            // 新しく ACME Order を作成する
            var orderDetails = await activity.Order(dnsNames);

            // 複数の Authorizations を処理する
            IList<AcmeChallengeResult> challengeResults;

            // ACME Challenge を実行
            if (useDns01Auth)
            {
                challengeResults = await activity.Dns01Authorization(orderDetails.Payload.Authorizations);

                // Azure DNS で正しくレコードが引けるか確認
                await activity.CheckDnsChallenge(challengeResults);
            }
            else
            {
                challengeResults = await activity.Http01Authorization((site, orderDetails.Payload.Authorizations));

                // HTTP で正しくアクセスできるか確認
                await activity.CheckHttpChallenge(challengeResults);
            }

            // ACME Answer を実行
            await activity.AnswerChallenges(challengeResults);

            // Order のステータスが ready になるまで 60 秒待機
            await activity.CheckIsReady(orderDetails);

            // Order の最終処理を実行し PFX を作成
            var (thumbprint, pfxBlob) = await activity.FinalizeOrder((dnsNames, orderDetails));

            return await activity.UploadCertificate((site, $"{dnsNames[0]}-{thumbprint}", pfxBlob, forceDns01Challenge));
        }

        [FunctionName(nameof(GetResourceGroups))]
        public Task<IList<ResourceGroup>> GetResourceGroups([ActivityTrigger] object input = null)
        {
            return _resourceManagementClient.ResourceGroups.ListAllAsync();
        }

        [FunctionName(nameof(GetSite))]
        public Task<Site> GetSite([ActivityTrigger] (string, string, string) input)
        {
            var (resourceGroupName, appName, slotName) = input;

            if (slotName != "production")
            {
                return _webSiteManagementClient.WebApps.GetSlotAsync(resourceGroupName, appName, slotName);
            }

            return _webSiteManagementClient.WebApps.GetAsync(resourceGroupName, appName);
        }

        [FunctionName(nameof(GetSites))]
        public async Task<IList<Site>> GetSites([ActivityTrigger] (string, bool) input)
        {
            var (resourceGroupName, isRunningOnly) = input;

            var sites = await _webSiteManagementClient.WebApps.ListByResourceGroupAllAsync(resourceGroupName);

            return sites.Where(x => !isRunningOnly || x.State == "Running")
                        .Where(x => x.HostNames.Any(xs => !xs.EndsWith(_environment.AppService) && !xs.EndsWith(_environment.TrafficManager)))
                        .ToArray();
        }

        [FunctionName(nameof(GetExpiringCertificates))]
        public async Task<IList<Certificate>> GetExpiringCertificates([ActivityTrigger] DateTime currentDateTime)
        {
            var certificates = await _webSiteManagementClient.Certificates.ListAllAsync();

            return certificates.Where(x => x.TagsFilter(IssuerName, _options.Endpoint))
                               .Where(x => (x.ExpirationDate.Value - currentDateTime).TotalDays < 30)
                               .ToArray();
        }

        [FunctionName(nameof(GetAllCertificates))]
        public async Task<IList<Certificate>> GetAllCertificates([ActivityTrigger] object input)
        {
            var certificates = await _webSiteManagementClient.Certificates.ListAllAsync();

            return certificates.ToArray();
        }

        [FunctionName(nameof(Order))]
        public async Task<OrderDetails> Order([ActivityTrigger] IList<string> dnsNames)
        {
            var acmeProtocolClient = await _acmeProtocolClientFactory.CreateClientAsync();

            return await acmeProtocolClient.CreateOrderAsync(dnsNames);
        }

        [FunctionName(nameof(Http01Precondition))]
        public async Task Http01Precondition([ActivityTrigger] Site site)
        {
            var config = await _webSiteManagementClient.WebApps.GetConfigurationAsync(site);

            // 既に .well-known が仮想アプリケーションとして追加されているか確認
            var virtualApplication = config.VirtualApplications.FirstOrDefault(x => x.VirtualPath == "/.well-known");

            if (virtualApplication == null)
            {
                // .well-known を仮想アプリケーションとして追加
                config.VirtualApplications.Add(new VirtualApplication
                {
                    VirtualPath = "/.well-known",
                    PhysicalPath = "site\\.well-known",
                    PreloadEnabled = false
                });

                await _webSiteManagementClient.WebApps.UpdateConfigurationAsync(site, config);

                // Web App を明示的に再起動する
                await _webSiteManagementClient.WebApps.RestartAsync(site);
            }
        }

        [FunctionName(nameof(Http01Authorization))]
        public async Task<IList<AcmeChallengeResult>> Http01Authorization([ActivityTrigger] (Site, string[]) input)
        {
            var (site, authorizationUrls) = input;

            var acmeProtocolClient = await _acmeProtocolClientFactory.CreateClientAsync();

            var challengeResults = new List<AcmeChallengeResult>();

            foreach (var authorizationUrl in authorizationUrls)
            {
                // Authorization の詳細を取得
                var authorization = await acmeProtocolClient.GetAuthorizationDetailsAsync(authorizationUrl);

                // HTTP-01 Challenge の情報を拾う
                var challenge = authorization.Challenges.FirstOrDefault(x => x.Type == "http-01");

                if (challenge == null)
                {
                    throw new InvalidOperationException("Simultaneous use of HTTP-01 and DNS-01 for authentication is not allowed.");
                }

                var challengeValidationDetails = AuthorizationDecoder.ResolveChallengeForHttp01(authorization, challenge, acmeProtocolClient.Signer);

                // Challenge の情報を保存する
                challengeResults.Add(new AcmeChallengeResult
                {
                    Url = challenge.Url,
                    HttpResourceUrl = challengeValidationDetails.HttpResourceUrl,
                    HttpResourcePath = challengeValidationDetails.HttpResourcePath,
                    HttpResourceValue = challengeValidationDetails.HttpResourceValue
                });
            }

            // 発行プロファイルを取得
            var credentials = await _webSiteManagementClient.WebApps.ListPublishingCredentialsAsync(site);

            var kuduClient = _kuduClientFactory.CreateClient(site.ScmSiteUrl(), credentials.PublishingUserName, credentials.PublishingPassword);

            // Answer 用ファイルを返すための Web.config を作成
            await kuduClient.WriteFileAsync(DefaultWebConfigPath, DefaultWebConfig);

            // Kudu API を使い、Answer 用のファイルを作成
            foreach (var challengeResult in challengeResults)
            {
                await kuduClient.WriteFileAsync(challengeResult.HttpResourcePath, challengeResult.HttpResourceValue);
            }

            return challengeResults;
        }

        [FunctionName(nameof(CheckHttpChallenge))]
        public async Task CheckHttpChallenge([ActivityTrigger] IList<AcmeChallengeResult> challengeResults)
        {
            foreach (var challengeResult in challengeResults)
            {
                // 実際に HTTP でアクセスして確認する
                var insecureHttpClient = _httpClientFactory.CreateClient("InSecure");

                var httpResponse = await insecureHttpClient.GetAsync(challengeResult.HttpResourceUrl);

                // ファイルにアクセスできない場合はエラー
                if (!httpResponse.IsSuccessStatusCode)
                {
                    // リトライする
                    throw new RetriableActivityException($"{challengeResult.HttpResourceUrl} is {httpResponse.StatusCode} status code.");
                }

                var fileContent = await httpResponse.Content.ReadAsStringAsync();

                // ファイルに今回のチャレンジが含まれていない場合もエラー
                if (fileContent != challengeResult.HttpResourceValue)
                {
                    throw new RetriableActivityException($"{challengeResult.HttpResourceUrl} is not correct. Expected: \"{challengeResult.HttpResourceValue}\", Actual: \"{fileContent}\"");
                }
            }
        }

        [FunctionName(nameof(Dns01Precondition))]
        public async Task Dns01Precondition([ActivityTrigger] IList<string> dnsNames)
        {
            // Azure DNS が存在するか確認
            var zones = await _dnsManagementClient.Zones.ListAllAsync();

            foreach (var dnsName in dnsNames)
            {
                if (!zones.Any(x => string.Equals(dnsName, x.Name, StringComparison.OrdinalIgnoreCase) || dnsName.EndsWith($".{x.Name}", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException($"Azure DNS zone \"{dnsName}\" is not found");
                }
            }
        }

        [FunctionName(nameof(Dns01Authorization))]
        public async Task<IList<AcmeChallengeResult>> Dns01Authorization([ActivityTrigger] string[] authorizationUrls)
        {
            var acmeProtocolClient = await _acmeProtocolClientFactory.CreateClientAsync();

            var challengeResults = new List<AcmeChallengeResult>();

            foreach (var authorizationUrl in authorizationUrls)
            {
                // Authorization の詳細を取得
                var authorization = await acmeProtocolClient.GetAuthorizationDetailsAsync(authorizationUrl);

                // DNS-01 Challenge の情報を拾う
                var challenge = authorization.Challenges.FirstOrDefault(x => x.Type == "dns-01");

                if (challenge == null)
                {
                    throw new InvalidOperationException("Simultaneous use of HTTP-01 and DNS-01 for authentication is not allowed.");
                }

                var challengeValidationDetails = AuthorizationDecoder.ResolveChallengeForDns01(authorization, challenge, acmeProtocolClient.Signer);

                // Challenge の情報を保存する
                challengeResults.Add(new AcmeChallengeResult
                {
                    Url = challenge.Url,
                    DnsRecordName = challengeValidationDetails.DnsRecordName,
                    DnsRecordValue = challengeValidationDetails.DnsRecordValue
                });
            }

            // Azure DNS zone の一覧を取得する
            var zones = await _dnsManagementClient.Zones.ListAllAsync();

            // DNS-01 の検証レコード名毎に Azure DNS に TXT レコードを作成
            foreach (var lookup in challengeResults.ToLookup(x => x.DnsRecordName))
            {
                var dnsRecordName = lookup.Key;

                var zone = zones.Where(x => dnsRecordName.EndsWith($".{x.Name}", StringComparison.OrdinalIgnoreCase))
                                .OrderByDescending(x => x.Name.Length)
                                .First();

                var resourceGroup = ExtractResourceGroup(zone.Id);

                // Challenge の詳細から Azure DNS 向けにレコード名を作成
                var acmeDnsRecordName = dnsRecordName.Replace($".{zone.Name}", "", StringComparison.OrdinalIgnoreCase);

                // 既存の TXT レコードがあれば取得する
                var recordSet = await _dnsManagementClient.RecordSets.GetOrDefaultAsync(resourceGroup, zone.Name, acmeDnsRecordName, RecordType.TXT) ?? new RecordSet();

                // TXT レコードに TTL と値をセットする
                recordSet.TTL = 60;
                recordSet.TxtRecords = lookup.Select(x => new TxtRecord(new[] { x.DnsRecordValue })).ToArray();

                await _dnsManagementClient.RecordSets.CreateOrUpdateAsync(resourceGroup, zone.Name, acmeDnsRecordName, RecordType.TXT, recordSet);
            }

            return challengeResults;
        }

        [FunctionName(nameof(CheckDnsChallenge))]
        public async Task CheckDnsChallenge([ActivityTrigger] IList<AcmeChallengeResult> challengeResults)
        {
            foreach (var challengeResult in challengeResults)
            {
                // 実際に ACME の TXT レコードを引いて確認する
                var queryResult = await _lookupClient.QueryAsync(challengeResult.DnsRecordName, QueryType.TXT);

                var txtRecords = queryResult.Answers
                                            .OfType<DnsClient.Protocol.TxtRecord>()
                                            .ToArray();

                // レコードが存在しなかった場合はエラー
                if (txtRecords.Length == 0)
                {
                    throw new RetriableActivityException($"{challengeResult.DnsRecordName} did not resolve.");
                }

                // レコードに今回のチャレンジが含まれていない場合もエラー
                if (!txtRecords.Any(x => x.Text.Contains(challengeResult.DnsRecordValue)))
                {
                    throw new RetriableActivityException($"{challengeResult.DnsRecordName} is not correct. Expected: \"{challengeResult.DnsRecordValue}\", Actual: \"{string.Join(",", txtRecords.SelectMany(x => x.Text))}\"");
                }
            }
        }

        [FunctionName(nameof(CheckIsReady))]
        public async Task CheckIsReady([ActivityTrigger] OrderDetails orderDetails)
        {
            var acmeProtocolClient = await _acmeProtocolClientFactory.CreateClientAsync();

            orderDetails = await acmeProtocolClient.GetOrderDetailsAsync(orderDetails.OrderUrl, orderDetails);

            if (orderDetails.Payload.Status == "pending")
            {
                // pending の場合はリトライする
                throw new RetriableActivityException("ACME domain validation is pending.");
            }

            if (orderDetails.Payload.Status == "invalid")
            {
                // invalid の場合は最初から実行が必要なので失敗させる
                throw new InvalidOperationException("Invalid order status. Required retry at first.");
            }
        }

        [FunctionName(nameof(AnswerChallenges))]
        public async Task AnswerChallenges([ActivityTrigger] IList<AcmeChallengeResult> challengeResults)
        {
            var acmeProtocolClient = await _acmeProtocolClientFactory.CreateClientAsync();

            // Answer の準備が出来たことを通知
            foreach (var challenge in challengeResults)
            {
                await acmeProtocolClient.AnswerChallengeAsync(challenge.Url);
            }
        }

        [FunctionName(nameof(FinalizeOrder))]
        public async Task<(string, byte[])> FinalizeOrder([ActivityTrigger] (IList<string>, OrderDetails) input)
        {
            var (dnsNames, orderDetails) = input;

            // App Service に ECDSA 証明書をアップロードするとエラーになるので一時的に RSA に
            var rsa = RSA.Create(2048);
            var csr = CryptoHelper.Rsa.GenerateCsr(dnsNames, rsa);

            // Order の最終処理を実行し、証明書を作成
            var acmeProtocolClient = await _acmeProtocolClientFactory.CreateClientAsync();

            var finalize = await acmeProtocolClient.FinalizeOrderAsync(orderDetails.Payload.Finalize, csr);

            // 証明書をバイト配列としてダウンロード
            var certificateData = await acmeProtocolClient.GetOrderCertificateAsync(finalize);

            // X509Certificate2Collection を作成
            var x509Certificates = new X509Certificate2Collection();

            x509Certificates.ImportFromPem(certificateData);

            // 秘密鍵を含んだ形で X509Certificate2 を作成
            x509Certificates[0] = x509Certificates[0].CopyWithPrivateKey(rsa);

            // PFX 形式としてエクスポート
            return (x509Certificates[0].Thumbprint, x509Certificates.Export(X509ContentType.Pfx, "P@ssw0rd"));
        }

        [FunctionName(nameof(UploadCertificate))]
        public Task<Certificate> UploadCertificate([ActivityTrigger] (Site, string, byte[], bool) input)
        {
            var (site, certificateName, pfxBlob, forceDns01Challenge) = input;

            return _webSiteManagementClient.Certificates.CreateOrUpdateAsync(site.ResourceGroup, certificateName, new Certificate
            {
                Location = site.Location,
                Password = "P@ssw0rd",
                PfxBlob = pfxBlob,
                ServerFarmId = site.ServerFarmId,
                Tags = new Dictionary<string, string>
                {
                    { "Issuer", IssuerName },
                    { "Endpoint", _options.Endpoint },
                    { "ForceDns01Challenge", forceDns01Challenge.ToString() }
                }
            });
        }

        [FunctionName(nameof(UpdateSiteBinding))]
        public Task UpdateSiteBinding([ActivityTrigger] Site site)
        {
            return _webSiteManagementClient.WebApps.CreateOrUpdateAsync(site);
        }

        [FunctionName(nameof(CleanupVirtualApplication))]
        public async Task CleanupVirtualApplication([ActivityTrigger] Site site)
        {
            var config = await _webSiteManagementClient.WebApps.GetConfigurationAsync(site);

            // 既に .well-known が仮想アプリケーションとして追加されているか確認
            var virtualApplication = config.VirtualApplications.FirstOrDefault(x => x.VirtualPath == "/.well-known" && x.PhysicalPath == "site\\.well-known");

            if (virtualApplication == null)
            {
                return;
            }

            // 作成した仮想アプリケーションを削除
            config.VirtualApplications.Remove(virtualApplication);

            await _webSiteManagementClient.WebApps.UpdateConfigurationAsync(site, config);

            // Web App を明示的に再起動する
            await _webSiteManagementClient.WebApps.RestartAsync(site);
        }

        [FunctionName(nameof(DeleteCertificate))]
        public Task DeleteCertificate([ActivityTrigger] Certificate certificate)
        {
            var resourceGroup = ExtractResourceGroup(certificate.Id);

            return _webSiteManagementClient.Certificates.DeleteAsync(resourceGroup, certificate.Name);
        }

        [FunctionName(nameof(SendCompletedEvent))]
        public Task SendCompletedEvent([ActivityTrigger] (Site, DateTime?, string[]) input)
        {
            var (site, expirationDate, dnsNames) = input;
            var (appName, slotName) = site.SplitName();

            return _webhookClient.SendCompletedEventAsync(appName, slotName ?? "production", expirationDate, dnsNames);
        }

        private static string ExtractResourceGroup(string resourceId)
        {
            var values = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);

            return values[3];
        }

        private const string DefaultWebConfigPath = ".well-known/web.config";
        private const string DefaultWebConfig = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<configuration>\r\n  <system.webServer>\r\n    <handlers>\r\n      <clear />\r\n      <add name=\"StaticFile\" path=\"*\" verb=\"*\" modules=\"StaticFileModule\" resourceType=\"Either\" requireAccess=\"Read\" />\r\n    </handlers>\r\n    <staticContent>\r\n      <remove fileExtension=\".\" />\r\n      <mimeMap fileExtension=\".\" mimeType=\"text/plain\" />\r\n    </staticContent>\r\n    <rewrite>\r\n      <rules>\r\n        <clear />\r\n      </rules>\r\n    </rewrite>\r\n  </system.webServer>\r\n  <system.web>\r\n    <authorization>\r\n      <allow users=\"*\"/>\r\n    </authorization>\r\n  </system.web>\r\n</configuration>";
    }
}
