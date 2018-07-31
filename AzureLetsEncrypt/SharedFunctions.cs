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

using Microsoft.Azure.Management.WebSites;
using Microsoft.Azure.Management.WebSites.Models;
using Microsoft.Azure.Services.AppAuthentication;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Microsoft.Rest;

using Newtonsoft.Json;

namespace AzureLetsEncrypt
{
    public static class SharedFunctions
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private static readonly HttpClient _acmeHttpClient = new HttpClient { BaseAddress = new Uri("https://acme-v02.api.letsencrypt.org/") };

        [FunctionName(nameof(GetSite))]
        public static async Task<Site> GetSite([ActivityTrigger] DurableActivityContext context, ILogger log)
        {
            var websiteClient = await CreateManagementClientAsync();

            var (resourceGroupName, siteName) = context.GetInput<(string, string)>();

            return await websiteClient.WebApps.GetAsync(resourceGroupName, siteName);
        }

        [FunctionName(nameof(GetSites))]
        public static async Task<IList<Site>> GetSites([ActivityTrigger] DurableActivityContext context, ILogger log)
        {
            var websiteClient = await CreateManagementClientAsync();

            var sites = await websiteClient.WebApps.ListAsync();

            return sites.Where(x => x.HostNameSslStates.Any(xs => !xs.Name.EndsWith(".azurewebsites.net"))).ToArray();
        }

        [FunctionName(nameof(GetCertificates))]
        public static async Task<IList<Certificate>> GetCertificates([ActivityTrigger] DurableActivityContext context, ILogger log)
        {
            var currentDateTime = context.GetInput<DateTime>();

            var websiteClient = await CreateManagementClientAsync();

            var certificates = await websiteClient.Certificates.ListByResourceGroupAsync("Default-Web-JapanEast");

            return certificates.Where(x => (x.ExpirationDate.Value - currentDateTime).TotalDays < 30).ToArray();
        }

        [FunctionName(nameof(UpdateSettings))]
        public static async Task UpdateSettings([ActivityTrigger] DurableActivityContext context, ILogger log)
        {
            var site = context.GetInput<Site>();

            var websiteClient = await CreateManagementClientAsync();

            var config = await websiteClient.WebApps.GetConfigurationAsync(site.ResourceGroup, site.Name);

            if (config.VirtualApplications.Any(x => x.VirtualPath == "/.well-known"))
            {
                return;
            }

            config.VirtualApplications.Add(new VirtualApplication
            {
                VirtualPath = "/.well-known",
                PhysicalPath = "site\\wwwroot\\.well-known",
                PreloadEnabled = false
            });

            await websiteClient.WebApps.UpdateConfigurationAsync(site.ResourceGroup, site.Name, config);
        }

        [FunctionName(nameof(Order))]
        public static async Task<OrderDetails> Order([ActivityTrigger] DurableActivityContext context, ILogger log)
        {
            var hostName = context.GetInput<string>();

            var acme = await CreateAcmeClientAsync();

            return await acme.CreateOrderAsync(new[] { hostName });
        }

        [FunctionName(nameof(Authorization))]
        public static async Task Authorization([ActivityTrigger] DurableActivityContext context, ILogger log)
        {
            var (site, authzUrl) = context.GetInput<(Site, string)>();

            var acme = await CreateAcmeClientAsync();

            var authz = await acme.GetAuthorizationDetailsAsync(authzUrl);

            var challenge = authz.Challenges.First(x => x.Type == "http-01");

            var challengeValidationDetails = AuthorizationDecoder.ResolveChallengeForHttp01(authz, challenge, acme.Signer);

            var websiteClient = await CreateManagementClientAsync();

            var credentials = await websiteClient.WebApps.ListPublishingCredentialsAsync(site.ResourceGroup, site.Name);

            var kuduClient = new KuduApiClient(site.Name, credentials.PublishingUserName, credentials.PublishingPassword);

            await kuduClient.WriteFileAsync(DefaultWebConfigPath, DefaultWebConfig);
            await kuduClient.WriteFileAsync(challengeValidationDetails.HttpResourcePath, challengeValidationDetails.HttpResourceValue);

            await acme.AnswerChallengeAsync(challenge.Url);
        }

