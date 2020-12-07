using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using VenmoForSlack.Database.Models;

namespace VenmoForSlack.Database
{
    public class MongoDatabase
    {
        private readonly ILogger logger;
        private IMongoDatabase database;
        private IMongoCollection<VenmoUser> usersCollection;

        public MongoDatabase(string workspaceId, ILogger<MongoDatabase> logger)
        {
            this.logger = logger;
            database = Program.Mongo.GetDatabase($"yhackslackpack_{workspaceId}");
            try
            {
                database.CreateCollection("users");
            }
            catch (MongoCommandException)
            {
                // collection already exists
            }
            usersCollection = database.GetCollection<VenmoUser>("users");
        }

        public List<VenmoUser> GetAllUsers()
        {
            return usersCollection.Find(_ => true).ToList();
        }

        public VenmoUser? GetUser(string userId)
        {
            var filter = Builders<VenmoUser>.Filter.Eq("_id", userId);
            VenmoUser? venmoUser = usersCollection.Find(filter).FirstOrDefault();
            return venmoUser;
        }

        public void SaveUser(VenmoUser venmoUser)
        {
            venmoUser.LastModified = DateTime.UtcNow;
            usersCollection.ReplaceOne(Builders<VenmoUser>.Filter.Eq<string>("_id", venmoUser.UserId),
                venmoUser,
                new ReplaceOptions()
                {
                    IsUpsert = true
                });
        }

        public bool? DeleteUser(string userId)
        {
            var filter = Builders<VenmoUser>.Filter.Eq("_id", userId);
            var deleteResult = usersCollection.DeleteOne(filter);
            if (deleteResult.IsAcknowledged)
            {
                var deleted = deleteResult.DeletedCount == 1;
                if (!deleted)
                {
                    logger.LogWarning($"Did not find {userId} to delete in {database.DatabaseNamespace.DatabaseName}");
                }
                return deleted;
            }
            else
            {
                logger.LogWarning($"Could not delete {userId} in {database.DatabaseNamespace.DatabaseName}");
                return null;
            }
        }
    }
}
