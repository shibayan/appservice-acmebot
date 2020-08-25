using System;
using System.Net.Http;

using AppService.Acmebot;
using AppService.Acmebot.Internal;
using AppService.Acmebot.Options;

using DnsClient;

using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.Azure.Management.Dns;
using Microsoft.Azure.Management.ResourceManager;
using Microsoft.Azure.Management.WebSites;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Rest;

[assembly: FunctionsStartup(typeof(Startup))]

namespace AppService.Acmebot
{
    public class Startup : FunctionsStartup
    {
        public override void Configure(IFunctionsHostBuilder builder)
        {
            builder.Services.Replace(ServiceDescriptor.Transient(typeof(IOptionsFactory<>), typeof(OptionsFactory<>)));

            builder.Services.AddHttpClient();
            builder.Services.AddHttpClient("InSecure")
                   .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                   {
                       ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                   });

            builder.Services.AddSingleton(new LookupClient(new LookupClientOptions { UseCache = false }));

            builder.Services.AddSingleton<ITokenProvider, AppAuthenticationTokenProvider>();

            builder.Services.AddSingleton<IAzureEnvironment>(provider =>
            {
                var options = provider.GetRequiredService<IOptions<AcmebotOptions>>();

                return AzureEnvironment.Get(options.Value.Environment);
            });

            builder.Services.AddSingleton(provider =>
            {
                var options = provider.GetRequiredService<IOptions<AcmebotOptions>>();
                var environment = provider.GetRequiredService<IAzureEnvironment>();

                return new WebSiteManagementClient(new Uri(environment.ResourceManager), new TokenCredentials(provider.GetRequiredService<ITokenProvider>()))
                {
                    SubscriptionId = options.Value.SubscriptionId
                };
            });

            builder.Services.AddSingleton(provider =>
            {
                var options = provider.GetRequiredService<IOptions<AcmebotOptions>>();
                var environment = provider.GetRequiredService<IAzureEnvironment>();

                return new DnsManagementClient(new Uri(environment.ResourceManager), new TokenCredentials(provider.GetRequiredService<ITokenProvider>()))
                {
                    SubscriptionId = options.Value.SubscriptionId
                };
            });

            builder.Services.AddSingleton(provider =>
            {
                var options = provider.GetRequiredService<IOptions<AcmebotOptions>>();
                var environment = provider.GetRequiredService<IAzureEnvironment>();

                return new ResourceManagementClient(new Uri(environment.ResourceManager), new TokenCredentials(provider.GetRequiredService<ITokenProvider>()))
                {
                    SubscriptionId = options.Value.SubscriptionId
                };
            });

            builder.Services.AddSingleton<IAcmeProtocolClientFactory, AcmeProtocolClientFactory>();
            builder.Services.AddSingleton<IKuduClientFactory, KuduClientFactory>();

            builder.Services.AddSingleton<WebhookClient>();
            builder.Services.AddSingleton<ILifeCycleNotificationHelper, WebhookLifeCycleNotification>();

            var context = builder.GetContext();

            var section = context.Configuration.GetSection("Acmebot");

            builder.Services.AddOptions<AcmebotOptions>()
                   .Bind(section.Exists() ? section : context.Configuration.GetSection("LetsEncrypt"))
                   .ValidateDataAnnotations();
        }
    }
}
