namespace AppService.Acmebot.Models
{
    public class AcmeChallengeResult
    {
        public string Url { get; set; }
        public string HttpResourceUrl { get; set; }
        public string HttpResourcePath { get; set; }
        public string HttpResourceValue { get; set; }
        public string DnsRecordName { get; set; }
        public string DnsRecordValue { get; set; }
    }
}
