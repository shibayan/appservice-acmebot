using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;

using ACMESharp.Protocol;

using AppService.Acmebot.Models;

using Azure.Core;
using Azure.ResourceManager.AppService;

using DurableTask.TypedProxy;

namespace AppService.Acmebot.Functions;

public interface ISharedActivity
{
    Task<IReadOnlyList<string>> GetResourceGroups(object input = null);

    Task<WebSiteData> GetSite((string, string, string) input);

    Task<IReadOnlyList<WebSiteData>> GetSites((string, bool) input);

    Task<IReadOnlyList<CertificateData>> GetExpiringCertificates(DateTime currentDateTime);

    Task<IReadOnlyList<CertificateData>> GetAllCertificates(object input = null);

    Task<OrderDetails> Order(IReadOnlyList<string> dnsNames);

    Task Http01Precondition(ResourceIdentifier id);

    Task<IReadOnlyList<AcmeChallengeResult>> Http01Authorization((ResourceIdentifier, IReadOnlyList<string>) input);

    [RetryOptions("00:00:10", 12, HandlerType = typeof(ExceptionRetryStrategy<RetriableActivityException>))]
    Task CheckHttpChallenge(IReadOnlyList<AcmeChallengeResult> challengeResults);

    Task Dns01Precondition(IReadOnlyList<string> dnsNames);

    Task<IReadOnlyList<AcmeChallengeResult>> Dns01Authorization(IReadOnlyList<string> authorizationUrls);

    [RetryOptions("00:00:10", 12, HandlerType = typeof(ExceptionRetryStrategy<RetriableActivityException>))]
    Task CheckDnsChallenge(IReadOnlyList<AcmeChallengeResult> challengeResults);

    Task AnswerChallenges(IReadOnlyList<AcmeChallengeResult> challengeResults);

    [RetryOptions("00:00:05", 12, HandlerType = typeof(ExceptionRetryStrategy<RetriableActivityException>))]
    Task CheckIsReady((OrderDetails, IReadOnlyList<AcmeChallengeResult>) input);

    Task<(OrderDetails, RSAParameters)> FinalizeOrder((IReadOnlyList<string>, OrderDetails) input);

    [RetryOptions("00:00:05", 12, HandlerType = typeof(ExceptionRetryStrategy<RetriableActivityException>))]
    Task<OrderDetails> CheckIsValid(OrderDetails orderDetails);

    Task<CertificateData> UploadCertificate((ResourceIdentifier, string, bool, OrderDetails, RSAParameters) input);

    Task UpdateSiteBinding(ResourceIdentifier id);

    Task CleanupVirtualApplication(ResourceIdentifier id);

    Task DeleteCertificate(ResourceIdentifier id);

    Task CleanupDnsChallenge(IReadOnlyList<AcmeChallengeResult> challengeResults);

    Task SendCompletedEvent((WebSiteData, DateTimeOffset?, IReadOnlyList<string>) input);
}
