using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

using ACMESharp.Protocol;

using Microsoft.Azure.Management.WebSites.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace AzureLetsEncrypt
{
    public static class AddCertificate
    {
        [FunctionName("AddCertificate")]
        public static async Task RunOrchestrator([OrchestrationTrigger] DurableOrchestrationContext context, ILogger log)
        {
            var request = context.GetInput<AddCertificateRequest>();

            var site = await context.CallActivityAsync<Site>(nameof(SharedFunctions.GetSite), (request.ResourceGroupName, request.SiteName));

            if (site == null)
            {
                log.LogInformation($"{request.SiteName} is not found");
                return;
            }

            var hostNameSslStates = site.HostNameSslStates
                                        .Where(x => request.Domains.Contains(x.Name))
                                        .ToArray();

            if (hostNameSslStates.Length != request.Domains.Length)
            {
                log.LogInformation($"{request.Domains} is not found");
                return;
            }

            // ワイルドカード、コンテナ、Linux の場合は DNS-01 を利用する
            var useDns01Auth = request.Domains.Any(x => x.StartsWith("*")) || site.Kind.Contains("container") || site.Kind.Contains("linux");

            // 前提条件をチェック
            if (useDns01Auth)
            {
                await context.CallActivityAsync(nameof(SharedFunctions.Dns01Precondition), request.Domains);
            }
            else
            {
                await context.CallActivityAsync(nameof(SharedFunctions.Http01Precondition), site);
            }

            // 新しく ACME Order を作成する
            var orderDetails = await context.CallActivityAsync<OrderDetails>(nameof(SharedFunctions.Order), request.Domains);

            // 複数の Authorizations には未対応
            foreach (var authorization in orderDetails.Payload.Authorizations)
            {
                // ACME Challenge を実行
                if (useDns01Auth)
                {
                    await context.CallActivityAsync(nameof(SharedFunctions.Dns01Authorization), authorization);
                }
                else
                {
                    await context.CallActivityAsync(nameof(SharedFunctions.Http01Authorization), (site, authorization));
                }
            }

            // Order status が ready になるまで待つ
            await context.CallActivityAsync(nameof(SharedFunctions.WaitChallenge), orderDetails);

            var (thumbprint, pfxBlob) = await context.CallActivityAsync<(string, byte[])>(nameof(SharedFunctions.FinalizeOrder), (request.Domains, orderDetails));

            await context.CallActivityAsync(nameof(SharedFunctions.UpdateCertificate), (site, $"{request.Domains[0]}-{thumbprint}", pfxBlob));

            foreach (var hostNameSslState in hostNameSslStates)
            {
                hostNameSslState.Thumbprint = thumbprint;
                hostNameSslState.SslState = request.UseIpBasedSsl ?? false ? SslState.IpBasedEnabled : SslState.SniEnabled;
                hostNameSslState.ToUpdate = true;
            }

            await context.CallActivityAsync(nameof(SharedFunctions.UpdateSiteBinding), site);
        }

        [FunctionName("AddCertificate_HttpStart")]
        public static async Task<HttpResponseMessage> HttpStart(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestMessage req,
            [OrchestrationClient] DurableOrchestrationClient starter,
            ILogger log)
        {
            var request = await req.Content.ReadAsAsync<AddCertificateRequest>();

            if (string.IsNullOrEmpty(request.ResourceGroupName))
            {
                return req.CreateErrorResponse(System.Net.HttpStatusCode.BadRequest, $"{nameof(request.ResourceGroupName)} is empty.");
            }

            if (string.IsNullOrEmpty(request.SiteName))
            {
                return req.CreateErrorResponse(System.Net.HttpStatusCode.BadRequest, $"{nameof(request.SiteName)} is empty.");
            }

            if (request.Domains == null || request.Domains.Length == 0)
            {
                return req.CreateErrorResponse(System.Net.HttpStatusCode.BadRequest, $"{nameof(request.Domains)} is empty.");
            }

            // Function input comes from the request content.
            var instanceId = await starter.StartNewAsync("AddCertificate", request);

            log.LogInformation($"Started orchestration with ID = '{instanceId}'.");

            return starter.CreateCheckStatusResponse(req, instanceId);
        }
    }

    public class AddCertificateRequest
    {
        public string ResourceGroupName { get; set; }
        public string SiteName { get; set; }
        public string[] Domains { get; set; }
        public bool? UseIpBasedSsl { get; set; }
    }
}