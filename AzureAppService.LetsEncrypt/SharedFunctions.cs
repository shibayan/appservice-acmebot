using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

using ACMESharp.Authorizations;
using ACMESharp.Crypto;
using ACMESharp.Protocol;
using ACMESharp.Protocol.Resources;

using AzureAppService.LetsEncrypt.Internal;

using DnsClient;

using Microsoft.Azure.Management.Dns;
using Microsoft.Azure.Management.Dns.Models;
using Microsoft.Azure.Management.WebSites;
using Microsoft.Azure.Management.WebSites.Models;
using Microsoft.Azure.Services.AppAuthentication;
using Microsoft.Azure.WebJobs;
using Microsoft.Rest;

using Newtonsoft.Json;

namespace AzureAppService.LetsEncrypt
{
    public class SharedFunctions : ISharedFunctions
    {
        private const string InstanceIdKey = "InstanceId";

        private static readonly HttpClient _httpClient = new HttpClient();
        private static readonly HttpClient _insecureHttpClient = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, __, ___, ____) => true });
        private static readonly HttpClient _acmeHttpClient = new HttpClient { BaseAddress = new Uri("https://acme-v02.api.letsencrypt.org/") };

        private static readonly LookupClient _lookupClient = new LookupClient { UseCache = false };

        [FunctionName(nameof(GetSite))]
        public async Task<Site> GetSite([ActivityTrigger] (string, string, string) input)
        {
            var (resourceGroupName, siteName, slotName) = input;

            var websiteClient = await CreateWebSiteManagementClientAsync();

            if (!string.IsNullOrEmpty(slotName))
            {
                return await websiteClient.WebApps.GetSlotAsync(resourceGroupName, siteName, slotName);
            }

            return await websiteClient.WebApps.GetAsync(resourceGroupName, siteName);
        }

        [FunctionName(nameof(GetSites))]
        public async Task<IList<Site>> GetSites([ActivityTrigger] object input)
        {
            var websiteClient = await CreateWebSiteManagementClientAsync();

            var list = new List<Site>();

            var sites = await websiteClient.WebApps.ListAsync();

            foreach (var site in sites)
            {
                var slots = await websiteClient.WebApps.ListSlotsAsync(site.ResourceGroup, site.Name);

                list.Add(site);
                list.AddRange(slots);
            }

            return list.Where(x => x.HostNameSslStates.Any(xs => !xs.Name.EndsWith(".azurewebsites.net") && !xs.Name.EndsWith(".trafficmanager.net"))).ToArray();
        }

        [FunctionName(nameof(GetCertificates))]
        public async Task<IList<Certificate>> GetCertificates([ActivityTrigger] DateTime currentDateTime)
        {
            var websiteClient = await CreateWebSiteManagementClientAsync();

            var certificates = await websiteClient.Certificates.ListAsync();

            return certificates
                   .Where(x => x.Issuer == "Let's Encrypt Authority X3" || x.Issuer == "Let's Encrypt Authority X4" || x.Issuer == "Fake LE Intermediate X1")
                   .Where(x => (x.ExpirationDate.Value - currentDateTime).TotalDays < 30).ToArray();
        }

        [FunctionName(nameof(GetAllCertificates))]
        public async Task<IList<Certificate>> GetAllCertificates([ActivityTrigger] object input)
        {
            var websiteClient = await CreateWebSiteManagementClientAsync();

            var certificates = await websiteClient.Certificates.ListAsync();

            return certificates.ToArray();
        }

        [FunctionName(nameof(Order))]
        public async Task<OrderDetails> Order([ActivityTrigger] IList<string> hostNames)
        {
            var acme = await CreateAcmeClientAsync();

            return await acme.CreateOrderAsync(hostNames);
        }

        [FunctionName(nameof(Http01Precondition))]
        public async Task Http01Precondition([ActivityTrigger] Site site)
        {
            var websiteClient = await CreateWebSiteManagementClientAsync();

            var config = await websiteClient.WebApps.GetConfigurationAsync(site);

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
            }
            else
            {
                // 追加済みの場合は物理パスを変更しているので対処
                virtualApplication.PhysicalPath = "site\\.well-known";
            }

            await websiteClient.WebApps.UpdateConfigurationAsync(site, config);
        }

        [FunctionName(nameof(Http01Authorization))]
        public async Task<ChallengeResult> Http01Authorization([ActivityTrigger] (Site, string) input)
        {
            var (site, authzUrl) = input;

            var acme = await CreateAcmeClientAsync();

            var authz = await acme.GetAuthorizationDetailsAsync(authzUrl);

            // HTTP-01 Challenge の情報を拾う
            var challenge = authz.Challenges.First(x => x.Type == "http-01");

            var challengeValidationDetails = AuthorizationDecoder.ResolveChallengeForHttp01(authz, challenge, acme.Signer);

            var websiteClient = await CreateWebSiteManagementClientAsync();

            var credentials = await websiteClient.WebApps.ListPublishingCredentialsAsync(site);

            // Kudu API を使い、Answer 用のファイルを作成
            var kuduClient = new KuduApiClient(site.ScmSiteUrl(), credentials.PublishingUserName, credentials.PublishingPassword);

            await kuduClient.WriteFileAsync(DefaultWebConfigPath, DefaultWebConfig);
            await kuduClient.WriteFileAsync(challengeValidationDetails.HttpResourcePath, challengeValidationDetails.HttpResourceValue);

            return new ChallengeResult
            {
                Url = challenge.Url,
                HttpResourceUrl = challengeValidationDetails.HttpResourceUrl,
                HttpResourceValue = challengeValidationDetails.HttpResourceValue
            };
        }

        [FunctionName(nameof(CheckHttpChallenge))]
        public async Task CheckHttpChallenge([ActivityTrigger] ChallengeResult challenge)
        {
            // 実際に HTTP でアクセスして確認する
            var httpResponse = await _insecureHttpClient.GetAsync(challenge.HttpResourceUrl);

            // ファイルにアクセスできない場合はエラー
            if (!httpResponse.IsSuccessStatusCode)
            {
                // リトライする
                throw new RetriableActivityException($"{challenge.HttpResourceUrl} is {httpResponse.StatusCode} status code.");
            }

            var fileContent = await httpResponse.Content.ReadAsStringAsync();

            // ファイルに今回のチャレンジが含まれていない場合もエラー
            if (fileContent != challenge.HttpResourceValue)
            {
                throw new InvalidOperationException($"{challenge.HttpResourceValue} value is not correct.");
            }
        }

        [FunctionName(nameof(Dns01Precondition))]
        public async Task Dns01Precondition([ActivityTrigger] IList<string> hostNames)
        {
            var dnsClient = await CreateDnsManagementClientAsync();

            // Azure DNS が存在するか確認
            var zones = await dnsClient.Zones.ListAsync();

            foreach (var hostName in hostNames)
            {
                if (!zones.Any(x => hostName.EndsWith(x.Name)))
                {
                    throw new InvalidOperationException($"Azure DNS zone \"{hostName}\" is not found");
                }
            }
        }

        [FunctionName(nameof(Dns01Authorization))]
        public async Task<ChallengeResult> Dns01Authorization([ActivityTrigger] (string, string) input)
        {
            var (authzUrl, instanceId) = input;

            var acme = await CreateAcmeClientAsync();

            var authz = await acme.GetAuthorizationDetailsAsync(authzUrl);

            // DNS-01 Challenge の情報を拾う
            var challenge = authz.Challenges.First(x => x.Type == "dns-01");

            var challengeValidationDetails = AuthorizationDecoder.ResolveChallengeForDns01(authz, challenge, acme.Signer);

            // Azure DNS の TXT レコードを書き換え
            var dnsClient = await CreateDnsManagementClientAsync();

            var zone = (await dnsClient.Zones.ListAsync()).First(x => challengeValidationDetails.DnsRecordName.EndsWith(x.Name));

            var resourceId = ParseResourceId(zone.Id);

            // Challenge の詳細から Azure DNS 向けにレコード名を作成
            var acmeDnsRecordName = challengeValidationDetails.DnsRecordName.Replace("." + zone.Name, "");

            RecordSet recordSet;

            try
            {
                recordSet = await dnsClient.RecordSets.GetAsync(resourceId["resourceGroups"], zone.Name, acmeDnsRecordName, RecordType.TXT);
            }
            catch
            {
                recordSet = null;
            }

            if (recordSet != null)
            {
                if (recordSet.Metadata == null || !recordSet.Metadata.TryGetValue(InstanceIdKey, out var dnsInstanceId) || dnsInstanceId != instanceId)
                {
                    recordSet.Metadata = new Dictionary<string, string>
                    {
                        { InstanceIdKey, instanceId }
                    };

                    recordSet.TxtRecords.Clear();
                }

                recordSet.TTL = 60;

                // 既存の TXT レコードに値を追加する
                recordSet.TxtRecords.Add(new TxtRecord(new[] { challengeValidationDetails.DnsRecordValue }));
            }
            else
            {
                // 新しく TXT レコードを作成する
                recordSet = new RecordSet
                {
                    TTL = 60,
                    Metadata = new Dictionary<string, string>
                    {
                        { InstanceIdKey, instanceId }
                    },
                    TxtRecords = new[]
                    {
                        new TxtRecord(new[] { challengeValidationDetails.DnsRecordValue })
                    }
                };
            }

            await dnsClient.RecordSets.CreateOrUpdateAsync(resourceId["resourceGroups"], zone.Name, acmeDnsRecordName, RecordType.TXT, recordSet);

            return new ChallengeResult
            {
                Url = challenge.Url,
                DnsRecordName = challengeValidationDetails.DnsRecordName,
                DnsRecordValue = challengeValidationDetails.DnsRecordValue
            };
        }

        [FunctionName(nameof(CheckDnsChallenge))]
        public async Task CheckDnsChallenge([ActivityTrigger] ChallengeResult challenge)
        {
            // 実際に ACME の TXT レコードを引いて確認する
            var queryResult = await _lookupClient.QueryAsync(challenge.DnsRecordName, QueryType.TXT);

            var txtRecords = queryResult.Answers
                                        .OfType<DnsClient.Protocol.TxtRecord>()
                                        .ToArray();

            // レコードが存在しなかった場合はエラー
            if (txtRecords.Length == 0)
            {
                throw new RetriableActivityException($"{challenge.DnsRecordName} did not resolve.");
            }

            // レコードに今回のチャレンジが含まれていない場合もエラー
            if (!txtRecords.Any(x => x.Text.Contains(challenge.DnsRecordValue)))
            {
                throw new RetriableActivityException($"{challenge.DnsRecordName} value is not correct.");
            }
        }

        [FunctionName(nameof(CheckIsReady))]
        public async Task CheckIsReady([ActivityTrigger] OrderDetails orderDetails)
        {
            var acme = await CreateAcmeClientAsync();

            orderDetails = await acme.GetOrderDetailsAsync(orderDetails.OrderUrl, orderDetails);

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
        public async Task AnswerChallenges([ActivityTrigger] IList<ChallengeResult> challenges)
        {
            var acme = await CreateAcmeClientAsync();

            // Answer の準備が出来たことを通知
            foreach (var challenge in challenges)
            {
                await acme.AnswerChallengeAsync(challenge.Url);
            }
        }

        [FunctionName(nameof(FinalizeOrder))]
        public async Task<(string, byte[])> FinalizeOrder([ActivityTrigger] (IList<string>, OrderDetails) input)
        {
            var (hostNames, orderDetails) = input;

            // ECC 256bit の証明書に固定
            var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var csr = CryptoHelper.Ec.GenerateCsr(hostNames, ec);

            var acme = await CreateAcmeClientAsync();

            // Order の最終処理を実行し、証明書を作成
            var finalize = await acme.FinalizeOrderAsync(orderDetails.Payload.Finalize, csr);

            var certificateData = await _httpClient.GetByteArrayAsync(finalize.Payload.Certificate);

            // 秘密鍵を含んだ形で X509Certificate2 を作成
            var (certificate, chainCertificate) = X509Certificate2Extension.LoadFromPem(certificateData);

            var certificateWithPrivateKey = certificate.CopyWithPrivateKey(ec);

            var x509Certificates = new X509Certificate2Collection(new[] { certificateWithPrivateKey, chainCertificate });

            // PFX 形式としてエクスポート
            return (certificateWithPrivateKey.Thumbprint, x509Certificates.Export(X509ContentType.Pfx, "P@ssw0rd"));
        }

        [FunctionName(nameof(UpdateCertificate))]
        public async Task UpdateCertificate([ActivityTrigger] (Site, string, byte[]) input)
        {
            var websiteClient = await CreateWebSiteManagementClientAsync();

            var (site, certificateName, pfxBlob) = input;

            await websiteClient.Certificates.CreateOrUpdateAsync(site.ResourceGroup, certificateName, new Certificate
            {
                Location = site.Location,
                Password = "P@ssw0rd",
                PfxBlob = pfxBlob,
                ServerFarmId = site.ServerFarmId
            });
        }

        [FunctionName(nameof(UpdateSiteBinding))]
        public async Task UpdateSiteBinding([ActivityTrigger] Site site)
        {
            var websiteClient = await CreateWebSiteManagementClientAsync();

            await websiteClient.WebApps.CreateOrUpdateAsync(site);
        }

        [FunctionName(nameof(DeleteCertificate))]
        public async Task DeleteCertificate([ActivityTrigger] Certificate certificate)
        {
            var websiteClient = await CreateWebSiteManagementClientAsync();

            var resourceId = ParseResourceId(certificate.Id);

            await websiteClient.Certificates.DeleteAsync(resourceId["resourceGroups"], certificate.Name);
        }

        private static async Task<AcmeProtocolClient> CreateAcmeClientAsync()
        {
            var account = default(AccountDetails);
            var accountKey = default(AccountKey);
            var acmeDir = default(ServiceDirectory);

            LoadState(ref account, "account.json");
            LoadState(ref accountKey, "account_key.json");
            LoadState(ref acmeDir, "directory.json");

            var acme = new AcmeProtocolClient(_acmeHttpClient, acmeDir, account, accountKey?.GenerateSigner());

            if (acmeDir == null)
            {
                acmeDir = await acme.GetDirectoryAsync();

                SaveState(acmeDir, "directory.json");

                acme.Directory = acmeDir;
            }

            await acme.GetNonceAsync();

            if (account == null || accountKey == null)
            {
                account = await acme.CreateAccountAsync(new[] { "mailto:" + Settings.Default.Contacts }, true);

                accountKey = new AccountKey
                {
                    KeyType = acme.Signer.JwsAlg,
                    KeyExport = acme.Signer.Export()
                };

                SaveState(account, "account.json");
                SaveState(accountKey, "account_key.json");

                acme.Account = account;
            }

            return acme;
        }

        private static void LoadState<T>(ref T value, string path)
        {
            var fullPath = Environment.ExpandEnvironmentVariables(@"%HOME%\.acme\" + path);

            if (!File.Exists(fullPath))
            {
                return;
            }

            var json = File.ReadAllText(fullPath);

            value = JsonConvert.DeserializeObject<T>(json);
        }

        private static void SaveState<T>(T value, string path)
        {
            var fullPath = Environment.ExpandEnvironmentVariables(@"%HOME%\.acme\" + path);
            var directoryPath = Path.GetDirectoryName(fullPath);

            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            var json = JsonConvert.SerializeObject(value, Formatting.Indented);

            File.WriteAllText(fullPath, json);
        }

        private static async Task<WebSiteManagementClient> CreateWebSiteManagementClientAsync()
        {
            var tokenProvider = new AzureServiceTokenProvider();

            var accessToken = await tokenProvider.GetAccessTokenAsync("https://management.azure.com/");

            var websiteClient = new WebSiteManagementClient(new TokenCredentials(accessToken))
            {
                SubscriptionId = Settings.Default.SubscriptionId
            };

            return websiteClient;
        }

        private static async Task<DnsManagementClient> CreateDnsManagementClientAsync()
        {
            var tokenProvider = new AzureServiceTokenProvider();

            var accessToken = await tokenProvider.GetAccessTokenAsync("https://management.azure.com/");

            var dnsClient = new DnsManagementClient(new TokenCredentials(accessToken))
            {
                SubscriptionId = Settings.Default.SubscriptionId
            };

            return dnsClient;
        }

        private static IDictionary<string, string> ParseResourceId(string resourceId)
        {
            var values = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);

            return new Dictionary<string, string>
            {
                { "subscriptions", values[1] },
                { "resourceGroups", values[3] },
                { "providers", values[5] }
            };
        }

        private static readonly string DefaultWebConfigPath = ".well-known/web.config";
        private static readonly string DefaultWebConfig = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<configuration>\r\n  <system.webServer>\r\n    <handlers>\r\n      <clear />\r\n      <add name=\"StaticFile\" path=\"*\" verb=\"*\" modules=\"StaticFileModule\" resourceType=\"Either\" requireAccess=\"Read\" />\r\n    </handlers>\r\n    <staticContent>\r\n      <remove fileExtension=\".\" />\r\n      <mimeMap fileExtension=\".\" mimeType=\"text/plain\" />\r\n    </staticContent>\r\n    <rewrite>\r\n      <rules>\r\n        <clear />\r\n      </rules>\r\n    </rewrite>\r\n  </system.webServer>\r\n  <system.web>\r\n    <authorization>\r\n      <allow users=\"*\"/>\r\n    </authorization>\r\n  </system.web>\r\n</configuration>";
    }

    public class ChallengeResult
    {
        public string Url { get; set; }
        public string HttpResourceUrl { get; set; }
        public string HttpResourceValue { get; set; }
        public string DnsRecordName { get; set; }
        public string DnsRecordValue { get; set; }
    }
}