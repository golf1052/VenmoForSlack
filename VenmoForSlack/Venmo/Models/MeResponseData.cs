using Newtonsoft.Json;

namespace VenmoForSlack.Venmo.Models
{
    public class MeResponseData
    {
        [JsonProperty("user")]
        public VenmoUser User { get; set; }

        [JsonProperty("balance")]
        public string Balance { get; set; }
    }
}
