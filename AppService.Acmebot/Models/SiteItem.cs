using System.Collections.Generic;

using Newtonsoft.Json;

namespace AppService.Acmebot.Models
{
    public class SiteItem
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("slots")]
        public IList<SlotItem> Slots { get; set; }
    }
}
