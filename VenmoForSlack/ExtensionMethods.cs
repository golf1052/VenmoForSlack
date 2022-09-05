using System;
using MongoDB.Bson;
using NodaTime;

namespace VenmoForSlack
{
    public static class ExtensionMethods
    {
        public static DateTimeOffset NextDayOfWeek(this DateTimeOffset from, DayOfWeek dayOfWeek)
        {
            int start = (int)from.DayOfWeek;
            int target = (int)dayOfWeek;
            if (target <= start)
            {
                target += 7;
            }
            return from.AddDays(target - start);
        }

        public static string GetFriendlyZonedDateTimeString(this ZonedDateTime zonedDateTime)
        {
            return zonedDateTime.ToDateTimeOffset().ToString("f");
        }

        public static bool TryGetElementIgnoreCase(this BsonDocument bsonDocument, string name, out BsonElement value)
        {
            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            foreach (var element in bsonDocument.Elements)
            {
                if (element.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = element;
                    return true;
                }
            }

            value = default(BsonElement);
            return false;
        }
    }
}
