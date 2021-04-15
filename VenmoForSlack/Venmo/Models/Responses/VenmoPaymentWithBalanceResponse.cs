using Newtonsoft.Json;

namespace VenmoForSlack.Venmo.Models.Responses
{
    public class VenmoPaymentWithBalanceResponse
    {
        [JsonProperty("data")]
        public VenmoPaymentWithBalance? Data { get; set; }

        public string? Error { get; set; }
    }
}
