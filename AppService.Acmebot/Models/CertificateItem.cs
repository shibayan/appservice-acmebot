using System;
using System.Collections.Generic;
using System.Linq;

using Azure.ResourceManager.AppService;

namespace AppService.Acmebot.Models;

public class CertificateItem
{
    public string Id { get; set; }

    public string SubjectName { get; set; }

    public string Thumbprint { get; set; }

    public string Issuer { get; set; }

    public DateTimeOffset ExpirationOn { get; set; }

    public IReadOnlyList<string> HostNames { get; set; }

    public IDictionary<string, string> Tags { get; set; }

    public static CertificateItem Create(AppCertificateData certificateData)
    {
        return new CertificateItem
        {
            Id = certificateData.Id,
            ExpirationOn = certificateData.ExpireOn.Value,
            HostNames = certificateData.HostNames.ToArray(),
            Issuer = certificateData.Issuer,
            SubjectName = certificateData.SubjectName,
            Tags = certificateData.Tags,
            Thumbprint = certificateData.Thumbprint.ToString()
        };
    }
}
