using System.Collections.Generic;
using Newtonsoft.Json;

namespace VenmoForSlack.Venmo.Models.Responses
{
    public class VenmoPaymentPendingResponse
    {
        [JsonProperty("pagination")]
        public PaginationObject? Pagination { get; set; }

        [JsonProperty("data")]
        public List<VenmoPaymentPending>? Data { get; set; }
    }
}
