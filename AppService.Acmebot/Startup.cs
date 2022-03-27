using System;
using System.Net.Http;

using AppService.Acmebot;
using AppService.Acmebot.Internal;
using AppService.Acmebot.Options;

using DnsClient;

using Microsoft.ApplicationInsights.Extensibility;
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

namespace AppService.Acmebot;

public class Startup : FunctionsStartup
{
    public override void Configure(IFunctionsHostBuilder builder)
    {
        // Add Options
        var context = builder.GetContext();

        var section = context.Configuration.GetSection("Acmebot");

        builder.Services.AddOptions<AcmebotOptions>()
               .Bind(section.Exists() ? section : context.Configuration.GetSection("LetsEncrypt"))
               .ValidateDataAnnotations();

        // Add Services
        builder.Services.Replace(ServiceDescriptor.Transient(typeof(IOptionsFactory<>), typeof(OptionsFactory<>)));

        builder.Services.AddHttpClient();
        builder.Services.AddHttpClient("InSecure")
               .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
               {
                   ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
               });

        builder.Services.AddSingleton<ITelemetryInitializer, ApplicationVersionInitializer<Startup>>();

        builder.Services.AddSingleton(new LookupClient(new LookupClientOptions(NameServer.GooglePublicDns, NameServer.GooglePublicDns2)
        {
            UseCache = false,
            UseRandomNameServer = true
        }));

        builder.Services.AddSingleton<ITokenProvider, AppAuthenticationTokenProvider>();

        builder.Services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<AcmebotOptions>>();

            return AzureEnvironment.Get(options.Value.Environment);
        });

        builder.Services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<AcmebotOptions>>();
            var environment = provider.GetRequiredService<AzureEnvironment>();

            return new WebSiteManagementClient(new Uri(environment.ResourceManager), new TokenCredentials(provider.GetRequiredService<ITokenProvider>()))
            {
                SubscriptionId = options.Value.SubscriptionId
            };
        });

        builder.Services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<AcmebotOptions>>();
            var environment = provider.GetRequiredService<AzureEnvironment>();

            return new DnsManagementClient(new Uri(environment.ResourceManager), new TokenCredentials(provider.GetRequiredService<ITokenProvider>()))
            {
                SubscriptionId = options.Value.SubscriptionId
            };
        });

        builder.Services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<AcmebotOptions>>();
            var environment = provider.GetRequiredService<AzureEnvironment>();

            return new ResourceManagementClient(new Uri(environment.ResourceManager), new TokenCredentials(provider.GetRequiredService<ITokenProvider>()))
            {
                SubscriptionId = options.Value.SubscriptionId
            };
        });

        builder.Services.AddSingleton<AcmeProtocolClientFactory>();
        builder.Services.AddSingleton<KuduClientFactory>();

        builder.Services.AddSingleton<WebhookInvoker>();
        builder.Services.AddSingleton<ILifeCycleNotificationHelper, WebhookLifeCycleNotification>();
    }
}
