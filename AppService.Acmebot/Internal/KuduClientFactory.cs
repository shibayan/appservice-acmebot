using System.Net.Http;

namespace AppService.Acmebot.Internal
{
    public interface IKuduClientFactory
    {
        KuduClient CreateClient(string scmUrl, string userName, string password);
    }

    internal class KuduClientFactory : IKuduClientFactory
    {
        public KuduClientFactory(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        private readonly IHttpClientFactory _httpClientFactory;

        public KuduClient CreateClient(string scmUrl, string userName, string password)
        {
            var httpClient = _httpClientFactory.CreateClient();

            return new KuduClient(httpClient, scmUrl, userName, password);
        }
    }
}
