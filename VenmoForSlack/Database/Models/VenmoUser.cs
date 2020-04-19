using System;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace VenmoForSlack.Database.Models
{
    public class VenmoUser
    {
        [BsonId]
        public string UserId { get; set; }

        [BsonElement("venmo")]
        public VenmoAuthObject? Venmo { get; set; }

        [BsonElement("lastModified")]
        public DateTime LastModified { get; set; }

        [BsonElement("last")]
        public string? Last { get; set; }

        [BsonElement("lastWebhook")]
        public string? LastWebhook { get; set; }

        [BsonElement("alias")]
        public BsonDocument? Alias { get; set; }

        [BsonElement("cache")]
        public BsonDocument? Cache { get; set; }

        public VenmoAlias? GetAlias(string alias)
        {
            if (Alias != null)
            {
                bool gotElement = Alias.TryGetElement(alias, out BsonElement element);
                if (gotElement)
                {
                    BsonDocument doc = element.Value.AsBsonDocument;
                    return new VenmoAlias()
                    {
                        Username = doc.GetElement("username").Value.AsString,
                        Id = doc.GetElement("id").Value.AsString
                    };
                }
            }
            return null;
        }

        public string? GetAliasId(string alias)
        {
            VenmoAlias? venmoAlias = GetAlias(alias);
            if (venmoAlias != null)
            {
                return venmoAlias.Id;
            }
            else
            {
                return null;
            }
        }

        public CachedVenmoUser? GetCachedId(string username)
        {
            if (Cache != null)
            {
                bool gotElement = Cache.TryGetElement(username, out BsonElement element);
                if (gotElement)
                {
                    BsonDocument doc = element.Value.AsBsonDocument;
                    return new CachedVenmoUser()
                    {
                        Id = doc.GetElement("id").Value.AsString
                    };
                }
            }
            return null;
        }
    }
}
