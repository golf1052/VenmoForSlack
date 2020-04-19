using Newtonsoft.Json;

namespace VenmoForSlack.Venmo.Models
{
    public class PaginationObject
    {
        [JsonProperty("previous")]
        public string? Previous { get; set; }

        [JsonProperty("next")]
        public string? Next { get; set; }
    }
}
