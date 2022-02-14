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

using AppService.Acmebot.Internal;
using AppService.Acmebot.Models;
using AppService.Acmebot.Options;

using Azure.Security.KeyVault.Certificates;
using Azure.Storage.Blobs;

using DnsClient;

using Microsoft.Azure.Management.Dns;
using Microsoft.Azure.Management.Dns.Models;
using Microsoft.Azure.Management.ResourceManager;
using Microsoft.Azure.Management.ResourceManager.Models;
using Microsoft.Azure.Management.WebSites;
using Microsoft.Azure.Management.WebSites.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Rest;

using Newtonsoft.Json;

namespace AppService.Acmebot.Functions
{
    public class SharedActivity : ISharedActivity
    {
        public SharedActivity(IHttpClientFactory httpClientFactory, AzureEnvironment environment, LookupClient lookupClient,
                              AcmeProtocolClientFactory acmeProtocolClientFactory, KuduClientFactory kuduClientFactory,
                              WebSiteManagementClient webSiteManagementClient, DnsManagementClient dnsManagementClient,
                              ResourceManagementClient resourceManagementClient, WebhookInvoker webhookInvoker, IOptions<AcmebotOptions> options,
                              ILogger<SharedActivity> logger, ITokenProvider tokenProvider, CertificateClient certificateClient,
                              BlobContainerClient blobContainerClient)
        {
            _httpClientFactory = httpClientFactory;
            _environment = environment;
            _lookupClient = lookupClient;
            _acmeProtocolClientFactory = acmeProtocolClientFactory;
            _kuduClientFactory = kuduClientFactory;
            _webSiteManagementClient = webSiteManagementClient;
            _dnsManagementClient = dnsManagementClient;
            _resourceManagementClient = resourceManagementClient;
            _webhookInvoker = webhookInvoker;
            _options = options.Value;
            _logger = logger;
            _credentials = new TokenCredentials(tokenProvider);
            _certificateClient = certificateClient;
            _blobContainerClient = blobContainerClient;
        }

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly AzureEnvironment _environment;
        private readonly LookupClient _lookupClient;
        private readonly AcmeProtocolClientFactory _acmeProtocolClientFactory;
        private readonly KuduClientFactory _kuduClientFactory;
        private readonly WebSiteManagementClient _webSiteManagementClient;
        private readonly DnsManagementClient _dnsManagementClient;
        private readonly ResourceManagementClient _resourceManagementClient;
        private readonly WebhookInvoker _webhookInvoker;
        private readonly AcmebotOptions _options;
        private readonly ILogger<SharedActivity> _logger;
        private readonly TokenCredentials _credentials;
        private readonly CertificateClient _certificateClient;
        BlobContainerClient _blobContainerClient;

        private const string IssuerName = "Acmebot";

        [FunctionName(nameof(GetResourceGroups))]
        public Task<IReadOnlyList<ResourceGroup>> GetResourceGroups([ActivityTrigger] object input = null)
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
                               .Where(x => (x.ExpirationDate.Value - currentDateTime).TotalDays <= 30)
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
        public Task Http01Precondition([ActivityTrigger] Site site)
        {
            // Disabling this entirely, as serving static files from .well-known/acme-challenge is supported directly by the website.
            // Also, the restart triggered by the code below is undesirable.
            return Task.CompletedTask;
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

            // Use blob storage to avoid the Kudu-induced app service restarts
            foreach (var challengeResult in challengeResults)
            {
                var blobClient = _blobContainerClient.GetBlobClient(challengeResult.HttpResourcePath);
                var content = new BinaryData(System.Text.Encoding.UTF8.GetBytes(challengeResult.HttpResourceValue));
                await blobClient.UploadAsync(content);
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
                recordSet.TxtRecords = lookup.Select(x => new TxtRecord(new[] { x.DnsRecordValue })).ToArray();

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
                throw new RetriableActivityException($"ACME domain validation is {orderDetails.Payload.Status}. It will retry automatically.");
            }

            if (orderDetails.Payload.Status == "invalid")
            {
                object lastError = null;

                foreach (var challengeResult in challengeResults)
                {
                    var challenge = await acmeProtocolClient.GetChallengeDetailsAsync(challengeResult.Url);

                    if (challenge.Status != "invalid")
                    {
                        continue;
                    }

                    _logger.LogError($"ACME domain validation error: {challenge.Error}");

                    lastError = challenge.Error;
                }

                // invalid の場合は最初から実行が必要なので失敗させる
                throw new InvalidOperationException($"ACME domain validation is invalid. Required retry at first.\nLastError = {lastError}");
            }
        }

