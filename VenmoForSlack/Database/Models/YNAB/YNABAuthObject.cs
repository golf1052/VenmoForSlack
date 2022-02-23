using MongoDB.Bson.Serialization.Attributes;

namespace VenmoForSlack.Database.Models.YNAB
{
    public class YNABAuthObject
    {
        [BsonElement("access_token")]
        public string? AccessToken { get; set; }

        [BsonElement("expires_in")]
        public object? ExpiresIn { get; set; }

        [BsonElement("refresh_token")]
        public string? RefreshToken { get; set; }
    }
}
