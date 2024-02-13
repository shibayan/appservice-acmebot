using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

using Azure.Core;

namespace AppService.Acmebot.Internal;

public class KuduClientFactory
{
    public KuduClientFactory(IHttpClientFactory httpClientFactory, TokenCredential tokenCredential, AzureEnvironment environment)
    {
        _httpClientFactory = httpClientFactory;
        _tokenCredential = tokenCredential;
        _environment = environment;
    }

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TokenCredential _tokenCredential;
    private readonly AzureEnvironment _environment;

    public async Task<KuduClient> CreateClientAsync(string scmHost)
    {
        var httpClient = _httpClientFactory.CreateClient();

        var context = new TokenRequestContext(new[] { _environment.ResourceManager.DefaultScope }, null);
        var accessToken = await _tokenCredential.GetTokenAsync(context, CancellationToken.None);

        httpClient.BaseAddress = new Uri($"https://{scmHost}");
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);

        return new KuduClient(httpClient);
    }
}
