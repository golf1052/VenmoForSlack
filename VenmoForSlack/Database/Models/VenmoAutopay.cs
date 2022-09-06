using System;
using MongoDB.Bson.Serialization.Attributes;

namespace VenmoForSlack.Database.Models
{
    public class VenmoAutopay
    {
        [BsonElement("username")]
        public string Username { get; set; }

        [BsonElement("user_id")]
        public string UserId { get; set; }

        /// <summary>
        /// Comparison can be one of '=' '&lt;' '&lt;='
        /// </summary>
        [BsonElement("comparison")]
        public string? Comparison { get; set; }

        [BsonElement("amount")]
        public double? Amount { get; set; }

        [BsonElement("note")]
        public string? Note { get; set; }

        [BsonElement("last_run")]
        public DateTime? LastRun { get; set; }
    }
}