        [FunctionName(nameof(FinalizeOrder))]
        public async Task<(string, byte[])> FinalizeOrder([ActivityTrigger] (IReadOnlyList<string>, OrderDetails) input)
        {
            var (dnsNames, orderDetails) = input;

            // App Service に ECDSA 証明書をアップロードするとエラーになるので一時的に RSA に
            var rsa = RSA.Create(2048);
            var csr = CryptoHelper.Rsa.GenerateCsr(dnsNames, rsa);

            // Order の最終処理を実行し、証明書を作成
            var acmeProtocolClient = await _acmeProtocolClientFactory.CreateClientAsync();

            var finalize = await acmeProtocolClient.FinalizeOrderAsync(orderDetails.Payload.Finalize, csr);

            // 証明書をバイト配列としてダウンロード
            var x509Certificates = await acmeProtocolClient.GetOrderCertificateAsync(finalize, _options.PreferredChain);

            // 秘密鍵を含んだ形で X509Certificate2 を作成
            x509Certificates[0] = x509Certificates[0].CopyWithPrivateKey(rsa);

            // PFX 形式としてエクスポート
            return (x509Certificates[0].Thumbprint, x509Certificates.Export(X509ContentType.Pfx, "P@ssw0rd"));
        }

        [FunctionName(nameof(UploadCertificate))]
        public async Task<Certificate> UploadCertificate([ActivityTrigger] (Site, string, string, byte[], bool) input)
        {
            var (site, dnsName, thumbprint, pfxBlob, forceDns01Challenge) = input;
            var certificateName = $"{dnsName}-{thumbprint}";

            var importCertOpts = new ImportCertificateOptions(dnsName.Replace(".", "-"), pfxBlob);
            importCertOpts.Password = "P@ssw0rd";
            await _certificateClient.ImportCertificateAsync(importCertOpts);

            var builder = new UriBuilder(_environment.ResourceManager);
            builder.Path = $"/subscriptions/{_webSiteManagementClient.SubscriptionId}/resourceGroups/{site.ResourceGroup}/providers/Microsoft.Web/certificates/{certificateName}";
            builder.Query = $"?api-version=2019-08-01";

            var request = new HttpRequestMessage();
            request.Method = new HttpMethod("PUT");
            request.RequestUri = builder.Uri;

            var payload = new
            {
                location = site.Location,
                properties = new
                {
                    pfxBlob = Convert.ToBase64String(pfxBlob),
                    password = importCertOpts.Password
                },
                tags = new Dictionary<string, string>
                    {
                        { "Issuer", IssuerName },
                        { "Endpoint", _options.Endpoint },
                        { "ForceDns01Challenge", forceDns01Challenge.ToString() }
                    }
            };
            var content = JsonConvert.SerializeObject(payload);
            _logger.LogInformation($"PUT {builder.Uri}: Payload length: {content.Length}");
            request.Content = new StringContent(content, System.Text.Encoding.UTF8);
            request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json; charset=utf-8");

            await _credentials.ProcessHttpRequestAsync(request, System.Threading.CancellationToken.None).ConfigureAwait(false);

            var response = await _webSiteManagementClient.HttpClient.SendAsync(request, System.Threading.CancellationToken.None).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var msg = $"Operation returned an invalid status code '{response.StatusCode}'. URL: '{request.RequestUri.OriginalString}'. Payload: '{content}'";
                throw new DefaultErrorResponseException(msg);
            }

            return await _webSiteManagementClient.Certificates.GetAsync(site.ResourceGroup, certificateName);
        }

        [FunctionName(nameof(UpdateHostNameSslState))]
        [ExponentialBackoffRetry(20, "00:00:15", "01:00:00")]
        public async Task UpdateHostNameSslState([ActivityTrigger] (Site, HostNameSslState) input)
        {
            // URL and payload gauged from the "Try" feature on https://docs.microsoft.com/en-us/rest/api/appservice/web-apps/create-or-update-host-name-binding
            var (site, newState) = input;
            var client = _webSiteManagementClient;
            var builder = new UriBuilder(_environment.ResourceManager);
            builder.Path = $"/subscriptions/{client.SubscriptionId}/resourceGroups/{site.ResourceGroup}/providers/Microsoft.Web/sites/{site.Name}/hostNameBindings/{newState.Name}";
            builder.Query = $"?api-version=2019-08-01";

            var request = new HttpRequestMessage();
            request.Method = new HttpMethod("PUT");
            request.RequestUri = builder.Uri;

            var content =
                JsonConvert.SerializeObject(
                    new
                    {
                        properties =
                        new
                        {
                            sslState = "sniEnabled",
                            thumbprint = newState.Thumbprint
                        }
                    }
                );

            _logger.LogInformation($"PUT {builder.Uri}: {content} ");
            request.Content = new StringContent(content, System.Text.Encoding.UTF8);
            request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json; charset=utf-8");

            await _credentials.ProcessHttpRequestAsync(request, System.Threading.CancellationToken.None).ConfigureAwait(false);

            var response = await client.HttpClient.SendAsync(request, System.Threading.CancellationToken.None).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {

                var msg = $"Operation returned an invalid status code '{response.StatusCode}'. URL: '{request.RequestUri.OriginalString}'. Payload: '{content}'";
                throw new DefaultErrorResponseException(msg);
            }
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
        public Task CleanupVirtualApplication([ActivityTrigger] Site site)
        {
            // Disabling this entirely, as serving static files from .well-known/acme-challenge is supported directly by the website.
            // Also, the restart triggered by the code below is undesirable.
            return Task.CompletedTask;
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
