using System;
using System.Collections.Generic;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using VenmoForSlack.Database.Models.YNAB;

namespace VenmoForSlack.Database.Models
{
    public class VenmoUser
    {
        [BsonId]
        public string UserId { get; set; }

        [BsonElement("venmo")]
        public VenmoAuthObject? Venmo { get; set; }

        [BsonElement("ynab")]
        public YNABUser? YNAB { get; set; }

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

        [BsonElement("schedule")]
        public List<VenmoSchedule>? Schedule { get; set; }

        [BsonElement("autopay")]
        public List<VenmoAutopay>? Autopay { get; set; }

        [BsonConstructor]
        public VenmoUser(string userId)
        {
            UserId = userId;
        }

        public VenmoAlias? GetAlias(string alias)
        {
            if (Alias != null)
            {
                bool gotElement = Alias.TryGetElementIgnoreCase(alias, out BsonElement element);
                if (gotElement)
                {
                    BsonDocument doc = element.Value.AsBsonDocument;
                    return new VenmoAlias(doc.GetElement("username").Value.AsString,
                        doc.GetElement("id").Value.AsString);
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
                bool gotElement = Cache.TryGetElementIgnoreCase(username, out BsonElement element);
                if (gotElement)
                {
                    BsonDocument doc = element.Value.AsBsonDocument;
                    return new CachedVenmoUser(doc.GetElement("id").Value.AsString);
                }
            }
            return null;
        }
    }
}
