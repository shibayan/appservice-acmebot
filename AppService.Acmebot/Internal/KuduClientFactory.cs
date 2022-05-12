using System;
using System.Net.Http;

namespace AppService.Acmebot.Internal;

public class KuduClientFactory
{
    public KuduClientFactory(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    private readonly IHttpClientFactory _httpClientFactory;

    public KuduClient CreateClient(Uri scmUri)
    {
        var httpClient = _httpClientFactory.CreateClient();

        return new KuduClient(httpClient, scmUri);
    }
}
