﻿using System;
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

using DnsClient;

using Microsoft.Azure.Management.Dns;
using Microsoft.Azure.Management.Dns.Models;
using Microsoft.Azure.Management.WebSites;
using Microsoft.Azure.Management.WebSites.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;

namespace AppService.Acmebot
{
    public class SharedFunctions : ISharedFunctions
    {
        public SharedFunctions(IHttpClientFactory httpClientFactory, LookupClient lookupClient,
                               IAcmeProtocolClientFactory acmeProtocolClientFactory, IKuduApiClientFactory kuduApiClientFactory,
                               WebSiteManagementClient webSiteManagementClient, DnsManagementClient dnsManagementClient)
        {
            _httpClientFactory = httpClientFactory;
            _lookupClient = lookupClient;
            _acmeProtocolClientFactory = acmeProtocolClientFactory;
            _kuduApiClientFactory = kuduApiClientFactory;
            _webSiteManagementClient = webSiteManagementClient;
            _dnsManagementClient = dnsManagementClient;
        }

        private const string InstanceIdKey = "InstanceId";

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly LookupClient _lookupClient;
        private readonly IAcmeProtocolClientFactory _acmeProtocolClientFactory;
        private readonly IKuduApiClientFactory _kuduApiClientFactory;
        private readonly WebSiteManagementClient _webSiteManagementClient;
        private readonly DnsManagementClient _dnsManagementClient;

        [FunctionName(nameof(GetSite))]
        public Task<Site> GetSite([ActivityTrigger] (string, string, string) input)
        {
            var (resourceGroupName, siteName, slotName) = input;

            if (!string.IsNullOrEmpty(slotName))
            {
                return _webSiteManagementClient.WebApps.GetSlotAsync(resourceGroupName, siteName, slotName);
            }

            return _webSiteManagementClient.WebApps.GetAsync(resourceGroupName, siteName);
        }

        [FunctionName(nameof(GetSites))]
        public async Task<IList<Site>> GetSites([ActivityTrigger] object input)
        {
            var list = new List<Site>();

            var sites = await _webSiteManagementClient.WebApps.ListAllAsync();

            foreach (var site in sites)
            {
                var slots = await _webSiteManagementClient.WebApps.ListSlotsAsync(site.ResourceGroup, site.Name);

                list.Add(site);
                list.AddRange(slots);
            }

            return list.Where(x => x.HostNameSslStates.Any(xs => !xs.Name.EndsWith(".azurewebsites.net") && !xs.Name.EndsWith(".trafficmanager.net"))).ToArray();
        }

        [FunctionName(nameof(GetCertificates))]
        public async Task<IList<Certificate>> GetCertificates([ActivityTrigger] DateTime currentDateTime)
        {
            var certificates = await _webSiteManagementClient.Certificates.ListAllAsync();

            return certificates
                   .Where(x => x.Issuer == "Let's Encrypt Authority X3" || x.Issuer == "Let's Encrypt Authority X4" || x.Issuer == "Fake LE Intermediate X1")
                   .Where(x => (x.ExpirationDate.Value - currentDateTime).TotalDays < 30).ToArray();
        }

        [FunctionName(nameof(GetAllCertificates))]
        public async Task<IList<Certificate>> GetAllCertificates([ActivityTrigger] object input)
        {
            var certificates = await _webSiteManagementClient.Certificates.ListAllAsync();

            return certificates.ToArray();
        }

        [FunctionName(nameof(Order))]
        public async Task<OrderDetails> Order([ActivityTrigger] IList<string> hostNames)
        {
            var acmeProtocolClient = await _acmeProtocolClientFactory.CreateClientAsync();

            return await acmeProtocolClient.CreateOrderAsync(hostNames);
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
            }
            else
            {
                // 追加済みの場合は物理パスを変更しているので対処
                virtualApplication.PhysicalPath = "site\\.well-known";
            }

            await _webSiteManagementClient.WebApps.UpdateConfigurationAsync(site, config);
        }

