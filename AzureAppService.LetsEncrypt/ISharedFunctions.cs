using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using ACMESharp.Protocol;

using Microsoft.Azure.Management.WebSites.Models;
using Microsoft.Azure.WebJobs;

namespace AzureAppService.LetsEncrypt
{
    public interface ISharedFunctions
    {
        Task<Site> GetSite((string, string, string) input);
        Task<IList<Site>> GetSites(object input);
        Task<IList<Certificate>> GetCertificates(DateTime currentDateTime);
        Task<IList<Certificate>> GetAllCertificates(object input);
        Task<OrderDetails> Order(IList<string> hostNames);
        Task Http01Precondition(Site site);
        Task<ChallengeResult> Http01Authorization((Site, string) input);
        Task CheckHttpChallenge(ChallengeResult challenge);
        Task Dns01Precondition(IList<string> hostNames);
        Task<ChallengeResult> Dns01Authorization((string, string) context);
        Task CheckDnsChallenge(ChallengeResult challenge);
        Task CheckIsReady(OrderDetails orderDetails);
        Task AnswerChallenges(IList<ChallengeResult> challenges);
        Task<(string, byte[])> FinalizeOrder((IList<string>, OrderDetails) input);
        Task UpdateCertificate((Site, string, byte[]) input);
        Task UpdateSiteBinding(Site site);
        Task DeleteCertificate(Certificate certificate);
    }
}