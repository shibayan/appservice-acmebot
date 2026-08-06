using System;
using System.Net.Http;
using System.Text.Json;

using AppService.Acmebot.Internal;
using AppService.Acmebot.Options;

using Azure.Core;
using Azure.Core.Serialization;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.AppService;
using Azure.Security.KeyVault.Certificates;
using Azure.Storage.Blobs;

using DnsClient;

using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// Add Options
var section = builder.Configuration.GetSection("Acmebot");

builder.Services.AddOptions<AcmebotOptions>()
       .Bind(section.Exists() ? section : builder.Configuration.GetSection("LetsEncrypt"))
       .ValidateDataAnnotations();

// Worker serialization: orchestration/activity inputs and outputs use ValueTuples,
// whose members are fields - System.Text.Json skips them unless IncludeFields is set
// (without it, activity tuple inputs silently deserialize to nulls). The Durable Task
// extension derives its data converter from this worker serializer, so configuring it
// here covers orchestrations, activities, and the durable client alike.
// CamelCase matches the Newtonsoft-era [JsonProperty] names on the models, which the
// add-certificate UI reads from the orchestrator outputs passed through over HTTP.
var serializerOptions = new JsonSerializerOptions
{
    IncludeFields = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
};

// Serialization must be configured on TWO independent surfaces, or ValueTuple
// inputs are written correctly but silently read back as nulls somewhere:
//
// 1. WorkerOptions.Serializer - used for activity/trigger binding, and the Durable
//    extension's PostConfigure derives its DataConverter from the WorkerOptions
//    instance matching its own (named) options instance, so ConfigureAll is required.
//    Configuring DurableTaskWorkerOptions.DataConverter directly is futile - the
//    extension's PostConfigure overwrites it unconditionally.
builder.Services.ConfigureAll<WorkerOptions>(options => options.Serializer = new JsonObjectSerializer(serializerOptions));

// 2. IOptions<JsonSerializerOptions> - FunctionsOrchestrationContext.GetInput uses
//    this raw DI options instance (NOT the data converter) when the orchestration
//    input arrives as a JsonElement, e.g. for sub-orchestration inputs.
builder.Services.Configure<JsonSerializerOptions>(options =>
{
    options.IncludeFields = serializerOptions.IncludeFields;
    options.PropertyNamingPolicy = serializerOptions.PropertyNamingPolicy;
});

// Add Services
builder.Services.AddApplicationInsightsTelemetryWorkerService();
builder.Services.ConfigureFunctionsApplicationInsights();

builder.Services.AddSingleton<ITelemetryInitializer, ApplicationVersionInitializer>();

builder.Services.AddHttpClient();
builder.Services.AddHttpClient("InSecure")
       .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
       {
           ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
       });

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

builder.Build().Run();
