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
using ACMESharp.Protocol.Resources;

using AppService.Acmebot.Internal;
using AppService.Acmebot.Models;
using AppService.Acmebot.Options;

using Azure.ResourceManager;
using Azure.ResourceManager.Dns;
using Azure.ResourceManager.Dns.Models;

using DnsClient;

using Microsoft.Azure.Management.WebSites;
using Microsoft.Azure.Management.WebSites.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Newtonsoft.Json;

namespace AppService.Acmebot.Functions
{
    public class SharedActivity : ISharedActivity
    {
        public SharedActivity(IHttpClientFactory httpClientFactory, AzureEnvironment environment, LookupClient lookupClient,
                              AcmeProtocolClientFactory acmeProtocolClientFactory, KuduClientFactory kuduClientFactory,
                              ArmClient armClient, WebSiteManagementClient webSiteManagementClient, DnsManagementClient dnsManagementClient,
                              WebhookInvoker webhookInvoker, IOptions<AcmebotOptions> options, ILogger<SharedActivity> logger)
        {
            _httpClientFactory = httpClientFactory;
            _environment = environment;
            _lookupClient = lookupClient;
            _acmeProtocolClientFactory = acmeProtocolClientFactory;
            _kuduClientFactory = kuduClientFactory;
            _armClient = armClient;
            _webSiteManagementClient = webSiteManagementClient;
            _dnsManagementClient = dnsManagementClient;
            _webhookInvoker = webhookInvoker;
            _options = options.Value;
            _logger = logger;
        }

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly AzureEnvironment _environment;
        private readonly LookupClient _lookupClient;
        private readonly AcmeProtocolClientFactory _acmeProtocolClientFactory;
        private readonly KuduClientFactory _kuduClientFactory;
        private readonly ArmClient _armClient;
        private readonly WebSiteManagementClient _webSiteManagementClient;
        private readonly DnsManagementClient _dnsManagementClient;
        private readonly WebhookInvoker _webhookInvoker;
        private readonly AcmebotOptions _options;
        private readonly ILogger<SharedActivity> _logger;

        private const string IssuerName = "Acmebot";

        [FunctionName(nameof(GetResourceGroups))]
        public async Task<IReadOnlyList<string>> GetResourceGroups([ActivityTrigger] object input = null)
        {
            var subscription = await _armClient.GetDefaultSubscriptionAsync();

            return await subscription.GetResourceGroups().ListAllAsync();
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
        public async Task<IReadOnlyList<Site>> GetSites([ActivityTrigger] (string, bool) input)
        {
            var (resourceGroupName, isRunningOnly) = input;

            var sites = await _webSiteManagementClient.WebApps.ListByResourceGroupAllAsync(resourceGroupName);

            return sites.Where(x => !isRunningOnly || x.State == "Running")
                        .Where(x => x.HostNames.Any(xs => !xs.EndsWith(_environment.AppService) && !xs.EndsWith(_environment.TrafficManager)))
                        .OrderBy(x => x.Name)
                        .ToArray();
        }

        [FunctionName(nameof(GetExpiringCertificates))]
        public async Task<IReadOnlyList<Certificate>> GetExpiringCertificates([ActivityTrigger] DateTime currentDateTime)
        {
            var certificates = await _webSiteManagementClient.Certificates.ListAllAsync();

            return certificates.Where(x => x.TagsFilter(IssuerName, _options.Endpoint))
                               .Where(x => (x.ExpirationDate.Value - currentDateTime).TotalDays <= _options.RenewBeforeExpiry)
                               .ToArray();
        }

        [FunctionName(nameof(GetAllCertificates))]
        public Task<IReadOnlyList<Certificate>> GetAllCertificates([ActivityTrigger] object input)
        {
            return _webSiteManagementClient.Certificates.ListAllAsync();
        }

        [FunctionName(nameof(Order))]
        public async Task<OrderDetails> Order([ActivityTrigger] IReadOnlyList<string> dnsNames)
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

            if (virtualApplication != null)
            {
                return;
            }

            // 発行プロファイルを取得
            var credentials = await _webSiteManagementClient.WebApps.ListPublishingCredentialsAsync(site);

            var kuduClient = _kuduClientFactory.CreateClient(site.ScmSiteUrl(), credentials.PublishingUserName, credentials.PublishingPassword);

            try
            {
                // 特殊なファイルが存在する場合は web.config の作成を行わない
                if (!await kuduClient.ExistsFileAsync(".well-known/configured"))
                {
                    // Answer 用ファイルを返すための Web.config を作成
                    await kuduClient.WriteFileAsync(DefaultWebConfigPath, DefaultWebConfig);
                }
            }
            catch (HttpRequestException ex)
            {
                throw new PreconditionException($"Failed to access SCM site. Message: {ex.Message}");
            }

            // .well-known を仮想アプリケーションとして追加
            config.VirtualApplications.Add(new VirtualApplication
            {
                VirtualPath = "/.well-known",
                PhysicalPath = "site\\.well-known",
                PreloadEnabled = false
            });

            await _webSiteManagementClient.WebApps.UpdateConfigurationAsync(site, config);
        }

