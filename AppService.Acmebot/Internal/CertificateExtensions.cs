using System;
using System.Collections.Generic;

using Azure.ResourceManager.AppService;

namespace AppService.Acmebot.Internal;

internal static class CertificateExtensions
{
    public static bool IsIssuedByAcmebot(this AppCertificateData certificateData)
    {
        return certificateData.Tags.TryGetIssuer(out var tagIssuer) && tagIssuer == IssuerValue;
    }

    public static bool IsSameEndpoint(this AppCertificateData certificateData, Uri endpoint)
    {
        return certificateData.Tags.TryGetEndpoint(out var tagEndpoint) && NormalizeEndpoint(tagEndpoint) == endpoint.Host;
    }

    private const string IssuerKey = "Issuer";
    private const string EndpointKey = "Endpoint";

    private const string IssuerValue = "Acmebot";

    private static bool TryGetIssuer(this IDictionary<string, string> tags, out string issuer) => tags.TryGetValue(IssuerKey, out issuer);

    private static bool TryGetEndpoint(this IDictionary<string, string> tags, out string endpoint) => tags.TryGetValue(EndpointKey, out endpoint);

    private static string NormalizeEndpoint(string endpoint) => Uri.TryCreate(endpoint, UriKind.Absolute, out var legacyEndpoint) ? legacyEndpoint.Host : endpoint;
}
