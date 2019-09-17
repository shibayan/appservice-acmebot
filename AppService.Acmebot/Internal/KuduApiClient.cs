using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace AppService.Acmebot.Internal
{
    public class KuduApiClient
    {
        public KuduApiClient(HttpClient httpClient, string scmUrl, string userName, string password)
        {
            _httpClient = httpClient;
            _scmUrl = scmUrl;
            _basicAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{password}"));
        }

        private readonly HttpClient _httpClient;
        private readonly string _scmUrl;
        private readonly string _basicAuth;

        public Task WriteFileAsync(string filePath, string value)
        {
            var request = new HttpRequestMessage(HttpMethod.Put, $"https://{_scmUrl}/api/vfs/site/{filePath}");

            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _basicAuth);
            request.Content = new StringContent(value, Encoding.UTF8);

            return _httpClient.SendAsync(request);
        }
    }
}
