using System.Collections.Generic;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace VenmoForSlack.Database.Models.YNAB
{
    public class YNABUser
    {
        [BsonElement("auth")]
        public YNABAuthObject? Auth { get; set; }

        [BsonElement("defaultAccount")]
        public string? DefaultAccount { get; set; }

        [BsonElement("mapping")]
        public List<YNABCategoryMapping>? Mapping { get; set; }
    }
}
