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

using Microsoft.Azure.Management.Dns;
using Microsoft.Azure.Management.Dns.Models;
using Microsoft.Azure.Management.WebSites;
using Microsoft.Azure.Management.WebSites.Models;
using Microsoft.Azure.Services.AppAuthentication;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Microsoft.Rest;

using Newtonsoft.Json;

namespace AzureAppService.LetsEncrypt
{
    public static class SharedFunctions
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private static readonly HttpClient _acmeHttpClient = new HttpClient { BaseAddress = new Uri("https://acme-v02.api.letsencrypt.org/") };

        [FunctionName(nameof(GetSite))]
        public static async Task<Site> GetSite([ActivityTrigger] DurableActivityContext context, ILogger log)
        {
            var websiteClient = await CreateWebSiteManagementClientAsync();

            var (resourceGroupName, siteName, slotName) = context.GetInput<(string, string, string)>();

            if (!string.IsNullOrEmpty(slotName))
            {
                return await websiteClient.WebApps.GetSlotAsync(resourceGroupName, siteName, slotName);
            }

            return await websiteClient.WebApps.GetAsync(resourceGroupName, siteName);
        }

        [FunctionName(nameof(GetSites))]
        public static async Task<IList<Site>> GetSites([ActivityTrigger] DurableActivityContext context, ILogger log)
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

