using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace AppService.Acmebot.Models;

public class AddCertificateRequest : IValidatableObject
{
    [Required]
    public string ResourceGroupName { get; set; }

    [Required]
    public string WebSiteName { get; set; }

    [Required]
    public string SlotName { get; set; }

    public string[] DnsNames { get; set; }

    public bool? UseIpBasedSsl { get; set; }

    public bool? ForceDns01Challenge { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (DnsNames is null || DnsNames.Length == 0)
        {
            yield return new ValidationResult($"The {nameof(DnsNames)} is required.", new[] { nameof(DnsNames) });
        }
    }
}
