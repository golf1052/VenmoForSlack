using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace VenmoForSlack.Database.Models
{
    public class VenmoAuthObject
    {
        [BsonElement("access_token")]
        public string? AccessToken { get; set; }

        [BsonElement("expires_in")]
        public object? ExpiresIn { get; set; }

        [BsonElement("user_id")]
        public string? UserId { get; set; }

        [BsonElement("refresh_token")]
        public string? RefreshToken { get; set; }

        [BsonElement("device_id")]
        public string? DeviceId { get; set; }

        [BsonElement("otp_secret")]
        public string? OtpSecret { get; set; }
    }
}