            return list.Where(x => x.HostNameSslStates.Any(xs => !xs.Name.EndsWith(".azurewebsites.net"))).ToArray();
        }

        [FunctionName(nameof(GetCertificates))]
        public static async Task<IList<Certificate>> GetCertificates([ActivityTrigger] DurableActivityContext context, ILogger log)
        {
            var currentDateTime = context.GetInput<DateTime>();

            var websiteClient = await CreateWebSiteManagementClientAsync();

            // TODO: https://github.com/Azure/azure-rest-api-specs/issues/3526
            //var certificates = await websiteClient.Certificates.ListAsync();
            var certificates = await websiteClient.ListCertificatesAsync();

            return certificates
                   .Where(x => x.Issuer == "Let's Encrypt Authority X3" || x.Issuer == "Let's Encrypt Authority X4" || x.Issuer == "Fake LE Intermediate X1")
                   .Where(x => (x.ExpirationDate.Value - currentDateTime).TotalDays < 30).ToArray();
        }

        [FunctionName(nameof(Order))]
        public static async Task<OrderDetails> Order([ActivityTrigger] DurableActivityContext context, ILogger log)
        {
            var hostNames = context.GetInput<string[]>();

            var acme = await CreateAcmeClientAsync();

            return await acme.CreateOrderAsync(hostNames);
        }

        [FunctionName(nameof(Http01Precondition))]
        public static async Task Http01Precondition([ActivityTrigger] DurableActivityContext context, ILogger log)
        {
            var site = context.GetInput<Site>();

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
        public static async Task<Challenge> Http01Authorization([ActivityTrigger] DurableActivityContext context, ILogger log)
        {
            var (site, authzUrl) = context.GetInput<(Site, string)>();

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

            return challenge;
        }

        [FunctionName(nameof(Dns01Precondition))]
        public static async Task Dns01Precondition([ActivityTrigger] DurableActivityContext context, ILogger log)
        {
            var hostNames = context.GetInput<string[]>();

            var dnsClient = await CreateDnsManagementClientAsync();

            // Azure DNS が存在するか確認
            var zones = await dnsClient.Zones.ListAsync();

            foreach (var hostName in hostNames)
            {
                if (!zones.Any(x => hostName.EndsWith(x.Name)))
                {
                    log.LogError($"Azure DNS zone \"{hostName}\" is not found");

                    throw new InvalidOperationException();
                }
            }
        }

        [FunctionName(nameof(Dns01Authorization))]
        public static async Task<Challenge> Dns01Authorization([ActivityTrigger] DurableActivityContext context, ILogger log)
        {
            var authzUrl = context.GetInput<string>();

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
                if (recordSet.Metadata == null || !recordSet.Metadata.TryGetValue(nameof(context.InstanceId), out var instanceId) || instanceId != context.InstanceId)
                {
                    recordSet.Metadata = new Dictionary<string, string>
                    {
                        { nameof(context.InstanceId), context.InstanceId }
                    };

                    recordSet.TxtRecords.Clear();
                }

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
                        { nameof(context.InstanceId), context.InstanceId }
                    },
                    TxtRecords = new[]
                    {
                        new TxtRecord(new[] { challengeValidationDetails.DnsRecordValue })
                    }
                };
            }

            await dnsClient.RecordSets.CreateOrUpdateAsync(resourceId["resourceGroups"], zone.Name, acmeDnsRecordName, RecordType.TXT, recordSet);

            return challenge;
        }

        [FunctionName(nameof(AnswerChallenges))]
        public static async Task AnswerChallenges([ActivityTrigger] DurableActivityContext context, ILogger log)
        {
            var challenges = context.GetInput<IList<Challenge>>();

            var acme = await CreateAcmeClientAsync();

            // Answer の準備が出来たことを通知
            foreach (var challenge in challenges)
            {
                await acme.AnswerChallengeAsync(challenge.Url);
            }
        }

        [FunctionName(nameof(CheckIsReady))]
        public static async Task CheckIsReady([ActivityTrigger] DurableActivityContext context, ILogger log)
        {
            var orderDetails = context.GetInput<OrderDetails>();

            var acme = await CreateAcmeClientAsync();

            orderDetails = await acme.GetOrderDetailsAsync(orderDetails.OrderUrl, orderDetails);

            if (orderDetails.Payload.Status != "ready")
            {
                throw new InvalidOperationException($"Invalid order status is {orderDetails.Payload.Status}");
            }
        }

        [FunctionName(nameof(FinalizeOrder))]
        public static async Task<(string, byte[])> FinalizeOrder([ActivityTrigger] DurableActivityContext context, ILogger log)
        {
            var (hostNames, orderDetails) = context.GetInput<(string[], OrderDetails)>();

            // ECC 256bit の証明書に固定
            var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var csr = CryptoHelper.Ec.GenerateCsr(hostNames, ec);

            var acme = await CreateAcmeClientAsync();

            // Order の最終処理を実行し、証明書を作成
            var finalize = await acme.FinalizeOrderAsync(orderDetails.Payload.Finalize, csr);

            var certificateData = await _httpClient.GetByteArrayAsync(finalize.Payload.Certificate);

            // 秘密鍵を含んだ形で X509Certificate2 を作成
            var certificate = new X509Certificate2(certificateData).CopyWithPrivateKey(ec);

            // PFX 形式としてエクスポート
            return (certificate.Thumbprint, certificate.Export(X509ContentType.Pfx, "P@ssw0rd"));
        }

        [FunctionName(nameof(UpdateCertificate))]
        public static async Task UpdateCertificate([ActivityTrigger] DurableActivityContext context, ILogger log)
        {
            var websiteClient = await CreateWebSiteManagementClientAsync();

            var (site, certificateName, pfxBlob) = context.GetInput<(Site, string, byte[])>();

            await websiteClient.Certificates.CreateOrUpdateAsync(site.ResourceGroup, certificateName, new Certificate
            {
                Location = site.Location,
                Password = "P@ssw0rd",
                PfxBlob = pfxBlob,
                ServerFarmId = site.ServerFarmId
            });
        }

        [FunctionName(nameof(UpdateSiteBinding))]
        public static async Task UpdateSiteBinding([ActivityTrigger] DurableActivityContext context, ILogger log)
        {
            var websiteClient = await CreateWebSiteManagementClientAsync();

            var site = context.GetInput<Site>();

            await websiteClient.WebApps.CreateOrUpdateAsync(site);
        }

        [FunctionName(nameof(DeleteCertificate))]
        public static async Task DeleteCertificate([ActivityTrigger] DurableActivityContext context, ILogger log)
        {
            var websiteClient = await CreateWebSiteManagementClientAsync();

            var certificate = context.GetInput<Certificate>();

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
        private static readonly string DefaultWebConfig = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<configuration>\r\n  <system.webServer>\r\n    <handlers>\r\n      <clear />\r\n      <add name=\"StaticFile\" path=\"*\" verb=\"*\" modules=\"StaticFileModule\" resourceType=\"Either\" requireAccess=\"Read\" />\r\n    </handlers>\r\n    <staticContent>\r\n      <remove fileExtension=\".\" />\r\n      <mimeMap fileExtension=\".\" mimeType=\"text/plain\" />\r\n    </staticContent>\r\n  </system.webServer>\r\n  <system.web>\r\n    <authorization>\r\n      <allow users=\"*\"/>\r\n    </authorization>\r\n  </system.web>\r\n</configuration>";
    }
}