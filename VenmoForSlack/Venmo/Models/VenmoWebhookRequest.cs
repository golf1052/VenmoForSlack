using Newtonsoft.Json;

namespace VenmoForSlack.Venmo.Models
{
    public class VenmoWebhookRequest
    {
        [JsonProperty("date_created")]
        public string DateCreated { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("data")]
        public VenmoPaymentPending Data { get; set; }
    }
}
