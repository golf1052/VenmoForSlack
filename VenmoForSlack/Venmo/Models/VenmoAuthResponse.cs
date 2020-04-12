using System;
using Newtonsoft.Json;

namespace VenmoForSlack.Venmo.Models
{
    public class VenmoAuthResponse
    {
        [JsonProperty("access_token")]
        public string AccessToken { get; set; }

        [JsonProperty("expires_in")]
        public long ExpiresIn { get; set; }

        [JsonProperty("refresh_token")]
        public string RefreshToken { get; set; }

        [JsonProperty("user")]
        public VenmoUser User { get; set; }

        [JsonProperty("balance")]
        public string Balance { get; set; }
    }
}
