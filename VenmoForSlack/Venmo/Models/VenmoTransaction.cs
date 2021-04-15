using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace VenmoForSlack.Venmo.Models
{
    public class VenmoTransaction
    {
        [JsonProperty("date_updated")]
        public string? DateUpdated { get; set; }

        [JsonProperty("transfer")]
        public VenmoTransfer? Transfer { get; set; }

        [JsonProperty("app")]
        public VenmoApp? App { get; set; }

        //[JsonProperty("comments")]

        [JsonProperty("payment")]
        public VenmoPayment? Payment { get; set; }

        [JsonProperty("note")]
        public string? Note { get; set; }

        [JsonProperty("audience")]
        public string? Audience { get; set; }

        //[JsonProperty("likes")]

        //[JsonProperty("mentions")]

        [JsonProperty("date_created")]
        public string? DateCreated { get; set; }

        [JsonProperty("type")]
        public string? Type { get; set; }

        [JsonProperty("id")]
        public string? Id { get; set; }

        //[JsonProperty("authorization")]

        public class VenmoApp
        {
            [JsonProperty("description")]
            public string? Description { get; set; }

            [JsonProperty("site_url")]
            public string? SiteUrl { get; set; }

            [JsonProperty("image_url")]
            public string? ImageUrl { get; set; }

            [JsonProperty("id")]
            public int Id { get; set; }

            [JsonProperty("name")]
            public string? Name { get; set; }
        }
    }
}
