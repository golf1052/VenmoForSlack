using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NodaTime;
using NodaTime.Text;
using golf1052.SlackAPI;
using VenmoForSlack.Database;
using VenmoForSlack.Database.Models;
using VenmoForSlack.Models;
using VenmoForSlack.Venmo;
using golf1052.SlackAPI.Objects;
using Microsoft.Extensions.Logging;
using VenmoForSlack.Controllers;
using System.Net.Http;

namespace VenmoForSlack
{
    public class ScheduleProcessor
    {
        private readonly ILogger<ScheduleProcessor> logger;
        private readonly ILogger<VenmoApi> venmoApiLogger;
        private readonly HttpClient httpClient;
        private readonly IClock clock;
        private readonly Duration CheckDuration;
        private Task checkTask;

        public ScheduleProcessor(ILogger<ScheduleProcessor> logger,
            ILogger<VenmoApi> venmoApiLogger,
            HttpClient httpClient,
            IClock clock)
        {
            this.logger = logger;
            this.venmoApiLogger = venmoApiLogger;
            this.httpClient = httpClient;
            this.clock = clock;
            CheckDuration = Duration.FromMinutes(15);
            checkTask = CheckSchedules();
        }

        private async Task CheckSchedules()
        {
            while (true)
            {
                foreach (var workspace in Settings.SettingsObject.Workspaces.Workspaces)
                {
                    WorkspaceInfo workspaceInfo = workspace.Value.ToObject<WorkspaceInfo>()!;
                    MongoDatabase database = new MongoDatabase(workspace.Key);
                    SlackCore slackApi = new SlackCore(workspaceInfo.BotToken);
                    var slackUsers = await slackApi.UsersList();
                    List<Database.Models.VenmoUser> users = database.GetAllUsers();
                    foreach (var user in users)
                    {
                        if (user.Schedule != null)
                        {
                            VenmoApi venmoApi = new VenmoApi(venmoApiLogger);
                            string? accessToken = await VenmoController.CheckIfAccessTokenIsExpired(user, venmoApi, database);
                            if (string.IsNullOrEmpty(accessToken))
                            {
                                logger.LogError($"Unable to refresh access token for {user.UserId}");
                                await WebhookController.SendSlackMessage(workspaceInfo,
                                    "Unable to process scheduled Venmos as your token has expired, Please refresh it.",
                                    user.UserId, httpClient);
                                continue;
                            }

                            venmoApi.AccessToken = accessToken;
                            bool saveNeeded = false;
                            for (int i = 0; i < user.Schedule.Count; i++)
                            {
                                VenmoSchedule schedule = user.Schedule[i];
                                Instant now = clock.GetCurrentInstant();
                                Instant scheduledTime = Instant.FromDateTimeUtc(schedule.NextExecution);
                                bool deleteSchedule = false;
                                if (now > scheduledTime)
                                {
                                    saveNeeded = true;
                                    await WebhookController.SendSlackMessage(workspaceInfo,
                                        $"Processing {schedule.Command}",
                                        user.UserId,
                                        httpClient);
                                    
                                    string[] splitPaymentMessage = VenmoController.ConvertScheduleMessageIntoPaymentMessage(schedule.Command.Split(' '));

                                    ParsedVenmoPayment parsedVenmoPayment;
                                    try
                                    {
                                        parsedVenmoPayment = VenmoController.ParseVenmoPaymentMessage(splitPaymentMessage);
                                    }
                                    catch (Exception ex)
                                    {
                                        logger.LogWarning(ex, "Failed to parse payment, this shouldn't happen as it was parsed before being saved.");
                                        continue;
                                    }

                                    var response = await VenmoController.VenmoPayment(venmoApi, user, database,
                                        parsedVenmoPayment.Amount, parsedVenmoPayment.Note, parsedVenmoPayment.Recipients,
                                        parsedVenmoPayment.Action, parsedVenmoPayment.Audience);

                                    foreach (var r in response.responses)
                                    {
                                        if (!string.IsNullOrEmpty(r.Error))
                                        {
                                            await WebhookController.SendSlackMessage(workspaceInfo,
                                                $"Venmo error: {r.Error}",
                                                user.UserId, httpClient);
                                            continue;
                                        }

                                        if (parsedVenmoPayment.Amount < 0)
                                        {
                                            await WebhookController.SendSlackMessage(workspaceInfo,
                                                $"Successfully charged {r.Data!.Payment.Target.User.Username} ${r.Data.Payment.Amount} for {r.Data.Payment.Note}. Audience is {r.Data.Payment.Audience}",
                                                user.UserId, httpClient);
                                        }
                                        else
                                        {
                                            await WebhookController.SendSlackMessage(workspaceInfo,
                                                $"Successfully paid {r.Data!.Payment.Target.User.Username} ${r.Data.Payment.Amount} for {r.Data.Payment.Note}. Audience is {r.Data.Payment.Audience}",
                                                user.UserId, httpClient);
                                        }
                                    }

                                    foreach (var u in response.unprocessedRecipients)
                                    {
                                        await WebhookController.SendSlackMessage(workspaceInfo,
                                            $"You are not friends with {u}.",
                                            user.UserId, httpClient);
                                    }

                                    if (schedule.Verb == "at" || schedule.Verb == "on")
                                    {
                                        deleteSchedule = true;
                                    }
                                    else if (schedule.Verb == "every")
                                    {
                                        SlackUser? slackUser = HelperMethods.GetSlackUser(user.UserId, slackUsers);
                                        if (slackUser == null)
                                        {
                                            // user somehow doesn't exist?
                                            logger.LogError($"While trying to process schedule for a slack user they disappeared? {user.UserId}");
                                            deleteSchedule = true;
                                        }
                                        else
                                        {
                                            ZonedDateTime nextExecution = ConvertScheduleMessageIntoDateTime(
                                                schedule.Command.Split(' '),
                                                slackUser.TimeZone,
                                                clock);
                                            await WebhookController.SendSlackMessage(workspaceInfo,
                                                $"Next execution is {nextExecution.GetFriendlyZonedDateTimeString()}",
                                                user.UserId,
                                                httpClient);
                                            schedule.NextExecution = nextExecution.ToDateTimeUtc();
                                        }
                                    }
                                }

                                if (deleteSchedule)
                                {
                                    user.Schedule.RemoveAt(i);
                                    i--;
                                }
                            }

                            if (saveNeeded)
                            {
                                database.SaveUser(user);
                            }
                        }
                    }
                }

                await Task.Delay(CheckDuration.ToTimeSpan());
            }
        }

        public static ZonedDateTime ConvertScheduleMessageIntoDateTime(string[] splitMessage,
            string userTimeZoneString,
            IClock clock)
        {
            int payOrChargeIndex = HelperMethods.FindStringInList(splitMessage, "pay");
            if (payOrChargeIndex == -1)
            {
                payOrChargeIndex = HelperMethods.FindStringInList(splitMessage, "charge");
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

        public static string GetScheduleMessageVerb(string[] splitMessage)
        {
            string verb = splitMessage[2].ToLower();
            if (verb != "every" && verb != "at" && verb != "on")
            {
                throw new Exception("Invalid schedule string, expected \"every\", \"at\", or \"on\" in string.");
            }
            return verb;
        }

        public static ZonedDateTime ConvertDateStringIntoDateTime(string dateString,
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

        private static LocalTime ToMidday(LocalTime time)
        {
            return new LocalTime(12, 0);
        }
    }
}
