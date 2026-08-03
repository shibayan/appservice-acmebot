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

using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.AppService;
using Azure.ResourceManager.AppService.Models;
using Azure.ResourceManager.Dns;
using Azure.ResourceManager.Dns.Models;
using Azure.ResourceManager.Resources;
using Azure.Security.KeyVault.Certificates;
using Azure.Storage.Blobs;

using DnsClient;

using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Newtonsoft.Json;

namespace AppService.Acmebot.Functions;

public class SharedActivity : ISharedActivity
{
    public SharedActivity(IHttpClientFactory httpClientFactory, AzureEnvironment environment, LookupClient lookupClient,
                          AcmeProtocolClientFactory acmeProtocolClientFactory, KuduClientFactory kuduClientFactory,
                          ArmClient armClient, WebhookInvoker webhookInvoker,
                          IOptions<AcmebotOptions> options, ILogger<SharedActivity> logger,
                          CertificateClient certificateClient, BlobContainerClient blobContainerClient)
    {
        _httpClientFactory = httpClientFactory;
        _environment = environment;
        _lookupClient = lookupClient;
        _acmeProtocolClientFactory = acmeProtocolClientFactory;
        _kuduClientFactory = kuduClientFactory;
        _armClient = armClient;
        _webhookInvoker = webhookInvoker;
        _options = options.Value;
        _logger = logger;
        _certificateClient = certificateClient;
        _blobContainerClient = blobContainerClient;
    }

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AzureEnvironment _environment;
    private readonly LookupClient _lookupClient;
    private readonly AcmeProtocolClientFactory _acmeProtocolClientFactory;
    private readonly KuduClientFactory _kuduClientFactory;
    private readonly ArmClient _armClient;
    private readonly WebhookInvoker _webhookInvoker;
    private readonly AcmebotOptions _options;
    private readonly ILogger<SharedActivity> _logger;
    private readonly CertificateClient _certificateClient;
    private readonly BlobContainerClient _blobContainerClient;

    private const string IssuerName = "Acmebot";

    [FunctionName(nameof(GetResourceGroups))]
    public async Task<IReadOnlyList<ResourceGroupItem>> GetResourceGroups([ActivityTrigger] object input = null)
    {
        var subscription = await _armClient.GetDefaultSubscriptionAsync();

        var resourceGroups = new List<ResourceGroupItem>();

        await foreach (var resourceGroup in subscription.GetResourceGroups().GetAllAsync())
        {
            resourceGroups.Add(ResourceGroupItem.Create(resourceGroup.Data));
        }

        return resourceGroups;
    }

    [FunctionName(nameof(GetWebSite))]
    public async Task<WebSiteItem> GetWebSite([ActivityTrigger] (string, string, string) input)
    {
        var (resourceGroupName, webSiteName, slotName) = input;

        var subscription = await _armClient.GetDefaultSubscriptionAsync();

        if (slotName == "production")
        {
            var id = WebSiteResource.CreateResourceIdentifier(subscription.Data.SubscriptionId, resourceGroupName, webSiteName);

            WebSiteResource webSite = await _armClient.GetWebSiteResource(id).GetAsync();

            return WebSiteItem.Create(webSite.Data, _environment);
        }
        else
        {
            var id = WebSiteSlotResource.CreateResourceIdentifier(subscription.Data.SubscriptionId, resourceGroupName, webSiteName, slotName);

            WebSiteSlotResource webSiteSlot = await _armClient.GetWebSiteSlotResource(id).GetAsync();

            return WebSiteItem.Create(webSiteSlot.Data, _environment);
        }
    }

    [FunctionName(nameof(GetWebSites))]
    public async Task<IReadOnlyList<WebSiteItem>> GetWebSites([ActivityTrigger] string resourceGroupName)
    {
        var subscription = await _armClient.GetDefaultSubscriptionAsync();

        ResourceGroupResource resourceGroup = await subscription.GetResourceGroupAsync(resourceGroupName);

        var webSites = new List<WebSiteItem>();

        await foreach (var webSite in resourceGroup.GetWebSites().GetAllAsync())
        {
            webSites.Add(WebSiteItem.Create(webSite.Data, _environment));
        }

        return webSites;
    }


    [FunctionName(nameof(GetWebSiteSlots))]
    public async Task<IReadOnlyList<WebSiteItem>> GetWebSiteSlots([ActivityTrigger] (string, string) input)
    {
        var (resourceGroupName, webSiteName) = input;

        var subscription = await _armClient.GetDefaultSubscriptionAsync();

        var id = WebSiteResource.CreateResourceIdentifier(subscription.Data.SubscriptionId, resourceGroupName, webSiteName);

        var webSite = _armClient.GetWebSiteResource(id);

        var webSites = new List<WebSiteItem>();

        await foreach (var webSiteSlot in webSite.GetWebSiteSlots().GetAllAsync())
        {
            webSites.Add(WebSiteItem.Create(webSiteSlot.Data, _environment));
        }

        return webSites;
    }

