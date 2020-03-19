using System.Collections.Generic;

using Newtonsoft.Json;

namespace AppService.Acmebot.Models
{
    public class ResourceGroupInformation
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("sites")]
        public IList<SiteInformation> Sites { get; set; }
    }

    public class SiteInformation
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("slots")]
        public IList<SlotInformation> Slots { get; set; }
    }

    public class SlotInformation
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("domains")]
        public IList<DomainInformation> Domains { get; set; }
    }

    public class DomainInformation
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("issuer")]
        public string Issuer { get; set; }
    }
}
