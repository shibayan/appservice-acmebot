using System;
using System.Linq;
using System.Net.Http;

using AppService.Acmebot.Internal;
using AppService.Acmebot.Options;

using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.AppService;
using Azure.Security.KeyVault.Certificates;
using Azure.Storage.Blobs;

using DnsClient;

using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

[assembly: FunctionsStartup(typeof(AppService.Acmebot.Startup))]

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

        // Isolate the Durable Functions task hub per deployment slot, so slots sharing a
        // storage account never execute each other's orchestrations and activities.
        // WEBSITE_SLOT_NAME is provided by App Service ("production" on the production slot);
        // outside App Service (e.g. local development) it falls back to "default".
        // Task hub names allow only letters and digits, so slot names are sanitized.
        builder.Services.Configure<DurableTaskOptions>(options =>
        {
            var slotName = Environment.GetEnvironmentVariable("WEBSITE_SLOT_NAME") ?? "default";

            var hubName = $"Acmebot{string.Concat(slotName.Where(char.IsLetterOrDigit))}";

            options.HubName = hubName.Length > 45 ? hubName[..45] : hubName;
        });

        // Add Services
        builder.Services.Replace(ServiceDescriptor.Transient(typeof(IOptionsFactory<>), typeof(OptionsFactory<>)));

        builder.Services.AddHttpClient();
        builder.Services.AddHttpClient("InSecure")
               .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
               {
                   ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
               });

        builder.Services.AddSingleton<ITelemetryInitializer, ApplicationVersionInitializer>();

        builder.Services.AddSingleton(new LookupClient(new LookupClientOptions(NameServer.GooglePublicDns, NameServer.GooglePublicDns2)
        {
            UseCache = false,
            UseRandomNameServer = true
        }));

        builder.Services.AddSingleton<TokenCredential>(provider =>
        {
            var environment = provider.GetRequiredService<AzureEnvironment>();

            return new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                AuthorityHost = environment.AuthorityHost
            });
        });

        builder.Services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<AcmebotOptions>>();

            return AzureEnvironment.Get(options.Value.Environment);
        });

        builder.Services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<AcmebotOptions>>();
            var environment = provider.GetRequiredService<AzureEnvironment>();
            var credential = provider.GetRequiredService<TokenCredential>();

            var armClientOptions = new ArmClientOptions { Environment = environment.ResourceManager };

            // The subscription-level Microsoft.Web/certificates list returns only empty pages on
            // api-versions newer than 2022-03-01 (observed 2026-08-05 in this subscription), which
            // makes GetExpiringCertificates/GetAllCertificates come back empty and renewals no-op.
            armClientOptions.SetApiVersion(AppCertificateResource.ResourceType, "2022-03-01");

            return new ArmClient(credential, options.Value.SubscriptionId, armClientOptions);
        });

        builder.Services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<AcmebotOptions>>();
            var credential = provider.GetRequiredService<TokenCredential>();

            return new CertificateClient(new Uri(options.Value.VaultBaseUrl), credential);
        });

        builder.Services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<AcmebotOptions>>();

            return new BlobContainerClient(new Uri(options.Value.StorageSasUri, UriKind.Absolute));
        });

        builder.Services.AddSingleton<AcmeProtocolClientFactory>();
        builder.Services.AddSingleton<KuduClientFactory>();

        builder.Services.AddSingleton<WebhookInvoker>();
        builder.Services.AddSingleton<ILifeCycleNotificationHelper, WebhookLifeCycleNotification>();
    }
}
