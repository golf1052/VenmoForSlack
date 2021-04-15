using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace VenmoForSlack.Venmo.Models.Responses
{
    public class VenmoUserSearchResponse
    {
        [JsonProperty("pagination")]
        public PaginationObject? Pagination { get; set; }

        [JsonProperty("data")]
        public List<VenmoUser>? Data { get; set; }
    }
}
