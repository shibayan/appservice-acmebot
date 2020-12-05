using Newtonsoft.Json;

namespace AppService.Acmebot.Models
{
    public class ResourceGroupItem
    {
        [JsonProperty("name")]
        public string Name { get; set; }
    }
}
