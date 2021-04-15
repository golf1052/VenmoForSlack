﻿using System.Collections.Generic;
using Newtonsoft.Json;

namespace VenmoForSlack.Venmo.Models.Responses
{
    public class VenmoTransactionResponse
    {
        [JsonProperty("pagination")]
        public PaginationObject? Pagination { get; set; }

        [JsonProperty("data")]
        public List<VenmoTransaction>? Data { get; set; }
    }
}
