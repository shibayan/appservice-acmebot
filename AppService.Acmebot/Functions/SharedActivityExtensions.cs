using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;

using ACMESharp.Protocol;

using AppService.Acmebot.Models;

using Microsoft.DurableTask;

namespace AppService.Acmebot.Functions;

/// <summary>
/// Typed wrappers around the SharedActivity functions, replacing the in-process
/// DurableTask.TypedProxy ISharedActivity proxy. Retry policies mirror the
/// [RetryOptions] attributes the interface used to carry.
/// </summary>
internal static class SharedActivityExtensions
{
    // [RetryOptions("00:00:10", 12, HandlerType = ExceptionRetryStrategy<RetriableActivityException>)]
    private static readonly TaskOptions RetriableSlow = TaskOptions.FromRetryPolicy(new RetryPolicy(12, TimeSpan.FromSeconds(10))
    {
        HandleFailure = failure => failure.IsCausedBy<RetriableActivityException>()
    });

    // [RetryOptions("00:00:05", 12, HandlerType = ExceptionRetryStrategy<RetriableActivityException>)]
    private static readonly TaskOptions RetriableFast = TaskOptions.FromRetryPolicy(new RetryPolicy(12, TimeSpan.FromSeconds(5))
    {
        HandleFailure = failure => failure.IsCausedBy<RetriableActivityException>()
    });

    // [RetryOptions("00:00:15", 20, BackoffCoefficient = 2.0, MaxRetryInterval = "01:00:00")]
    private static readonly TaskOptions BindingRetry = TaskOptions.FromRetryPolicy(new RetryPolicy(20, TimeSpan.FromSeconds(15), 2.0, TimeSpan.FromHours(1)));

    public static Task<IReadOnlyList<ResourceGroupItem>> GetResourceGroups(this TaskOrchestrationContext context)
        => context.CallActivityAsync<IReadOnlyList<ResourceGroupItem>>(nameof(SharedActivity.GetResourceGroups), null);

    public static Task<WebSiteItem> GetWebSite(this TaskOrchestrationContext context, (string, string, string) input)
        => context.CallActivityAsync<WebSiteItem>(nameof(SharedActivity.GetWebSite), input);

    public static Task<IReadOnlyList<WebSiteItem>> GetWebSites(this TaskOrchestrationContext context, string resourceGroupName)
        => context.CallActivityAsync<IReadOnlyList<WebSiteItem>>(nameof(SharedActivity.GetWebSites), resourceGroupName);

    public static Task<IReadOnlyList<WebSiteItem>> GetWebSiteSlots(this TaskOrchestrationContext context, (string, string) input)
        => context.CallActivityAsync<IReadOnlyList<WebSiteItem>>(nameof(SharedActivity.GetWebSiteSlots), input);

    public static Task<IReadOnlyList<CertificateItem>> GetExpiringCertificates(this TaskOrchestrationContext context, DateTime currentDateTime)
        => context.CallActivityAsync<IReadOnlyList<CertificateItem>>(nameof(SharedActivity.GetExpiringCertificates), currentDateTime);

    public static Task<IReadOnlyList<CertificateItem>> GetAllCertificates(this TaskOrchestrationContext context)
        => context.CallActivityAsync<IReadOnlyList<CertificateItem>>(nameof(SharedActivity.GetAllCertificates), null);

    public static Task<OrderDetails> Order(this TaskOrchestrationContext context, IReadOnlyList<string> dnsNames)
        => context.CallActivityAsync<OrderDetails>(nameof(SharedActivity.Order), dnsNames);

    public static Task Http01Precondition(this TaskOrchestrationContext context, string id)
        => context.CallActivityAsync(nameof(SharedActivity.Http01Precondition), id);

    public static Task<IReadOnlyList<AcmeChallengeResult>> Http01Authorization(this TaskOrchestrationContext context, (string, IReadOnlyList<string>) input)
        => context.CallActivityAsync<IReadOnlyList<AcmeChallengeResult>>(nameof(SharedActivity.Http01Authorization), input);

    public static Task CheckHttpChallenge(this TaskOrchestrationContext context, IReadOnlyList<AcmeChallengeResult> challengeResults)
        => context.CallActivityAsync(nameof(SharedActivity.CheckHttpChallenge), challengeResults, RetriableSlow);

    public static Task Dns01Precondition(this TaskOrchestrationContext context, IReadOnlyList<string> dnsNames)
        => context.CallActivityAsync(nameof(SharedActivity.Dns01Precondition), dnsNames);

    public static Task<IReadOnlyList<AcmeChallengeResult>> Dns01Authorization(this TaskOrchestrationContext context, IReadOnlyList<string> authorizationUrls)
        => context.CallActivityAsync<IReadOnlyList<AcmeChallengeResult>>(nameof(SharedActivity.Dns01Authorization), authorizationUrls);

    public static Task CheckDnsChallenge(this TaskOrchestrationContext context, IReadOnlyList<AcmeChallengeResult> challengeResults)
        => context.CallActivityAsync(nameof(SharedActivity.CheckDnsChallenge), challengeResults, RetriableSlow);

    public static Task AnswerChallenges(this TaskOrchestrationContext context, IReadOnlyList<AcmeChallengeResult> challengeResults)
        => context.CallActivityAsync(nameof(SharedActivity.AnswerChallenges), challengeResults);

    public static Task CheckIsReady(this TaskOrchestrationContext context, (OrderDetails, IReadOnlyList<AcmeChallengeResult>) input)
        => context.CallActivityAsync(nameof(SharedActivity.CheckIsReady), input, RetriableFast);

    public static Task<(OrderDetails, RSAParameters)> FinalizeOrder(this TaskOrchestrationContext context, (IReadOnlyList<string>, OrderDetails) input)
        => context.CallActivityAsync<(OrderDetails, RSAParameters)>(nameof(SharedActivity.FinalizeOrder), input);

    public static Task<OrderDetails> CheckIsValid(this TaskOrchestrationContext context, OrderDetails orderDetails)
        => context.CallActivityAsync<OrderDetails>(nameof(SharedActivity.CheckIsValid), orderDetails, RetriableFast);

    public static Task<CertificateItem> UploadCertificate(this TaskOrchestrationContext context, (string, string, bool, OrderDetails, RSAParameters) input)
        => context.CallActivityAsync<CertificateItem>(nameof(SharedActivity.UploadCertificate), input);

    public static Task UpdateSiteBinding(this TaskOrchestrationContext context, (string, IReadOnlyList<string>, string, bool?) input)
        => context.CallActivityAsync(nameof(SharedActivity.UpdateSiteBinding), input, BindingRetry);

    public static Task CleanupVirtualApplication(this TaskOrchestrationContext context, string id)
        => context.CallActivityAsync(nameof(SharedActivity.CleanupVirtualApplication), id);

    public static Task DeleteCertificate(this TaskOrchestrationContext context, string id)
        => context.CallActivityAsync(nameof(SharedActivity.DeleteCertificate), id);

    public static Task CleanupDnsChallenge(this TaskOrchestrationContext context, IReadOnlyList<AcmeChallengeResult> challengeResults)
        => context.CallActivityAsync(nameof(SharedActivity.CleanupDnsChallenge), challengeResults);

    public static Task SendCompletedEvent(this TaskOrchestrationContext context, (WebSiteItem, DateTimeOffset?, IReadOnlyList<string>) input)
        => context.CallActivityAsync(nameof(SharedActivity.SendCompletedEvent), input);

    public static Task SendFailedEvent(this TaskOrchestrationContext context, (string, string) input)
        => context.CallActivityAsync(nameof(SharedActivity.SendFailedEvent), input);
}
