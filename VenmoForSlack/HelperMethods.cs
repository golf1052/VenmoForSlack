using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using golf1052.SlackAPI;
using golf1052.SlackAPI.BlockKit.BlockElements;
using golf1052.SlackAPI.Objects;
using golf1052.YNABAPI.Client;
using golf1052.YNABAPI.Model;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using NodaTime;
using NodaTime.Text;
using VenmoForSlack.Database;
using VenmoForSlack.Database.Models;
using VenmoForSlack.Venmo;
using VenmoForSlack.Venmo.Models.Responses;

namespace VenmoForSlack
{
    public class HelperMethods
    {
        private readonly ILogger logger;

        public HelperMethods(ILogger<HelperMethods> logger)
        {
            this.logger = logger;
        }

        public async Task<List<SlackUser>> GetCachedSlackUsers(string botToken,
            TimeSpan cacheItemLifetime,
            SlackCore slackApi,
            IMemoryCache cache)
        {
            return await cache.GetOrCreateAsync(botToken, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = cacheItemLifetime;
                return await slackApi.UsersList();
            });
        }

        public Image GetVenmoUserProfileImage(Venmo.Models.VenmoUser user)
        {
            return new Image(user.ProfilePictureUrl, GetVenmoUserProfilePictureAltText(user));
        }

        public string GetVenmoUserProfilePictureAltText(Venmo.Models.VenmoUser user)
        {
            return $"Venmo profile picture of {user.DisplayName}";
        }

        public ZonedDateTime ConvertScheduleMessageIntoDateTime(string[] splitMessage,
            string userTimeZoneString,
            IClock clock)
        {
            int payOrChargeIndex = FindStringInList(splitMessage, "pay");
            if (payOrChargeIndex == -1)
            {
                payOrChargeIndex = FindStringInList(splitMessage, "charge");
                if (payOrChargeIndex == -1)
                {
                    throw new Exception("Invalid schedule string, could not find \"pay\" or \"charge\" in string.");
                }
            }

            if (splitMessage[payOrChargeIndex - 1].ToLower() == "private" ||
                splitMessage[payOrChargeIndex - 1].ToLower() == "friends" ||
                splitMessage[payOrChargeIndex - 1].ToLower() == "public")
            {
                payOrChargeIndex -= 1;
            }

            string verb;
            try
            {
                verb = GetScheduleMessageVerb(splitMessage);
            }
            catch (Exception)
            {
                throw;
            }

            string dateString = string.Join(' ', splitMessage[3..payOrChargeIndex]);
            try
            {
                ZonedDateTime scheduledTime = ConvertDateStringIntoDateTime(dateString, userTimeZoneString, clock);
                return scheduledTime;
            }
            catch (Exception)
            {
                throw;
            }
        }

        public string GetScheduleMessageVerb(string[] splitMessage)
        {
            string verb = splitMessage[2].ToLower();
            if (verb != "every" && verb != "at" && verb != "on")
            {
                throw new Exception("Invalid schedule string, expected \"every\", \"at\", or \"on\" in string.");
            }
            return verb;
        }

