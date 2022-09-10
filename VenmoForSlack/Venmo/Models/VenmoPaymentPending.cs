using Newtonsoft.Json;

namespace VenmoForSlack.Venmo.Models
{
    public class VenmoPaymentPending
    {
        [JsonProperty("status")]
        public string? Status { get; set; }

        [JsonProperty("id")]
        public string? Id { get; set; }

        // [JsonProperty("date_authorized")]

        // [JsonProperty("date_completed")]

        [JsonProperty("target")]
        public VenmoTarget? Target { get; set; }

        [JsonProperty("audience")]
        public string? Audience { get; set; }

        [JsonProperty("actor")]
        public VenmoUser? Actor { get; set; }

        [JsonProperty("note")]
        public string? Note { get; set; }

        [JsonProperty("amount")]
        public double Amount { get; set; }

        [JsonProperty("action")]
        public string? Action { get; set; }

        [JsonProperty("date_created")]
        public string? DateCreated { get; set; }

        [JsonProperty("date_reminded")]
        public string? DateReminded { get; set; }
    }
}
