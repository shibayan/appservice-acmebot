using AzureAppService.LetsEncrypt.Internal;

using DnsClient;

using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.Azure.Management.Dns;
using Microsoft.Azure.Management.WebSites;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Rest;

[assembly: FunctionsStartup(typeof(AzureAppService.LetsEncrypt.Startup))]

namespace AzureAppService.LetsEncrypt
{
    public class Startup : FunctionsStartup
    {
        public override void Configure(IFunctionsHostBuilder builder)
        {
            builder.Services.AddSingleton(new LookupClient { UseCache = false });

            builder.Services.AddSingleton(provider => new WebSiteManagementClient(new TokenCredentials(new AppAuthenticationTokenProvider()))
            {
                SubscriptionId = Settings.Default.SubscriptionId
            });

            builder.Services.AddSingleton(provider => new DnsManagementClient(new TokenCredentials(new AppAuthenticationTokenProvider()))
            {
                SubscriptionId = Settings.Default.SubscriptionId
            });
        }
    }
}
