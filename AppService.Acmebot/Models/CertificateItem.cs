using System;
using System.Collections.Generic;

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
}
