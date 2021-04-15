using Newtonsoft.Json;

namespace VenmoForSlack.Venmo.Models
{
    public class VenmoPaymentResponse
    {
        [JsonProperty("data")]
        public VenmoPayment? Data { get; set; }
    }
}