        [FunctionName(nameof(Http01Authorization))]
        public async Task<IReadOnlyList<AcmeChallengeResult>> Http01Authorization([ActivityTrigger] (Site, IReadOnlyList<string>) input)
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

            // Kudu API を使い、Answer 用のファイルを作成
            foreach (var challengeResult in challengeResults)
            {
                await kuduClient.WriteFileAsync(challengeResult.HttpResourcePath, challengeResult.HttpResourceValue);
            }

            return challengeResults;
        }

        [FunctionName(nameof(CheckHttpChallenge))]
        public async Task CheckHttpChallenge([ActivityTrigger] IReadOnlyList<AcmeChallengeResult> challengeResults)
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
        public async Task Dns01Precondition([ActivityTrigger] IReadOnlyList<string> dnsNames)
        {
            // Azure DNS が存在するか確認
            var zones = await _dnsManagementClient.Zones.ListAllAsync();

            var foundZones = new HashSet<Zone>();
            var zoneNotFoundDnsNames = new List<string>();

            foreach (var dnsName in dnsNames)
            {
                var zone = zones.Where(x => string.Equals(dnsName, x.Name, StringComparison.OrdinalIgnoreCase) || dnsName.EndsWith($".{x.Name}", StringComparison.OrdinalIgnoreCase))
                                .OrderByDescending(x => x.Name.Length)
                                .FirstOrDefault();

                // マッチする DNS zone が見つからない場合はエラー
                if (zone == null)
                {
                    zoneNotFoundDnsNames.Add(dnsName);
                    continue;
                }

                foundZones.Add(zone);
            }

            if (zoneNotFoundDnsNames.Count > 0)
            {
                throw new PreconditionException($"DNS zone(s) are not found. DnsNames = {string.Join(",", zoneNotFoundDnsNames)}");
            }

            // DNS zone に移譲されている Name servers が正しいか検証
            foreach (var zone in foundZones)
            {
                // DNS provider が Name servers を返していなければスキップ
                if (zone.NameServers == null || zone.NameServers.Count == 0)
                {
                    continue;
                }

                // DNS provider が Name servers を返している場合は NS レコードを確認
                var queryResult = await _lookupClient.QueryAsync(zone.Name, QueryType.NS);

                // 最後の . が付いている場合があるので削除して統一
                var expectedNameServers = zone.NameServers
                                              .Select(x => x.TrimEnd('.'))
                                              .ToArray();

                var actualNameServers = queryResult.Answers
                                                   .OfType<DnsClient.Protocol.NsRecord>()
                                                   .Select(x => x.NSDName.Value.TrimEnd('.'))
                                                   .ToArray();

                // 処理対象の DNS zone から取得した NS と実際に引いた NS の値が一つも一致しない場合はエラー
                if (!actualNameServers.Intersect(expectedNameServers, StringComparer.OrdinalIgnoreCase).Any())
                {
                    throw new PreconditionException($"The delegated name server is not correct. DNS zone = {zone.Name}, Expected = {string.Join(",", expectedNameServers)}, Actual = {string.Join(",", actualNameServers)}");
                }
            }
        }

        [FunctionName(nameof(Dns01Authorization))]
        public async Task<IReadOnlyList<AcmeChallengeResult>> Dns01Authorization([ActivityTrigger] IReadOnlyList<string> authorizationUrls)
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

                foreach (var value in lookup)
                {
                    recordSet.TxtRecords.Add(new TxtRecord { Value = { value.DnsRecordValue } });
                }