        [FunctionName(nameof(Http01Authorization))]
        public async Task<ChallengeResult> Http01Authorization([ActivityTrigger] (Site, string) input)
        {
            var (site, authzUrl) = input;

            var acmeProtocolClient = await _acmeProtocolClientFactory.CreateClientAsync();

            var authz = await acmeProtocolClient.GetAuthorizationDetailsAsync(authzUrl);

            // HTTP-01 Challenge の情報を拾う
            var challenge = authz.Challenges.First(x => x.Type == "http-01");

            var challengeValidationDetails = AuthorizationDecoder.ResolveChallengeForHttp01(authz, challenge, acmeProtocolClient.Signer);

            var credentials = await _webSiteManagementClient.WebApps.ListPublishingCredentialsAsync(site);

            // Kudu API を使い、Answer 用のファイルを作成
            var kuduClient = _kuduApiClientFactory.CreateClient(site.ScmSiteUrl(), credentials.PublishingUserName, credentials.PublishingPassword);

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
            var insecureHttpClient = _httpClientFactory.CreateClient("InSecure");

            var httpResponse = await insecureHttpClient.GetAsync(challenge.HttpResourceUrl);

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
            // Azure DNS が存在するか確認
            var zones = await _dnsManagementClient.Zones.ListAllAsync();

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

            var acmeProtocolClient = await _acmeProtocolClientFactory.CreateClientAsync();

            var authz = await acmeProtocolClient.GetAuthorizationDetailsAsync(authzUrl);

            // DNS-01 Challenge の情報を拾う
            var challenge = authz.Challenges.First(x => x.Type == "dns-01");

            var challengeValidationDetails = AuthorizationDecoder.ResolveChallengeForDns01(authz, challenge, acmeProtocolClient.Signer);

            // Azure DNS の TXT レコードを書き換え
            var zone = (await _dnsManagementClient.Zones.ListAllAsync()).First(x => challengeValidationDetails.DnsRecordName.EndsWith(x.Name));

            var resourceGroup = ExtractResourceGroup(zone.Id);

            // Challenge の詳細から Azure DNS 向けにレコード名を作成
            var acmeDnsRecordName = challengeValidationDetails.DnsRecordName.Replace("." + zone.Name, "");

            RecordSet recordSet;

            try
            {
                recordSet = await _dnsManagementClient.RecordSets.GetAsync(resourceGroup, zone.Name, acmeDnsRecordName, RecordType.TXT);
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

            await _dnsManagementClient.RecordSets.CreateOrUpdateAsync(resourceGroup, zone.Name, acmeDnsRecordName, RecordType.TXT, recordSet);

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
        public async Task AnswerChallenges([ActivityTrigger] IList<ChallengeResult> challenges)
        {
            var acmeProtocolClient = await _acmeProtocolClientFactory.CreateClientAsync();

            // Answer の準備が出来たことを通知
            foreach (var challenge in challenges)
            {
                await acmeProtocolClient.AnswerChallengeAsync(challenge.Url);
            }
        }

        [FunctionName(nameof(FinalizeOrder))]
        public async Task<(string, byte[])> FinalizeOrder([ActivityTrigger] (IList<string>, OrderDetails) input)
        {
            var (hostNames, orderDetails) = input;

            // App Service に ECDSA 証明書をアップロードするとエラーになるので一時的に RSA に
            var rsa = RSA.Create(2048);
            var csr = CryptoHelper.Rsa.GenerateCsr(hostNames, rsa);

            // Order の最終処理を実行し、証明書を作成
            var acmeProtocolClient = await _acmeProtocolClientFactory.CreateClientAsync();

            var finalize = await acmeProtocolClient.FinalizeOrderAsync(orderDetails.Payload.Finalize, csr);

            var httpClient = _httpClientFactory.CreateClient();

            var certificateData = await httpClient.GetByteArrayAsync(finalize.Payload.Certificate);

            // 秘密鍵を含んだ形で X509Certificate2 を作成
            var (certificate, chainCertificate) = X509Certificate2Helper.LoadFromPem(certificateData);

            var certificateWithPrivateKey = certificate.CopyWithPrivateKey(rsa);

            var x509Certificates = new X509Certificate2Collection(new[] { certificateWithPrivateKey, chainCertificate });

            // PFX 形式としてエクスポート
            return (certificateWithPrivateKey.Thumbprint, x509Certificates.Export(X509ContentType.Pfx, "P@ssw0rd"));
        }

        [FunctionName(nameof(UpdateCertificate))]
        public Task UpdateCertificate([ActivityTrigger] (Site, string, byte[]) input)
        {
            var (site, certificateName, pfxBlob) = input;

            return _webSiteManagementClient.Certificates.CreateOrUpdateAsync(site.ResourceGroup, certificateName, new Certificate
            {
                Location = site.Location,
                Password = "P@ssw0rd",
                PfxBlob = pfxBlob,
                ServerFarmId = site.ServerFarmId
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
        }

        [FunctionName(nameof(DeleteCertificate))]
        public Task DeleteCertificate([ActivityTrigger] Certificate certificate)
        {
            var resourceGroup = ExtractResourceGroup(certificate.Id);

            return _webSiteManagementClient.Certificates.DeleteAsync(resourceGroup, certificate.Name);
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