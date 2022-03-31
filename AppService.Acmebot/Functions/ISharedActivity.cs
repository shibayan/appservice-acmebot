using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;

using ACMESharp.Protocol;

using AppService.Acmebot.Models;

using DurableTask.TypedProxy;

using Microsoft.Azure.Management.WebSites.Models;

namespace AppService.Acmebot.Functions
{
    public interface ISharedActivity
    {
        Task<IReadOnlyList<string>> GetResourceGroups(object input = null);

        Task<Site> GetSite((string, string, string) input);

        Task<IReadOnlyList<Site>> GetSites((string, bool) input);

        Task<IReadOnlyList<Certificate>> GetExpiringCertificates(DateTime currentDateTime);

        Task<IReadOnlyList<Certificate>> GetAllCertificates(object input = null);

        Task<OrderDetails> Order(IReadOnlyList<string> dnsNames);

        Task Http01Precondition(Site site);

        Task<IReadOnlyList<AcmeChallengeResult>> Http01Authorization((Site, IReadOnlyList<string>) input);

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

        Task<Certificate> UploadCertificate((Site, string, bool, OrderDetails, RSAParameters) input);

        Task UpdateSiteBinding(Site site);

        Task CleanupVirtualApplication(Site site);

        Task DeleteCertificate(Certificate certificate);

        Task CleanupDnsChallenge(IReadOnlyList<AcmeChallengeResult> challengeResults);

        Task SendCompletedEvent((Site, DateTime?, IReadOnlyList<string>) input);
    }
}
