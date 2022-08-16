using Newtonsoft.Json;

namespace VenmoForSlack.Venmo.Models.Responses
{
    public class VenmoErrorObject
    {
        [JsonProperty("url")]
        public string? Url { get; set; }

        [JsonProperty("message")]
        public string? Message { get; set; }

        [JsonProperty("code")]
        public int? Code { get; set; }

        [JsonProperty("title")]
        public string? Title { get; set; }
    }
}
