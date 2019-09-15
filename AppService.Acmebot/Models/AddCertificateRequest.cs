namespace AppService.Acmebot.Models
{
    public class AddCertificateRequest
    {
        public string ResourceGroupName { get; set; }
        public string SiteName { get; set; }
        public string SlotName { get; set; }
        public string[] Domains { get; set; }
        public bool? UseIpBasedSsl { get; set; }
    }
}