        [FunctionName(nameof(WaitChallenge))]
        public static async Task<bool> WaitChallenge([ActivityTrigger] DurableActivityContext context, ILogger log)
        {
            var orderDetails = context.GetInput<OrderDetails>();

            var acme = await CreateAcmeClientAsync();

            for (int i = 0; i < 6; i++)
            {
                orderDetails = await acme.GetOrderDetailsAsync(orderDetails.OrderUrl, orderDetails);

                if (orderDetails.Payload.Status == "ready")
                {
                    return true;
                }

                await Task.Delay(TimeSpan.FromSeconds(5));
            }

            return false;
        }

        [FunctionName(nameof(FinalizeOrder))]
        public static async Task<(string, byte[])> FinalizeOrder([ActivityTrigger] DurableActivityContext context, ILogger log)
        {
            var (hostNameSslState, orderDetails) = context.GetInput<(HostNameSslState, OrderDetails)>();

            var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var csr = CryptoHelper.Ec.GenerateCsr(new[] { hostNameSslState.Name }, ec);

            var acme = await CreateAcmeClientAsync();

            var finalize = await acme.FinalizeOrderAsync(orderDetails.Payload.Finalize, csr);

            var certificateData = await _httpClient.GetByteArrayAsync(finalize.Payload.Certificate);

            var certificate = new X509Certificate2(certificateData).CopyWithPrivateKey(ec);

            return (certificate.Thumbprint, certificate.Export(X509ContentType.Pfx, "P@ssw0rd"));
        }

        [FunctionName(nameof(UpdateCertificate))]
        public static async Task UpdateCertificate([ActivityTrigger] DurableActivityContext context, ILogger log)
        {
            var websiteClient = await CreateManagementClientAsync();

            var (site, thumbprint, pfxBlob) = context.GetInput<(Site, string, byte[])>();

            await websiteClient.Certificates.CreateOrUpdateAsync(site.ResourceGroup, $"{site.Name}-{thumbprint}", new Certificate
            {
                Location = site.Location,
                Password = "P@ssw0rd",
                PfxBlob = pfxBlob
            });
        }

        [FunctionName(nameof(UpdateSiteBinding))]
        public static async Task UpdateSiteBinding([ActivityTrigger] DurableActivityContext context, ILogger log)
        {
            var websiteClient = await CreateManagementClientAsync();

            var site = context.GetInput<Site>();

            await websiteClient.WebApps.CreateOrUpdateAsync(site.ResourceGroup, site.Name, site);
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

        private static async Task<WebSiteManagementClient> CreateManagementClientAsync()
        {
            var tokenProvider = new AzureServiceTokenProvider();

            var accessToken = await tokenProvider.GetAccessTokenAsync("https://management.azure.com/");

            var websiteClient = new WebSiteManagementClient(new TokenCredentials(accessToken))
            {
                SubscriptionId = Settings.Default.SubscriptionId
            };

            return websiteClient;
        }

        private static readonly string DefaultWebConfigPath = ".well-known/web.config";
        private static readonly string DefaultWebConfig = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<configuration>\r\n  <system.webServer>\r\n    <handlers>\r\n      <clear />\r\n      <add name=\"ACMEStaticFile\" path=\"*\" verb=\"*\" modules=\"StaticFileModule\" resourceType=\"Either\" requireAccess=\"Read\" />\r\n    </handlers>\r\n    <staticContent>\r\n      <remove fileExtension=\".\" />\r\n      <mimeMap fileExtension=\".\" mimeType=\"text/plain\" />\r\n    </staticContent>\r\n  </system.webServer>\r\n  <system.web>\r\n    <authorization>\r\n      <allow users=\"*\"/>\r\n    </authorization>\r\n  </system.web>\r\n</configuration>";
    }
}