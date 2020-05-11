using System;
using System.Collections.Generic;
using golf1052.SlackAPI.Objects;
using NodaTime;

namespace VenmoForSlack
{
    public static class HelperMethods
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

        /// <summary>
        /// Finds the index of the given string (case insensitive) in the given enumerable.
        /// Returns -1 if the string was not found.
        /// </summary>
        /// <param name="enumerable">The enumerable to look in.</param>
        /// <param name="str">The string to look for.</param>
        /// <returns>The index of the string, -1 if not found.</returns>
        public static int FindStringInList(IEnumerable<string> enumerable, string str)
        {
            int i = 0;
            foreach (var element in enumerable)
            {
                if (element.ToLower() == str.ToLower())
                {
                    return i;
                }
                i++;
            }
            return -1;
        }

        public static int FindLastStringInList(IEnumerable<string> enumerable, string str)
        {
            int index = -1;
            int i = 0;
            foreach (var item in enumerable)
            {
                if (item.ToLower() == str.ToLower())
                {
                    index = i;
                }
                i += 1;
            }
            return index;
        }

        public static SlackUser? GetSlackUser(string userId, List<SlackUser> slackUsers)
        {
            foreach (var user in slackUsers)
            {
                if (user.Id == userId)
                {
                    return user;
                }
            }
            return null;
        }

        public static string GetFriendlyZonedDateTimeString(this ZonedDateTime zonedDateTime)
        {
            return zonedDateTime.ToDateTimeOffset().ToString("f");
        }
    }
}
