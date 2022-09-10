using System;
using MongoDB.Bson.Serialization.Attributes;

namespace VenmoForSlack.Database.Models
{
    public class VenmoSchedule
    {
        [BsonElement("verb")]
        public string Verb { get; set; }

        [BsonElement("nextExecution")]
        public DateTime NextExecution { get; set; }

        [BsonElement("command")]
        public string Command { get; set; }

        [BsonConstructor]
        public VenmoSchedule(string verb, DateTime nextExecution, string command)
        {
            Verb = verb;
            NextExecution = nextExecution;
            Command = command;
        }
    }
}
