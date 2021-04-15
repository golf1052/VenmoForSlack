using Newtonsoft.Json;

namespace VenmoForSlack.Venmo.Models.Responses
{
    public class MeResponse
    {
        [JsonProperty("data")]
        public MeResponseData? Data { get; set; }
    }
}
