using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;

using ACMESharp.Protocol;

using AppService.Acmebot.Models;

using DurableTask.TypedProxy;

namespace AppService.Acmebot.Functions;

public interface ISharedActivity
{
    Task<IReadOnlyList<ResourceGroupItem>> GetResourceGroups(object input = null);

    Task<WebSiteItem> GetWebSite((string, string, string) input);

    Task<IReadOnlyList<WebSiteItem>> GetWebSites(string resourceGroupName);

    Task<IReadOnlyList<WebSiteItem>> GetWebSiteSlots((string, string) input);

    Task<IReadOnlyList<CertificateItem>> GetExpiringCertificates(DateTime currentDateTime);

    Task<IReadOnlyList<CertificateItem>> GetAllCertificates(object input = null);

    Task<OrderDetails> Order(IReadOnlyList<string> dnsNames);

    Task Http01Precondition(string id);

    Task<IReadOnlyList<AcmeChallengeResult>> Http01Authorization((string, IReadOnlyList<string>) input);

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

    Task<CertificateItem> UploadCertificate((string, string, bool, OrderDetails, RSAParameters) input);

    Task UpdateSiteBinding((string, IReadOnlyList<string>, string, bool?) input);

    Task CleanupVirtualApplication(string id);

    Task DeleteCertificate(string id);

    Task CleanupDnsChallenge(IReadOnlyList<AcmeChallengeResult> challengeResults);

    Task SendCompletedEvent((WebSiteItem, DateTimeOffset?, IReadOnlyList<string>) input);
}
