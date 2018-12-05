using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace AzureAppService.LetsEncrypt.Internal
{
    internal class KuduApiClient
    {
        public KuduApiClient(string scmUrl, string userName, string password)
        {
            _scmUrl = scmUrl;
            _basicAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{password}"));
        }

        private readonly string _scmUrl;
        private readonly string _basicAuth;

        private static readonly HttpClient _httpClient = new HttpClient();

        public Task WriteFileAsync(string filePath, string value)
        {
            var request = new HttpRequestMessage(HttpMethod.Put, $"https://{_scmUrl}/api/vfs/site/{filePath}");

            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _basicAuth);
            request.Content = new StringContent(value, Encoding.UTF8);

            return _httpClient.SendAsync(request);
        }
    }
}
