namespace AppService.Acmebot.Models;

/// <summary>
/// Per-certificate outcome of a renewal run, collected by the fan-out and
/// aggregated into the nightly <see cref="RenewalRunSummary"/> digest.
/// </summary>
public class CertificateRenewalResult
{
    public const string Renewed = "renewed";
    public const string Skipped = "skipped";
    public const string Failed = "failed";

    public string DnsNames { get; set; }

    public string Outcome { get; set; }

    public string Detail { get; set; }
}
