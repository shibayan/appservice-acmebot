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

        Task<OrderDetails> Order(IList<string> hostNames);

        Task Http01Precondition(Site site);

        Task<AcmeChallengeResult> Http01Authorization((Site, string) input);

        [RetryOptions("00:00:10", 6, HandlerType = typeof(RetryStrategy), HandlerMethodName = nameof(RetryStrategy.RetriableException))]
        Task CheckHttpChallenge(AcmeChallengeResult challenge);

        Task Dns01Precondition(IList<string> hostNames);

        Task<AcmeChallengeResult> Dns01Authorization((string, string) context);

        [RetryOptions("00:00:10", 6, HandlerType = typeof(RetryStrategy), HandlerMethodName = nameof(RetryStrategy.RetriableException))]
        Task CheckDnsChallenge(AcmeChallengeResult challenge);

        [RetryOptions("00:00:05", 12, HandlerType = typeof(RetryStrategy), HandlerMethodName = nameof(RetryStrategy.RetriableException))]
        Task CheckIsReady(OrderDetails orderDetails);

        Task AnswerChallenges(IList<AcmeChallengeResult> challenges);

        Task<(string, byte[])> FinalizeOrder((IList<string>, OrderDetails) input);

        Task UpdateCertificate((Site, string, byte[]) input);

        Task UpdateSiteBinding(Site site);

        Task CleanupVirtualApplication(Site site);

        Task DeleteCertificate(Certificate certificate);
    }
}