using Newtonsoft.Json;

namespace VenmoForSlack.Venmo.Models
{
    public class VenmoPayment
    {
        [JsonProperty("status")]
        public string? Status { get; set; }

        // [JsonProperty("refund")]

        [JsonProperty("medium")]
        public string? Medium { get; set; }

        [JsonProperty("id")]
        public string? Id { get; set; }

        // [JsonProperty("date_authorized")]

        // [JsonProperty("fee")]

        [JsonProperty("date_completed")]
        public string? DateCompleted { get; set; }

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

        [JsonProperty("data_created")]
        public string? DateCreated { get; set; }

        [JsonProperty("date_reminded")]
        public string? DateReminded { get; set; }
    }
}