        public ZonedDateTime ConvertDateStringIntoDateTime(string dateString,
            string userTimeZoneString,
            IClock clock)
        {
            Instant utcNow = clock.GetCurrentInstant();
            DateTimeZone userTimeZone = DateTimeZoneProviders.Tzdb[userTimeZoneString];
            ZonedDateTime zonedNow = new ZonedDateTime(utcNow, userTimeZone);
            if (dateString.ToLower() == "day" || dateString.ToLower() == "tomorrow")
            {
                // tomorrow at 12 PM
                return zonedNow.LocalDateTime.With(ToMidday)
                .PlusDays(1)
                .InZoneLeniently(userTimeZone);
            }
            else if (dateString.ToLower() == "sunday" || dateString.ToLower() == "sun")
            {
                return zonedNow.LocalDateTime.Next(IsoDayOfWeek.Sunday)
                    .With(ToMidday)
                    .InZoneLeniently(userTimeZone);
            }
            else if (dateString.ToLower() == "monday" || dateString.ToLower() == "mon")
            {
                return zonedNow.LocalDateTime.Next(IsoDayOfWeek.Monday)
                    .With(ToMidday)
                    .InZoneLeniently(userTimeZone);
            }
            else if (dateString.ToLower() == "tuesday" || dateString.ToLower() == "tue")
            {
                return zonedNow.LocalDateTime.Next(IsoDayOfWeek.Tuesday)
                    .With(ToMidday)
                    .InZoneLeniently(userTimeZone);
            }
            else if (dateString.ToLower() == "wednesday" || dateString.ToLower() == "wed")
            {
                return zonedNow.LocalDateTime.Next(IsoDayOfWeek.Wednesday)
                    .With(ToMidday)
                    .InZoneLeniently(userTimeZone);
            }
            else if (dateString.ToLower() == "thursday" || dateString.ToLower() == "thu" || dateString.ToLower() == "thur")
            {
                return zonedNow.LocalDateTime.Next(IsoDayOfWeek.Thursday)
                    .With(ToMidday)
                    .InZoneLeniently(userTimeZone);
            }
            else if (dateString.ToLower() == "friday" || dateString.ToLower() == "fri")
            {
                return zonedNow.LocalDateTime.Next(IsoDayOfWeek.Friday)
                    .With(ToMidday)
                    .InZoneLeniently(userTimeZone);
            }
            else if (dateString.ToLower() == "saturday" || dateString.ToLower() == "sat")
            {
                return zonedNow.LocalDateTime.Next(IsoDayOfWeek.Saturday)
                    .With(ToMidday)
                    .InZoneLeniently(userTimeZone);
            }
            else if (dateString.ToLower() == "beginning of the month" || dateString.ToLower() == "beginning of month")
            {
                return zonedNow.LocalDateTime.With(DateAdjusters.StartOfMonth)
                    .PlusMonths(1)
                    .With(ToMidday)
                    .InZoneLeniently(userTimeZone);
            }
            else if (dateString.ToLower() == "end of the month" || dateString.ToLower() == "end of month")
            {
                return zonedNow.LocalDateTime.With(DateAdjusters.EndOfMonth)
                    .With(ToMidday)
                    .InZoneLeniently(userTimeZone);
            }
            else if (int.TryParse(dateString, out int date))
            {
                if (date < 1 || date > 31)
                {
                    // date isn't allowed to be less than 1 or larger than 31
                    throw new Exception("Date isn't allowed to be less than 1 or larger than 31");
                }

                int daysInMonth = zonedNow.Calendar.GetDaysInMonth(zonedNow.Year, zonedNow.Month);
                if (date > daysInMonth)
                {
                    // if the given date is past the end of the month
                    if (zonedNow.Day == daysInMonth)
                    {
                        // and if the current day is the last day of this month schedule for the date in the next month
                        LocalDateTime firstDayNextMonth = zonedNow.LocalDateTime.PlusDays(1);
                        int daysInNextMonth = firstDayNextMonth.Calendar.GetDaysInMonth(firstDayNextMonth.Year, firstDayNextMonth.Month);
                        if (date > daysInNextMonth)
                        {
                            // same as before, if the given date is past the end of the month return the end of the month
                            
                            // note that this case can't happen ever in the Gregorian calendar, we're checking if
                            // the requested date is past the end of the current month AND the requested date is past
                            // the end of the next month, this isn't possible because there are no less than 31 day
                            // months twice in a row. 
                            return firstDayNextMonth.With(DateAdjusters.EndOfMonth)
                                .With(ToMidday)
                                .InZoneLeniently(userTimeZone);
                        }
                        else
                        {
                            // else return the day specified
                            return firstDayNextMonth.With((LocalDate localDate) =>
                            {
                                return new LocalDate(localDate.Year, localDate.Month, date);
                            })
                            .With(ToMidday)
                            .InZoneLeniently(userTimeZone);
                        }
                    }
                    else
                    {
                        // else first check if the requested date is the current date or before it
                        if (date <= zonedNow.Day)
                        {
                            // this case can't ever happen because if we request a date past the end of this month
                            // we already have a check if we are at the end of this month (last if). we can't possibly
                            // get to this branch and have the requested date be before or equal to the current date

                            // if it's before the current date we need to shift to the next month but we also need to
                            // check if the requested date is after the last day of the next month
                            // shift to first of month first before moving to next month so we don't get an invalid date
                            LocalDateTime firstDayNextMonth = zonedNow.LocalDateTime.With(DateAdjusters.StartOfMonth)
                                .PlusMonths(1);
                            int daysInNextMonth = firstDayNextMonth.Calendar.GetDaysInMonth(firstDayNextMonth.Year, firstDayNextMonth.Month);
                            if (date > daysInNextMonth)
                            {
                                return firstDayNextMonth.With(DateAdjusters.EndOfMonth)
                                    .With(ToMidday)
                                    .InZoneLeniently(userTimeZone);
                            }
                            else
                            {
                                return firstDayNextMonth.With((LocalDate localDate) =>
                                {
                                    return new LocalDate(localDate.Year, localDate.Month, date);
                                })
                                .With(ToMidday)
                                .InZoneLeniently(userTimeZone);
                            }
                        }
                        else
                        {
                            return zonedNow.LocalDateTime.With(DateAdjusters.EndOfMonth)
                                .With(ToMidday)
                                .InZoneLeniently(userTimeZone);
                        }
                    }
                }
                else
                {
                    if (date <= zonedNow.Day)
                    {
                        // if the date requested is before the current date, set to a month in the future
                        // we also need to check that the date in the next month is actually a valid date
                        LocalDateTime firstDayNextMonth = zonedNow.LocalDateTime.With(DateAdjusters.StartOfMonth)
                            .PlusMonths(1);
                        int daysInNextMonth = firstDayNextMonth.Calendar.GetDaysInMonth(firstDayNextMonth.Year, firstDayNextMonth.Month);
                        if (date > daysInNextMonth)
                        {
                            return firstDayNextMonth.With(DateAdjusters.EndOfMonth)
                                .With(ToMidday)
                                .InZoneLeniently(userTimeZone);
                        }
                        else
                        {
                            return firstDayNextMonth.With((LocalDate localDate) =>
                            {
                                return new LocalDate(localDate.Year, localDate.Month, date);
                            })
                            .With(ToMidday)
                            .InZoneLeniently(userTimeZone);
                        }
                    }
                    else
                    {
                        return zonedNow.LocalDateTime.With((LocalDate localDate) =>
                        {
                            return new LocalDate(localDate.Year, localDate.Month, date);
                        })
                        .With(ToMidday)
                        .InZoneLeniently(userTimeZone);
                    }
                    
                }
            }
            else
            {
                // else just try and parse the date as ISO
                ParseResult<LocalDate> localDateParseResult = LocalDatePattern.Iso.Parse(dateString);
                if (localDateParseResult.Success)
                {
                    ZonedDateTime zonedResult = localDateParseResult.Value.At(new LocalTime(12, 0))
                        .InZoneLeniently(userTimeZone);
                    if (zonedResult.LocalDateTime <= zonedNow.LocalDateTime)
                    {
                        throw new Exception("Scheduled date needs to be after current date.");
                    }
                    else
                    {
                        return zonedResult;
                    }
                }

                ParseResult<LocalDateTime> localDateTimeWithoutSecondsParseResult =
                    LocalDateTimePattern.CreateWithInvariantCulture("uuuu'-'MM'-'dd'T'HH':'mm")
                    .Parse(dateString);
                if (localDateTimeWithoutSecondsParseResult.Success)
                {
                    ZonedDateTime zonedResult = localDateTimeWithoutSecondsParseResult.Value.InZoneLeniently(userTimeZone);
                    if (zonedResult.LocalDateTime <= zonedNow.LocalDateTime)
                    {
                        throw new Exception("Scheduled date needs to be after current date.");
                    }
                    else
                    {
                        return zonedResult;
                    }
                }

                ParseResult<LocalDateTime> localDateTimeParseResult = LocalDateTimePattern.GeneralIso.Parse(dateString);
                if (localDateTimeParseResult.Success)
                {
                    ZonedDateTime zonedResult = localDateTimeParseResult.Value.InZoneLeniently(userTimeZone);
                    if (zonedResult.LocalDateTime <= zonedNow.LocalDateTime)
                    {
                        throw new Exception("Scheduled date needs to be after current date.");
                    }
                    else
                    {
                        return zonedResult;
                    }
                }
                
                ParseResult<Instant> instantParseResult = InstantPattern.General.Parse(dateString);
                if (instantParseResult.Success)
                {
                    ZonedDateTime zonedResult = instantParseResult.Value.InZone(userTimeZone);
                    if (zonedResult.LocalDateTime <= zonedNow.LocalDateTime)
                    {
                        // result needs to be after current date
                        throw new Exception($"Scheduled date need to be after current date");
                    }
                    else
                    {
                        return zonedResult;
                    }
                    
                }

                throw new Exception($"Unable to parse: {dateString}");
            }
        }

