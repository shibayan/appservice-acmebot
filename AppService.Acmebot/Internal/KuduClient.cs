using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace AppService.Acmebot.Internal;

public class KuduClient
{
    public KuduClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    private readonly HttpClient _httpClient;

    public async Task<bool> ExistsFileAsync(string filePath)
    {
        var request = new HttpRequestMessage(HttpMethod.Head, $"/api/vfs/site/{filePath}");

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
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/vfs/site/{filePath}");

        request.Headers.IfMatch.Add(EntityTagHeaderValue.Any);

        request.Content = new StringContent(content, Encoding.UTF8);

        await _httpClient.SendAsync(request);
    }
}
