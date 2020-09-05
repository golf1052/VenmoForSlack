using System;
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
    }
}