        private LocalTime ToMidday(LocalTime time)
        {
            return new LocalTime(12, 0);
        }

        public async Task<string?> CheckIfVenmoAccessTokenIsExpired(Database.Models.VenmoUser venmoUser,
            VenmoApi venmoApi,
            MongoDatabase database)
        {
            // With the new auth method Venmo access tokens never expire and ExpiresIn (and RefreshToken) will be null.
            // However since some users still have old, refreshable tokens we'll continue to check if they've expired.
            if (venmoUser.Venmo!.ExpiresIn == null || (venmoUser.Venmo!.ExpiresIn.GetType() == typeof(string) && venmoUser.Venmo!.ExpiresIn.ToString() == string.Empty))
            {
                return venmoUser.Venmo.AccessToken;
            }
            DateTime expiresDate = (DateTime)venmoUser.Venmo!.ExpiresIn;
            if (expiresDate < DateTime.UtcNow)
            {
                VenmoAuthResponse response;
                try
                {
                    response = await venmoApi.RefreshAuth(venmoUser.Venmo.RefreshToken!);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, $"Exception while refreshing Venmo auth token for {venmoUser.UserId}");
                    venmoUser.Venmo = new VenmoAuthObject()
                    {
                        AccessToken = "",
                        ExpiresIn = "",
                        RefreshToken = "",
                        UserId = ""
                    };
                    database.SaveUser(venmoUser);
                    return null;
                }

                // The user id is already in the database so set the venmoApi.UserId here because we don't get it from
                // the API response
                venmoApi.UserId = venmoUser.Venmo.UserId;
                venmoUser.Venmo.AccessToken = response.AccessToken;
                venmoUser.Venmo.ExpiresIn = DateTime.UtcNow + TimeSpan.FromSeconds(response.ExpiresIn);
                venmoUser.Venmo.RefreshToken = response.RefreshToken;

                database.SaveUser(venmoUser);
            }
            return venmoUser.Venmo.AccessToken;
        }

        public ParsedVenmoPayment ParseVenmoPaymentMessage(string[] splitMessage)
        {
            string audienceString = "private";
            if (splitMessage[2].ToLower() == "charge" || splitMessage[2].ToLower() == "pay")
            {
                audienceString = splitMessage[1].ToLower();
                if (audienceString != "public" && audienceString != "friends" && audienceString != "private")
                {
                    throw new Exception("Valid payment sharing commands\npublic\nfriend\nprivate");
                }
                var list = splitMessage.ToList();
                list.RemoveAt(1);
                splitMessage = list.ToArray();
            }

            VenmoAudience audience;
            try
            {
                audience = VenmoAudienceHelperMethods.FromString(audienceString);
            }
            catch (Exception)
            {
                throw new Exception("Valid payment sharing commands\npublic\nfriend\nprivate");
            }

            string which = splitMessage[1];
            VenmoAction action;
            
            try
            {
                action = VenmoActionHelperMethods.FromString(which);
            }
            catch (Exception)
            {
                throw new Exception("Unknown payment type, must be either 'pay' or 'charge'.");
            }

            if (splitMessage.Length <= 6)
            {
                throw new Exception("Invalid payment string.");
            }

            int forIndex = FindStringInList(splitMessage, "for");
            if (forIndex == -1)
            {
                throw new Exception("Invalid payment string, you need to include what the payment is \"for\".");
            }

            string[] amountStringArray = splitMessage[2..forIndex];

            double amount;
            try
            {
                amount = CalculateTotal(amountStringArray.ToList());
            }
            catch (Exception)
            {
                throw;
            }

            int toIndex = FindLastStringInList(splitMessage, "to");
            if (toIndex < 5)
            {
                throw new Exception("Could not find recipients");
            }

            string note = string.Join(' ', splitMessage[(forIndex + 1)..toIndex]);
            List<string> recipients = splitMessage[(toIndex + 1)..].ToList();
            return new ParsedVenmoPayment(amount, note, recipients, action, audience);
        }

        public async Task<(List<VenmoPaymentWithBalanceResponse> responses, List<string> unprocessedRecipients)> VenmoPayment(
            VenmoApi venmoApi,
            Database.Models.VenmoUser venmoUser,
            MongoDatabase database,
            double amount,
            string note,
            List<string> recipients,
            VenmoAction action,
            VenmoAudience venmoAudience = VenmoAudience.Private)
        {
            List<Venmo.Models.VenmoUser>? friendsList = null;
            List<string> ids = new List<string>();
            List<string> unprocessedRecipients = new List<string>();
            foreach (var recipient in recipients)
            {
                if (recipient.StartsWith("phone:"))
                {
                    ids.Add(recipient);
                }
                else if (recipient.StartsWith("email:"))
                {
                    ids.Add(recipient);
                }
                else if (recipient.StartsWith("user_id:"))
                {
                    ids.Add(recipient);
                }
                else
                {
                    string? id = venmoUser.GetAliasId(recipient);
                    if (id == null)
                    {
                        id = venmoUser.GetCachedId(recipient)?.Id;
                        if (id == null)
                        {
                            if (friendsList == null)
                            {
                                friendsList = await venmoApi.GetAllFriends();
                            }
                            id = VenmoApi.FindFriendId(recipient, friendsList);
                            if (id != null)
                            {
                                AddUsernameToCache(recipient, id, venmoUser, database);
                            }
                        }
                    }

                    if (id == null)
                    {
                        unprocessedRecipients.Add(recipient);
                        continue;
                    }
                    ids.Add($"user_id:{id}");
                }
            }

            List<VenmoPaymentWithBalanceResponse> responses = await venmoApi.PostPayment(amount, note, ids, action, venmoAudience);
            return (responses, unprocessedRecipients);
        }

        public async Task<(List<Venmo.Models.VenmoUser> foundUsers, List<string> unprocessedRecipients)> ProcessUnknownRecipients(List<string> recipients, VenmoApi venmoApi,
            Database.Models.VenmoUser venmoUser, MongoDatabase database)
        {
            List<Venmo.Models.VenmoUser> foundUsers = new List<Venmo.Models.VenmoUser>();
            List<string> unprocessedRecipients = new List<string>();
            foreach (var recipient in recipients)
            {
                VenmoUserSearchResponse response = await venmoApi.SearchUsers(recipient);
                bool foundUser = false;
                foreach (var user in response.Data!)
                {
                    if (recipient.ToLower() == user.Username!.ToLower())
                    {
                        foundUser = true;
                        foundUsers.Add(user);
                        break;
                    }
                }

                if (!foundUser)
                {
                    unprocessedRecipients.Add(recipient);
                }
            }
            return (foundUsers, unprocessedRecipients);
        }

        public void AddUsernameToCache(string username,
            string id,
            Database.Models.VenmoUser venmoUser,
            MongoDatabase database)
        {
            BsonDocument cacheDoc = new BsonDocument()
            {
                { "id", id }
            };
            if (venmoUser.Cache == null)
            {
                venmoUser.Cache = new BsonDocument();
            }
            venmoUser.Cache.Add(new BsonElement(username, cacheDoc));
            database.SaveUser(venmoUser);
        }

        public double CalculateTotal(List<string> amountStringList)
        {
            string currentSign = string.Empty;
            double? previousNumber = null;
            double? currentNumber = null;
            while (amountStringList.Count > 1)
            {
                for (int i = 0; i < amountStringList.Count; i++)
                {
                    string copy = amountStringList[i];
                    if (copy.StartsWith("$"))
                    {
                        copy = copy.Substring(1);
                    }
                    
                    if (copy == "+" || copy == "-" || copy == "*" || copy == "/")
                    {
                        if (string.IsNullOrEmpty(currentSign))
                        {
                            if (previousNumber == null)
                            {
                                throw new Exception("Invalid arithmetic string");
                            }
                            currentSign = copy;
                        }
                        else
                        {
                            throw new Exception("Invalid arithmetic string");
                        }
                    }
                    else if (previousNumber == null)
                    {
                        bool canParse = double.TryParse(copy, out double number);
                        if (!canParse)
                        {
                            throw new Exception("Invalid arithmetic string");
                        }
                        previousNumber = number;
                    }
                    else if (currentNumber == null)
                    {
                        bool canParse = double.TryParse(copy, out double number);
                        if (!canParse)
                        {
                            throw new Exception("Invalid arithmetic string");
                        }
                        currentNumber = number;

                        double result;
                        try
                        {
                            result = Mathify(previousNumber, currentSign, currentNumber);
                        }
                        catch (ArithmeticException)
                        {
                            throw;
                        }
                        amountStringList[i] = result.ToString();
                        int modifyingI = i;
                        amountStringList.RemoveAt(modifyingI - 1);
                        modifyingI -= 1;
                        amountStringList.RemoveAt(modifyingI - 1);
                        previousNumber = null;
                        currentSign = string.Empty;
                        currentNumber = null;
                        break;
                    }
                }
            }

            if (amountStringList[0].StartsWith("$"))
            {
                amountStringList[0] = amountStringList[0].Substring(1);
            }
            bool parsed = double.TryParse(amountStringList[0], out double final);
            if (!parsed)
            {
                throw new Exception("Could not calculate total");
            }
            return final;
        }

        public double Mathify(double? num1, string? sign, double? num2)
        {
            if (num1 == null || sign == null || num2 == null)
            {
                throw new ArithmeticException($"An argument is null. {nameof(num1)}: {num1}. {nameof(sign)}: {sign}. {nameof(num2)}: {num2}");
            }

            if (sign == "+")
            {
                return num1.Value + num2.Value;
            }
            else if (sign == "-")
            {
                return num1.Value - num2.Value;
            }
            else if (sign == "*")
            {
                return num1.Value * num2.Value;
            }
            else if (sign == "/")
            {
                return num1.Value / num2.Value;
            }
            else
            {
                throw new ArithmeticException($"Unknown sign: {sign}");
            }
        }

        public string[] ConvertScheduleMessageIntoPaymentMessage(string[] splitMessage)
        {
            int payOrChargeIndex = FindStringInList(splitMessage, "pay");
            if (payOrChargeIndex == -1)
            {
                payOrChargeIndex = FindStringInList(splitMessage, "charge");
                if (payOrChargeIndex == -1)
                {
                    throw new Exception("Invalid schedule string, could not find \"pay\" or \"charge\" in string.");
                }
            }

            if (splitMessage[payOrChargeIndex - 1].ToLower() == "private" ||
                splitMessage[payOrChargeIndex - 1].ToLower() == "friends" ||
                splitMessage[payOrChargeIndex - 1].ToLower() == "public")
            {
                payOrChargeIndex -= 1;
            }

            string[] newSplitMessage = new string[splitMessage.Length - payOrChargeIndex + 1];
            Array.Copy(splitMessage, payOrChargeIndex, newSplitMessage, 1, splitMessage.Length - payOrChargeIndex);
            newSplitMessage[0] = splitMessage[0];
            return newSplitMessage;
        }

        /// <summary>
        /// Finds the index of the given string (case insensitive) in the given enumerable.
        /// Returns -1 if the string was not found.
        /// </summary>
        /// <param name="enumerable">The enumerable to look in.</param>
        /// <param name="str">The string to look for.</param>
        /// <returns>The index of the string, -1 if not found.</returns>
        public int FindStringInList(IEnumerable<string> enumerable, string str)
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

        public int FindLastStringInList(IEnumerable<string> enumerable, string str)
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

        public SlackUser? GetSlackUser(string userId, List<SlackUser> slackUsers)
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

        public async Task<string?> CheckIfYNABAccessTokenIsExpired(VenmoUser venmoUser,
            MongoDatabase database,
            ApiClient ynabApi)
        {
            if (venmoUser.YNAB!.Auth == null || venmoUser.YNAB.Auth.AccessToken == null)
            {
                return null;
            }
            DateTime expiresDate = (DateTime)venmoUser.YNAB!.Auth!.ExpiresIn!;
            if (expiresDate < DateTime.UtcNow)
            {
                YNABTokenResponse response;
                try
                {
                    response = await ynabApi.RefreshAuth(venmoUser.YNAB.Auth.RefreshToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, $"Exception while refreshing YNAB auth token for {venmoUser.UserId}");
                    venmoUser.YNAB.Auth = null;
                    database.SaveUser(venmoUser);
                    return null;
                }

                venmoUser.YNAB.Auth.AccessToken = response.AccessToken;
                venmoUser.YNAB.Auth.ExpiresIn = DateTime.UtcNow + TimeSpan.FromSeconds(response.ExpiresIn);
                venmoUser.YNAB.Auth.RefreshToken = response.RefreshToken;
                database.SaveUser(venmoUser);
            }
            return venmoUser.YNAB.Auth.AccessToken;
        }

        public Configuration CreateConfiguration()
        {
            Configuration config = new Configuration();
            config.ClientId = Secrets.YNABClientId;
            config.ClientSecret = Secrets.YNABClientSecret;
            config.RedirectUri = Secrets.YNABRedirectUrl;
            return config;
        }

        public Configuration CreateConfiguration(string accessToken)
        {
            Configuration config = new Configuration();
            config.ClientId = Secrets.YNABClientId;
            config.ClientSecret = Secrets.YNABClientSecret;
            config.RedirectUri = Secrets.YNABRedirectUrl;
            config.AccessToken = accessToken;
            return config;
        }
    }
}
