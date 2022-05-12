using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace AppService.Acmebot.Internal;

public class KuduClient
{
    public KuduClient(HttpClient httpClient, Uri scmUri)
    {
        _httpClient = httpClient;
        _scmHost = scmUri.Host;
        _basicAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes(scmUri.UserInfo));
    }

    private readonly HttpClient _httpClient;
    private readonly string _scmHost;
    private readonly string _basicAuth;

    public async Task<bool> ExistsFileAsync(string filePath)
    {
        var request = new HttpRequestMessage(HttpMethod.Head, $"https://{_scmHost}/api/vfs/site/{filePath}");

        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _basicAuth);

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

    public Task WriteFileAsync(string filePath, string content)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"https://{_scmHost}/api/vfs/site/{filePath}");

        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _basicAuth);
        request.Headers.IfMatch.Add(EntityTagHeaderValue.Any);

        request.Content = new StringContent(content, Encoding.UTF8);

        return _httpClient.SendAsync(request);
    }
}
