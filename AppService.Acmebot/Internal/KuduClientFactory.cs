using System;
using System.Net.Http;

using Azure.Core;

namespace AppService.Acmebot.Internal;

public class KuduClientFactory
{
    public KuduClientFactory(IHttpClientFactory httpClientFactory, TokenCredential tokenCredential)
    {
        _httpClientFactory = httpClientFactory;
        _tokenCredential = tokenCredential;
    }

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TokenCredential _tokenCredential;

    public KuduClient CreateClient(Uri scmUri)
    {
        var httpClient = _httpClientFactory.CreateClient();

        return new KuduClient(httpClient, scmUri, _tokenCredential);
    }
}
