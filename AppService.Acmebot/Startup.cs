using System.Net.Http;

using AppService.Acmebot;
using AppService.Acmebot.Internal;

using DnsClient;

using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.Azure.Management.Dns;
using Microsoft.Azure.Management.WebSites;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Rest;

[assembly: FunctionsStartup(typeof(Startup))]

namespace AppService.Acmebot
{
    public class Startup : FunctionsStartup
    {
        public Startup()
        {
            var config = new ConfigurationBuilder()
                .AddEnvironmentVariables();

            Configuration = config.Build();
        }

        public IConfiguration Configuration { get; }

        public override void Configure(IFunctionsHostBuilder builder)
        {
            builder.Services.AddHttpClient();
            builder.Services.AddHttpClient("InSecure")
                   .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                   {
                       ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                   });

            builder.Services.AddSingleton(new LookupClient(new LookupClientOptions { UseCache = false }));

            builder.Services.AddSingleton(provider =>
            {
                var options = provider.GetRequiredService<IOptions<LetsEncryptOptions>>();

                return new WebSiteManagementClient(new TokenCredentials(new AppAuthenticationTokenProvider()))
                {
                    SubscriptionId = options.Value.SubscriptionId
                };
            });

            builder.Services.AddSingleton(provider =>
            {
                var options = provider.GetRequiredService<IOptions<LetsEncryptOptions>>();

                return new DnsManagementClient(new TokenCredentials(new AppAuthenticationTokenProvider()))
                {
                    SubscriptionId = options.Value.SubscriptionId
                };
            });

            builder.Services.AddSingleton<IAcmeProtocolClientFactory, AcmeProtocolClientFactory>();
            builder.Services.AddSingleton<IKuduApiClientFactory, KuduApiClientFactory>();
            builder.Services.AddSingleton<ILifeCycleNotificationHelper, WebhookLifeCycleNotification>();

            builder.Services.Configure<LetsEncryptOptions>(Configuration.GetSection("LetsEncrypt"));
        }
    }
}
