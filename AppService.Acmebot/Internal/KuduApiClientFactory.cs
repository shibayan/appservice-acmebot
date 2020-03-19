using System.Net.Http;

namespace AppService.Acmebot.Internal
{
    public interface IKuduApiClientFactory
    {
        KuduApiClient CreateClient(string scmUrl, string userName, string password);
    }

    internal class KuduApiClientFactory : IKuduApiClientFactory
    {
        public KuduApiClientFactory(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        private readonly IHttpClientFactory _httpClientFactory;

        public KuduApiClient CreateClient(string scmUrl, string userName, string password)
        {
            var httpClient = _httpClientFactory.CreateClient();

            return new KuduApiClient(httpClient, scmUrl, userName, password);
        }
    }
}