    [FunctionName(nameof(GetExpiringCertificates))]
    public async Task<IReadOnlyList<CertificateItem>> GetExpiringCertificates([ActivityTrigger] DateTime currentDateTime)
    {
        var subscription = await _armClient.GetDefaultSubscriptionAsync();

        var certificates = new List<CertificateItem>();

        await foreach (var certificate in subscription.GetAppCertificatesAsync())
        {
            if (!certificate.Data.IsIssuedByAcmebot())
            {
                continue;
            }

            if (!certificate.Data.IsSameEndpoint(_options.Endpoint))
            {
                // An Acmebot-issued certificate that fails the endpoint match will never be
                // renewed automatically, so make the mismatch visible instead of silently skipping.
                certificate.Data.Tags.TryGetValue("Endpoint", out var tagEndpoint);

                _logger.LogWarning($"Skipping Acmebot-issued certificate {certificate.Data.Name} because its Endpoint tag '{tagEndpoint}' does not match '{_options.Endpoint.Host}'");

                continue;
            }

            if ((certificate.Data.ExpireOn.Value - currentDateTime).TotalDays > _options.RenewBeforeExpiry)
            {
                continue;
            }

            certificates.Add(CertificateItem.Create(certificate.Data));
        }

        return certificates;
    }

    [FunctionName(nameof(GetAllCertificates))]
    public async Task<IReadOnlyList<CertificateItem>> GetAllCertificates([ActivityTrigger] object input)
    {
        var subscription = await _armClient.GetDefaultSubscriptionAsync();

        var certificates = new List<CertificateItem>();

        await foreach (var certificate in subscription.GetAppCertificatesAsync())
        {
            certificates.Add(CertificateItem.Create(certificate.Data));
        }

        return certificates;
    }

    [FunctionName(nameof(Order))]
    public async Task<OrderDetails> Order([ActivityTrigger] IReadOnlyList<string> dnsNames)
    {
        var acmeProtocolClient = await _acmeProtocolClientFactory.CreateClientAsync();

        return await acmeProtocolClient.CreateOrderAsync(dnsNames);
    }

    [FunctionName(nameof(Http01Precondition))]
    public Task Http01Precondition([ActivityTrigger] string id)
    {
        // Disabled entirely: challenge responses are served from blob storage, so no
        // .well-known virtual application (and the app restart it triggers) is needed.
        return Task.CompletedTask;
    }

