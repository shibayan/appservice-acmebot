using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Azure.Core;

namespace AppService.Acmebot.Internal;

public class KuduClient
{
    public KuduClient(HttpClient httpClient, Uri scmUri, TokenCredential tokenCredential)
    {
        _httpClient = httpClient;
        _scmHost = scmUri.Host;
        _tokenCredential = tokenCredential;
    }

    private readonly HttpClient _httpClient;
    private readonly string _scmHost;
    private readonly TokenCredential _tokenCredential;

    public async Task<bool> ExistsFileAsync(string filePath)
    {
        var accessToken = await _tokenCredential.GetTokenAsync(new TokenRequestContext(), CancellationToken.None);

        var request = new HttpRequestMessage(HttpMethod.Head, $"https://{_scmHost}/api/vfs/site/{filePath}");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);

        var response = await _httpClient.SendAsync(request);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            return true;
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();

        return false;
    }

    public async Task WriteFileAsync(string filePath, string content)
    {
        var accessToken = await _tokenCredential.GetTokenAsync(new TokenRequestContext(), CancellationToken.None);

        var request = new HttpRequestMessage(HttpMethod.Put, $"https://{_scmHost}/api/vfs/site/{filePath}");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        request.Headers.IfMatch.Add(EntityTagHeaderValue.Any);

        request.Content = new StringContent(content, Encoding.UTF8);

        await _httpClient.SendAsync(request);
    }
}
