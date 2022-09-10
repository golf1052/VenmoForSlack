using System;
using Newtonsoft.Json;

namespace VenmoForSlack.Venmo.Models
{
    public class VenmoUser
    {
        [JsonProperty("id")]
        public string? Id { get; set; }

        [JsonProperty("username")]
        public string? Username { get; set; }

        [JsonProperty("first_name")]
        public string? FirstName { get; set; }

        [JsonProperty("last_name")]
        public string? LastName { get; set; }

        [JsonProperty("friends_count")]
        public int? FriendsCount { get; set; }

        [JsonProperty("is_group")]
        public bool IsGroup { get; set; }

        [JsonProperty("is_active")]
        public bool IsActive { get; set; }

        // [JsonProperty("trust_request")]

        [JsonProperty("phone")]
        public string? Phone { get; set; }

        [JsonProperty("profile_picture_url")]
        public string? ProfilePictureUrl { get; set; }

        [JsonProperty("is_blocked")]
        public bool IsBlocked { get; set; }

        // [JsonProperty("identity")]

        [JsonProperty("date_joined")]
        public DateTimeOffset DateJoined { get; set; }

        [JsonProperty("about")]
        public string? About { get; set; }

        [JsonProperty("display_name")]
        public string? DisplayName { get; set; }

        [JsonProperty("identity_type")]
        public string? IdentityType { get; set; }

        [JsonProperty("friend_status")]
        public string? FriendStatus { get; set; }

        [JsonProperty("email")]
        public string? Email { get; set; }
    }
}