    [FunctionName(nameof(Http01Authorization))]
    public async Task<IReadOnlyList<AcmeChallengeResult>> Http01Authorization([ActivityTrigger] (string, IReadOnlyList<string>) input)
    {
        var (id, authorizationUrls) = input;

        var acmeProtocolClient = await _acmeProtocolClientFactory.CreateClientAsync();

        var challengeResults = new List<AcmeChallengeResult>();

        foreach (var authorizationUrl in authorizationUrls)
        {
            // Authorization の詳細を取得
            var authorization = await acmeProtocolClient.GetAuthorizationDetailsAsync(authorizationUrl);

            // HTTP-01 Challenge の情報を拾う
            var challenge = authorization.Challenges.FirstOrDefault(x => x.Type == "http-01");

            if (challenge is null)
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

        // Write the challenge responses to blob storage instead of using the Kudu API,
        // to avoid the app service restarts that Kudu-based file writes induce.
        foreach (var challengeResult in challengeResults)
        {
            var blobClient = _blobContainerClient.GetBlobClient(challengeResult.HttpResourcePath);
            var content = new BinaryData(System.Text.Encoding.UTF8.GetBytes(challengeResult.HttpResourceValue));
            await blobClient.UploadAsync(content, overwrite: true);
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
        var subscription = await _armClient.GetDefaultSubscriptionAsync();

        var dnsZones = await subscription.ListAllDnsZonesAsync();

        var foundDnsZones = new HashSet<DnsZoneData>();
        var zoneNotFoundDnsNames = new List<string>();

        foreach (var dnsName in dnsNames)
        {
            var dnsZone = dnsZones.Where(x => string.Equals(dnsName, x.Data.Name, StringComparison.OrdinalIgnoreCase) || dnsName.EndsWith($".{x.Data.Name}", StringComparison.OrdinalIgnoreCase))
                                  .MaxBy(x => x.Data.Name.Length);

            // マッチする DNS zone が見つからない場合はエラー
            if (dnsZone is null)
            {
                zoneNotFoundDnsNames.Add(dnsName);
                continue;
            }

            foundDnsZones.Add(dnsZone.Data);
        }

        if (zoneNotFoundDnsNames.Count > 0)
        {
            throw new PreconditionException($"DNS zone(s) are not found. DnsNames = {string.Join(",", zoneNotFoundDnsNames)}");
        }

        // DNS zone に移譲されている Name servers が正しいか検証
        foreach (var dnsZone in foundDnsZones)
        {
            // DNS provider が Name servers を返していなければスキップ
            if (dnsZone.NameServers is null || dnsZone.NameServers.Count == 0)
            {
                continue;
            }

            // DNS provider が Name servers を返している場合は NS レコードを確認
            var queryResult = await _lookupClient.QueryAsync(dnsZone.Name, QueryType.NS);

            // 最後の . が付いている場合があるので削除して統一
            var expectedNameServers = dnsZone.NameServers
                                             .Select(x => x.TrimEnd('.'))
                                             .ToArray();

            var actualNameServers = queryResult.Answers
                                               .OfType<DnsClient.Protocol.NsRecord>()
                                               .Select(x => x.NSDName.Value.TrimEnd('.'))
                                               .ToArray();

            // 処理対象の DNS zone から取得した NS と実際に引いた NS の値が一つも一致しない場合はエラー
            if (!actualNameServers.Intersect(expectedNameServers, StringComparer.OrdinalIgnoreCase).Any())
            {
                throw new PreconditionException($"The delegated name server is not correct. DNS zone = {dnsZone.Name}, Expected = {string.Join(",", expectedNameServers)}, Actual = {string.Join(",", actualNameServers)}");
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

            if (challenge is null)
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
        var subscription = await _armClient.GetDefaultSubscriptionAsync();

        var dnsZones = await subscription.ListAllDnsZonesAsync();

        // DNS-01 の検証レコード名毎に Azure DNS に TXT レコードを作成
        foreach (var lookup in challengeResults.ToLookup(x => x.DnsRecordName))
        {
            var dnsRecordName = lookup.Key;

            var dnsZone = dnsZones.Where(x => dnsRecordName.EndsWith($".{x.Data.Name}", StringComparison.OrdinalIgnoreCase))
                                  .OrderByDescending(x => x.Data.Name.Length)
                                  .First();

            // Challenge の詳細から Azure DNS 向けにレコード名を作成
            var acmeDnsRecordName = dnsRecordName.Replace($".{dnsZone.Data.Name}", "", StringComparison.OrdinalIgnoreCase);

            // TXT レコードに TTL と値をセットする
            var recordSets = dnsZone.GetDnsTxtRecords();

            var recordSet = new DnsTxtRecordData
            {
                TtlInSeconds = 60
            };

            foreach (var value in lookup)
            {
                recordSet.DnsTxtRecords.Add(new DnsTxtRecordInfo { Values = { value.DnsRecordValue } });
            }

            await recordSets.CreateOrUpdateAsync(WaitUntil.Completed, acmeDnsRecordName, recordSet);
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
        foreach (var challengeResult in challengeResults)
        {
            await acmeProtocolClient.AnswerChallengeAsync(challengeResult.Url);
        }
    }

    [FunctionName(nameof(CheckIsReady))]
    public async Task CheckIsReady([ActivityTrigger] (OrderDetails, IReadOnlyList<AcmeChallengeResult>) input)
    {
        var (orderDetails, challengeResults) = input;

        var acmeProtocolClient = await _acmeProtocolClientFactory.CreateClientAsync();

        orderDetails = await acmeProtocolClient.GetOrderDetailsAsync(orderDetails.OrderUrl, orderDetails);

        // Include the DNS domain (if available) in exception messages to ease troubleshooting
        var identifiers = orderDetails?.Payload?.Identifiers ?? Array.Empty<Identifier>();
        var firstDomain = identifiers.Select(x => $" ({x.Value})").FirstOrDefault();

        if (orderDetails.Payload.Status == "invalid")
        {
            var problems = new List<Problem>();

            foreach (var challengeResult in challengeResults)
            {
                var challenge = await acmeProtocolClient.GetChallengeDetailsAsync(challengeResult.Url);

                if (challenge.Status != "invalid" || challenge.Error is null)
                {
                    continue;
                }

                _logger.LogError($"ACME domain validation error: {JsonConvert.SerializeObject(challenge.Error)}");

                problems.Add(challenge.Error);
            }

            // 全てのエラーが connection か dns 関係の場合は Orchestrator からリトライさせる
            if (problems.All(x => x.Type is "urn:ietf:params:acme:error:connection" or "urn:ietf:params:acme:error:dns"))
            {
                throw new RetriableOrchestratorException($"ACME validation status{firstDomain} is invalid, but retriable error. It will retry automatically.");
            }

            // invalid の場合は最初から実行が必要なので失敗させる
            throw new InvalidOperationException($"ACME validation status{firstDomain} is invalid. Required retry at first.\nLastError = {JsonConvert.SerializeObject(problems.Last())}");
        }

        if (orderDetails.Payload.Status != "ready")
        {
            // ready 以外の場合はリトライする
            throw new RetriableActivityException($"ACME validation status{firstDomain} is {orderDetails.Payload.Status}. It will retry automatically.");
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

        if (orderDetails.Payload.Status == "invalid")
        {
            // invalid の場合は最初から実行が必要なので失敗させる
            throw new InvalidOperationException("Finalize request is invalid. Required retry at first.");
        }

        if (orderDetails.Payload.Status != "valid")
        {
            // valid 以外の場合はリトライする
            throw new RetriableActivityException($"Finalize request is {orderDetails.Payload.Status}. It will retry automatically.");
        }

        return orderDetails;
    }

    [FunctionName(nameof(UploadCertificate))]
    public async Task<CertificateItem> UploadCertificate([ActivityTrigger] (string, string, bool, OrderDetails, RSAParameters) input)
    {
        var (id, dnsName, forceDns01Challenge, orderDetails, rsaParameters) = input;

        var acmeProtocolClient = await _acmeProtocolClientFactory.CreateClientAsync();

        // 証明書をバイト配列としてダウンロード
        var x509Certificates = await acmeProtocolClient.GetOrderCertificateAsync(orderDetails, _options.PreferredChain, _logger);

        // 秘密鍵を含んだ形で X509Certificate2 を作成
        var rsa = RSA.Create(rsaParameters);

        x509Certificates[0] = x509Certificates[0].CopyWithPrivateKey(rsa);

        // PFX 形式としてエクスポート
        var password = Guid.NewGuid().ToString();

        var pfxBlob = x509Certificates.Export(X509ContentType.Pfx, password);

        var certificateName = $"{dnsName}-{x509Certificates[0].Thumbprint}";

        // Also import the certificate into Key Vault
        var importCertificateOptions = new ImportCertificateOptions(dnsName.Replace(".", "-"), pfxBlob)
        {
            Password = password
        };

        await _certificateClient.ImportCertificateAsync(importCertificateOptions);

        var resourceId = new ResourceIdentifier(id);

        AzureLocation location;
        ResourceIdentifier appServicePlanId;

        if (resourceId.ResourceType == WebSiteResource.ResourceType)
        {
            WebSiteResource webSite = await _armClient.GetWebSiteResource(resourceId).GetAsync();

            location = webSite.Data.Location;
            appServicePlanId = webSite.Data.AppServicePlanId;
        }
        else
        {
            WebSiteSlotResource webSiteSlot = await _armClient.GetWebSiteSlotResource(resourceId).GetAsync();

            location = webSiteSlot.Data.Location;
            appServicePlanId = webSiteSlot.Data.AppServicePlanId;
        }

        var resourceGroup = _armClient.GetResourceGroupResource(ResourceGroupResource.CreateResourceIdentifier(resourceId.SubscriptionId, resourceId.ResourceGroupName));

        var certificateCollection = resourceGroup.GetAppCertificates();

        var result = await certificateCollection.CreateOrUpdateAsync(WaitUntil.Completed, certificateName, new AppCertificateData(location)
        {
            Password = password,
            PfxBlob = pfxBlob,
            ServerFarmId = appServicePlanId,
            Tags =
            {
                { "Issuer", IssuerName },
                { "Endpoint", _options.Endpoint.Host },
                { "ForceDns01Challenge", forceDns01Challenge.ToString() }
            }
        });

        return CertificateItem.Create(result.Value.Data);
    }

    [FunctionName(nameof(UpdateSiteBinding))]
    public async Task UpdateSiteBinding([ActivityTrigger] (string, IReadOnlyList<string>, string, bool?) input)
    {
        var (id, dnsNames, thumbprint, useIpBasedSsl) = input;

        var resourceId = new ResourceIdentifier(id);

        if (resourceId.ResourceType == WebSiteResource.ResourceType)
        {
            await UpdateSiteBinding_WebSite(resourceId, dnsNames, thumbprint, useIpBasedSsl);
        }
        else
        {
            await UpdateSiteBinding_WebSiteSlot(resourceId, dnsNames, thumbprint, useIpBasedSsl);
        }
    }

    // Update each host name binding individually with a minimal payload (sslState + thumbprint),
    // instead of updating the whole site object. Updating the whole site triggers DNS
    // re-registration and app restarts, which have caused issues in the past.
    private async Task UpdateSiteBinding_WebSite(ResourceIdentifier resourceId, IReadOnlyList<string> dnsNames, string thumbprint, bool? useIpBasedSsl)
    {
        WebSiteResource webSite = await _armClient.GetWebSiteResource(resourceId).GetAsync();

        var sslState = useIpBasedSsl ?? false ? HostNameBindingSslState.IPBasedEnabled : HostNameBindingSslState.SniEnabled;

        foreach (var hostNameSslState in webSite.Data.HostNameSslStates.Where(x => dnsNames.Contains(Punycode.Encode(x.Name))))
        {
            _logger.LogInformation($"Updating host name binding for {hostNameSslState.Name} with thumbprint {thumbprint}");

            await webSite.GetSiteHostNameBindings().CreateOrUpdateAsync(WaitUntil.Completed, hostNameSslState.Name, new HostNameBindingData
            {
                SslState = sslState,
                ThumbprintString = thumbprint
            });
        }
    }

    private async Task UpdateSiteBinding_WebSiteSlot(ResourceIdentifier resourceId, IReadOnlyList<string> dnsNames, string thumbprint, bool? useIpBasedSsl)
    {
        WebSiteSlotResource webSiteSlot = await _armClient.GetWebSiteSlotResource(resourceId).GetAsync();

        var sslState = useIpBasedSsl ?? false ? HostNameBindingSslState.IPBasedEnabled : HostNameBindingSslState.SniEnabled;

        foreach (var hostNameSslState in webSiteSlot.Data.HostNameSslStates.Where(x => dnsNames.Contains(Punycode.Encode(x.Name))))
        {
            _logger.LogInformation($"Updating host name binding for {hostNameSslState.Name} with thumbprint {thumbprint}");

            await webSiteSlot.GetSiteSlotHostNameBindings().CreateOrUpdateAsync(WaitUntil.Completed, hostNameSslState.Name, new HostNameBindingData
            {
                SslState = sslState,
                ThumbprintString = thumbprint
            });
        }
    }

    [FunctionName(nameof(CleanupDnsChallenge))]
    public async Task CleanupDnsChallenge([ActivityTrigger] IReadOnlyList<AcmeChallengeResult> challengeResults)
    {
        // Azure DNS zone の一覧を取得する
        var subscription = await _armClient.GetDefaultSubscriptionAsync();

        var dnsZones = await subscription.ListAllDnsZonesAsync();

        // DNS-01 の検証レコード名毎に Azure DNS から TXT レコードを削除
        foreach (var lookup in challengeResults.ToLookup(x => x.DnsRecordName))
        {
            var dnsRecordName = lookup.Key;

            var dnsZone = dnsZones.Where(x => dnsRecordName.EndsWith($".{x.Data.Name}", StringComparison.OrdinalIgnoreCase))
                                  .OrderByDescending(x => x.Data.Name.Length)
                                  .First();

            // Challenge の詳細から Azure DNS 向けにレコード名を作成
            var acmeDnsRecordName = dnsRecordName.Replace($".{dnsZone.Data.Name}", "", StringComparison.OrdinalIgnoreCase);

            DnsTxtRecordResource recordSet = await dnsZone.GetDnsTxtRecordAsync(acmeDnsRecordName);

            await recordSet.DeleteAsync(WaitUntil.Completed);
        }
    }

    [FunctionName(nameof(CleanupVirtualApplication))]
    public Task CleanupVirtualApplication([ActivityTrigger] string id)
    {
        // Disabled entirely: no virtual application is created (challenge responses are
        // served from blob storage), so there is nothing to clean up.
        return Task.CompletedTask;
    }

    [FunctionName(nameof(DeleteCertificate))]
    public async Task DeleteCertificate([ActivityTrigger] string id)
    {
        var certificateResource = _armClient.GetAppCertificateResource(new ResourceIdentifier(id));

        await certificateResource.DeleteAsync(WaitUntil.Completed);
    }

    [FunctionName(nameof(SendCompletedEvent))]
    public async Task SendCompletedEvent([ActivityTrigger] (WebSiteItem, DateTimeOffset?, IReadOnlyList<string>) input)
    {
        var (site, expirationDate, dnsNames) = input;

        await _webhookInvoker.SendCompletedEventAsync(site.Name, site.SlotName, expirationDate, dnsNames);
    }
}
