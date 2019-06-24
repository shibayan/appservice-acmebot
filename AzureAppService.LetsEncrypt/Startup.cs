using DnsClient;

using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

[assembly: FunctionsStartup(typeof(AzureAppService.LetsEncrypt.Startup))]

namespace AzureAppService.LetsEncrypt
{
    public class Startup : FunctionsStartup
    {
        public override void Configure(IFunctionsHostBuilder builder)
        {
            builder.Services.AddSingleton(new LookupClient { UseCache = false });
        }
    }
}
