using Newtonsoft.Json;

namespace VenmoForSlack.Venmo.Models
{
    public class MeResponse
    {
        [JsonProperty("data")]
        public MeResponseData Data { get; set; }
    }
}
