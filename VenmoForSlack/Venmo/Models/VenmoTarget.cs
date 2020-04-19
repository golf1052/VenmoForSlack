using Newtonsoft.Json;

namespace VenmoForSlack.Venmo.Models
{
    public class VenmoTarget
    {
        [JsonProperty("merchant")]
        public string? Merchant { get; set; }

        [JsonProperty("redeemable_target")]
        public string? RedeemableTarget { get; set; }

        [JsonProperty("phone")]
        public string? Phone { get; set; }

        [JsonProperty("user")]
        public VenmoUser User { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("email")]
        public string? Email { get; set; }
    }
}
