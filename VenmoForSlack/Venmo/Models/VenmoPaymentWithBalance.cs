using Newtonsoft.Json;

namespace VenmoForSlack.Venmo.Models
{
    public class VenmoPaymentWithBalance
    {
        [JsonProperty("balance")]
        public string? Balance { get; set; }

        [JsonProperty("payment")]
        public VenmoPayment? Payment { get; set; }

        // [JsonProperty("payment_token")]
    }
}
