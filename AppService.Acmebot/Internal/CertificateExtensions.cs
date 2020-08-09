using Microsoft.Azure.Management.WebSites.Models;

namespace AppService.Acmebot.Internal
{
    internal static class CertificateExtensions
    {
        public static bool TagsFilter(this Certificate certificate, string issuer, string endpoint)
        {
            var tags = certificate.Tags;

            if (tags == null)
            {
                return false;
            }

            if (tags.TryGetValue("Issuer", out var tagIssuer) && tagIssuer == issuer)
            {
                return true;
            }

            if (tags.TryGetValue("Endpoint", out var tagEndpoint) && tagEndpoint == endpoint)
            {
                return true;
            }

            return false;
        }
    }
}
