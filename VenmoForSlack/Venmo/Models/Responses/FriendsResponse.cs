using System.Collections.Generic;
using Newtonsoft.Json;

namespace VenmoForSlack.Venmo.Models.Responses
{
    public class FriendsResponse
    {
        [JsonProperty("pagination")]
        public PaginationObject? Pagination { get; set; }
        
        [JsonProperty("data")]
        public List<VenmoUser>? Data { get; set; }
    }
}
