using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace AppService.Acmebot.Internal
{
    public class KuduClient
    {
        public KuduClient(HttpClient httpClient, string scmUrl, string userName, string password)
        {
            _httpClient = httpClient;
            _scmUrl = scmUrl;
            _basicAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{password}"));
        }

        private readonly HttpClient _httpClient;
        private readonly string _scmUrl;
        private readonly string _basicAuth;

        public async Task<bool> ExistsFileAsync(string filePath)
        {
            var request = new HttpRequestMessage(HttpMethod.Head, $"https://{_scmUrl}/api/vfs/site/{filePath}");

            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _basicAuth);

            var response = await _httpClient.SendAsync(request);

            return response.StatusCode == HttpStatusCode.OK;
        }

        public Task WriteFileAsync(string filePath, string content)
        {
            var request = new HttpRequestMessage(HttpMethod.Put, $"https://{_scmUrl}/api/vfs/site/{filePath}");

            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _basicAuth);
            request.Headers.IfMatch.Add(EntityTagHeaderValue.Any);

            request.Content = new StringContent(content, Encoding.UTF8);

            return _httpClient.SendAsync(request);
        }
    }
}
