using Newtonsoft.Json;

namespace VenmoForSlack.Venmo.Models
{
    public class VenmoTransferDestination
    {
        [JsonProperty("transfer_to_estimate")]
        public string? TransferToEstimate { get; set; }

        [JsonProperty("is_default")]
        public bool IsDefault { get; set; }

        [JsonProperty("last_four")]
        public string? LastFour { get; set; }

        [JsonProperty("account_status")]
        public string? AccountStatus { get; set; }

        [JsonProperty("id")]
        public string? Id { get; set; }

        //[JsonProperty("bank_account")]

        [JsonProperty("assets")]
        public Image? Assets { get; set; }

        [JsonProperty("asset_name")]
        public string? AssetName { get; set; }

        [JsonProperty("name")]
        public string? Name { get; set; }

        [JsonProperty("image_url")]
        public Image? ImageUrl { get; set; }

        //[JsonProperty("card")]

        [JsonProperty("type")]
        public string? Type { get; set; }

        public class Image
        {
            [JsonProperty("detail")]
            public string? Detail { get; set; }

            [JsonProperty("thumbnail")]
            public string? Thumbnail { get; set; }
        }
    }
}
