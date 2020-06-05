using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using ACMESharp.Protocol;

using AppService.Acmebot.Models;

using DurableTask.TypedProxy;

using Microsoft.Azure.Management.WebSites.Models;

namespace AppService.Acmebot.Contracts
{
    public interface ISharedFunctions
    {
        Task<Site> GetSite((string, string, string) input);

        Task<IList<Site>> GetSites(object input = null);

        Task<IList<Certificate>> GetCertificates(DateTime currentDateTime);

        Task<IList<Certificate>> GetAllCertificates(object input = null);

        Task<OrderDetails> Order(IList<string> dnsNames);

        Task Http01Precondition(Site site);

        Task<IList<AcmeChallengeResult>> Http01Authorization((Site, string[]) input);

        [RetryOptions("00:00:10", 12, HandlerType = typeof(RetryStrategy), HandlerMethodName = nameof(RetryStrategy.RetriableException))]
        Task CheckHttpChallenge(IList<AcmeChallengeResult> challengeResults);

        Task Dns01Precondition(IList<string> dnsNames);

        Task<IList<AcmeChallengeResult>> Dns01Authorization(string[] authorizationUrls);

        [RetryOptions("00:00:10", 12, HandlerType = typeof(RetryStrategy), HandlerMethodName = nameof(RetryStrategy.RetriableException))]
        Task CheckDnsChallenge(IList<AcmeChallengeResult> challengeResults);

        [RetryOptions("00:00:05", 12, HandlerType = typeof(RetryStrategy), HandlerMethodName = nameof(RetryStrategy.RetriableException))]
        Task CheckIsReady(OrderDetails orderDetails);

        Task AnswerChallenges(IList<AcmeChallengeResult> challengeResults);

        Task<(string, byte[])> FinalizeOrder((IList<string>, OrderDetails) input);

        Task<Certificate> UploadCertificate((Site, string, byte[]) input);

        Task UpdateSiteBinding(Site site);

        Task CleanupVirtualApplication(Site site);

        Task DeleteCertificate(Certificate certificate);

        Task SendCompletedEvent((Site, DateTime?, string[]) input);
    }
}