                await _dnsManagementClient.RecordSets.CreateOrUpdateAsync(resourceGroup, zone.Name, acmeDnsRecordName, RecordType.TXT, recordSet);
            }

            return challengeResults;
        }

        [FunctionName(nameof(CheckDnsChallenge))]
        public async Task CheckDnsChallenge([ActivityTrigger] IReadOnlyList<AcmeChallengeResult> challengeResults)
        {
            foreach (var challengeResult in challengeResults)
            {
                IDnsQueryResponse queryResult;

                try
                {
                    // 実際に ACME の TXT レコードを引いて確認する
                    queryResult = await _lookupClient.QueryAsync(challengeResult.DnsRecordName, QueryType.TXT);
                }
                catch (DnsResponseException ex)
                {
                    // 一時的な DNS エラーの可能性があるためリトライ
                    throw new RetriableActivityException($"{challengeResult.DnsRecordName} bad response. Message: \"{ex.DnsError}\"", ex);
                }

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

        [FunctionName(nameof(AnswerChallenges))]
        public async Task AnswerChallenges([ActivityTrigger] IReadOnlyList<AcmeChallengeResult> challengeResults)
        {
            var acmeProtocolClient = await _acmeProtocolClientFactory.CreateClientAsync();

            // Answer の準備が出来たことを通知
            foreach (var challenge in challengeResults)
            {
                await acmeProtocolClient.AnswerChallengeAsync(challenge.Url);
            }
        }

        [FunctionName(nameof(CheckIsReady))]
        public async Task CheckIsReady([ActivityTrigger] (OrderDetails, IReadOnlyList<AcmeChallengeResult>) input)
        {
            var (orderDetails, challengeResults) = input;

            var acmeProtocolClient = await _acmeProtocolClientFactory.CreateClientAsync();

            orderDetails = await acmeProtocolClient.GetOrderDetailsAsync(orderDetails.OrderUrl, orderDetails);

            if (orderDetails.Payload.Status == "pending" || orderDetails.Payload.Status == "processing")
            {
                // pending か processing の場合はリトライする
                throw new RetriableActivityException($"ACME validation status is {orderDetails.Payload.Status}. It will retry automatically.");
            }

            if (orderDetails.Payload.Status == "invalid")
            {
                var problems = new List<Problem>();

                foreach (var challengeResult in challengeResults)
                {
                    var challenge = await acmeProtocolClient.GetChallengeDetailsAsync(challengeResult.Url);

                    if (challenge.Status != "invalid" || challenge.Error == null)
                    {
                        continue;
                    }

                    _logger.LogError($"ACME domain validation error: {JsonConvert.SerializeObject(challenge.Error)}");

                    problems.Add(challenge.Error);
                }

                // 全てのエラーが connection か dns 関係の場合は Orchestrator からリトライさせる
                if (problems.All(x => x.Type == "urn:ietf:params:acme:error:connection" || x.Type == "urn:ietf:params:acme:error:dns"))
                {
                    throw new RetriableOrchestratorException("ACME validation status is invalid, but retriable error. It will retry automatically.");
                }

                // invalid の場合は最初から実行が必要なので失敗させる
                throw new InvalidOperationException($"ACME validation status is invalid. Required retry at first.\nLastError = {JsonConvert.SerializeObject(problems.Last())}");
            }
        }

        [FunctionName(nameof(FinalizeOrder))]
        public async Task<(OrderDetails, RSAParameters)> FinalizeOrder([ActivityTrigger] (IReadOnlyList<string>, OrderDetails) input)
        {
            var (dnsNames, orderDetails) = input;

            // App Service に ECDSA 証明書をアップロードするとエラーになるので一時的に RSA に
            var rsa = RSA.Create(2048);
            var csr = CryptoHelper.Rsa.GenerateCsr(dnsNames, rsa);

            // Order の最終処理を実行し、証明書を作成
            var acmeProtocolClient = await _acmeProtocolClientFactory.CreateClientAsync();

            orderDetails = await acmeProtocolClient.FinalizeOrderAsync(orderDetails.Payload.Finalize, csr);

            return (orderDetails, rsa.ExportParameters(true));
        }

        [FunctionName(nameof(CheckIsValid))]
        public async Task<OrderDetails> CheckIsValid([ActivityTrigger] OrderDetails orderDetails)
        {
            var acmeProtocolClient = await _acmeProtocolClientFactory.CreateClientAsync();

            orderDetails = await acmeProtocolClient.GetOrderDetailsAsync(orderDetails.OrderUrl, orderDetails);

            if (orderDetails.Payload.Status == "pending" || orderDetails.Payload.Status == "processing")
            {
                // pending か processing の場合はリトライする
                throw new RetriableActivityException($"Finalize request is {orderDetails.Payload.Status}. It will retry automatically.");
            }

            if (orderDetails.Payload.Status == "invalid")
            {
                // invalid の場合は最初から実行が必要なので失敗させる
                throw new InvalidOperationException("Finalize request is invalid. Required retry at first.");
            }

            return orderDetails;
        }

        [FunctionName(nameof(UploadCertificate))]
        public async Task<Certificate> UploadCertificate([ActivityTrigger] (Site, string, bool, OrderDetails, RSAParameters) input)
        {
            var (site, dnsName, forceDns01Challenge, orderDetails, rsaParameters) = input;

            var acmeProtocolClient = await _acmeProtocolClientFactory.CreateClientAsync();

            // 証明書をバイト配列としてダウンロード
            var x509Certificates = await acmeProtocolClient.GetOrderCertificateAsync(orderDetails, _options.PreferredChain);

            // 秘密鍵を含んだ形で X509Certificate2 を作成
            var rsa = RSA.Create(rsaParameters);

            x509Certificates[0] = x509Certificates[0].CopyWithPrivateKey(rsa);

            // PFX 形式としてエクスポート
            var pfxBlob = x509Certificates.Export(X509ContentType.Pfx, "P@ssw0rd");

            var certificateName = $"{dnsName}-{x509Certificates[0].Thumbprint}";

            return await _webSiteManagementClient.Certificates.CreateOrUpdateAsync(site.ResourceGroup, certificateName, new Certificate
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

        [FunctionName(nameof(CleanupDnsChallenge))]
        public async Task CleanupDnsChallenge([ActivityTrigger] IReadOnlyList<AcmeChallengeResult> challengeResults)
        {
            // Azure DNS zone の一覧を取得する
            var zones = await _dnsManagementClient.Zones.ListAllAsync();

            // DNS-01 の検証レコード名毎に Azure DNS から TXT レコードを削除
            foreach (var lookup in challengeResults.ToLookup(x => x.DnsRecordName))
            {
                var dnsRecordName = lookup.Key;

                var zone = zones.Where(x => dnsRecordName.EndsWith($".{x.Name}", StringComparison.OrdinalIgnoreCase))
                                .OrderByDescending(x => x.Name.Length)
                                .First();

                var resourceGroup = ExtractResourceGroup(zone.Id);

                // Challenge の詳細から Azure DNS 向けにレコード名を作成
                var acmeDnsRecordName = dnsRecordName.Replace($".{zone.Name}", "", StringComparison.OrdinalIgnoreCase);

                await _dnsManagementClient.RecordSets.DeleteAsync(resourceGroup, zone.Name, acmeDnsRecordName, RecordType.TXT);
            }
        }

        [FunctionName(nameof(CleanupVirtualApplication))]
        public async Task CleanupVirtualApplication([ActivityTrigger] Site site)
        {
            var config = await _webSiteManagementClient.WebApps.GetConfigurationAsync(site);

            // 既に .well-known が仮想アプリケーションとして追加されているか確認
            var virtualApplication = config.VirtualApplications.FirstOrDefault(x => x.VirtualPath == "/.well-known");

            if (virtualApplication == null)
            {
                return;
            }

            // 作成した仮想アプリケーションを削除
            config.VirtualApplications.Remove(virtualApplication);

            await _webSiteManagementClient.WebApps.UpdateConfigurationAsync(site, config);
        }

        [FunctionName(nameof(DeleteCertificate))]
        public Task DeleteCertificate([ActivityTrigger] Certificate certificate)
        {
            var resourceGroup = ExtractResourceGroup(certificate.Id);

            return _webSiteManagementClient.Certificates.DeleteAsync(resourceGroup, certificate.Name);
        }

        [FunctionName(nameof(SendCompletedEvent))]
        public Task SendCompletedEvent([ActivityTrigger] (Site, DateTime?, IReadOnlyList<string>) input)
        {
            var (site, expirationDate, dnsNames) = input;
            var (appName, slotName) = site.SplitName();

            return _webhookInvoker.SendCompletedEventAsync(appName, slotName ?? "production", expirationDate, dnsNames);
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
