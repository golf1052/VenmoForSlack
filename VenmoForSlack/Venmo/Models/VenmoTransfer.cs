using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace VenmoForSlack.Venmo.Models
{
    public class VenmoTransfer
    {
        [JsonProperty("status")]
        public string? Status { get; set; }

        [JsonProperty("amount")]
        public double Amount { get; set; }

        [JsonProperty("date_requested")]
        public string? DateRequested { get; set; }

        [JsonProperty("amount_cents")]
        public int AmountCents { get; set; }

        [JsonProperty("amount_fee_cents")]
        public int AmountFeeCents { get; set; }

        [JsonProperty("destination")]
        public VenmoTransferDestination? Destination { get; set; }

        [JsonProperty("amount_requested_cents")]
        public int AmountRequestedCents { get; set; }

        [JsonProperty("type")]
        public string? Type { get; set; }
    }
}
