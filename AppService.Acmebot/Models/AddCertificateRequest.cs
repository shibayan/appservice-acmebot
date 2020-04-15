using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace AppService.Acmebot.Models
{
    public class AddCertificateRequest : IValidatableObject
    {
        [Required]
        public string ResourceGroupName { get; set; }

        [Required]
        public string AppName { get; set; }

        [Required]
        public string SlotName { get; set; }

        public string[] Domains { get; set; }

        public bool? UseIpBasedSsl { get; set; }

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (Domains == null || Domains.Length == 0)
            {
                yield return new ValidationResult($"The {nameof(Domains)} is required.", new[] { nameof(Domains) });
            }
        }
    }
}
