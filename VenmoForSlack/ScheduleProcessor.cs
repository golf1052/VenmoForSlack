using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NodaTime;
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
        private readonly ILogger<MongoDatabase> mongoDatabaseLogger;
        private readonly HttpClient httpClient;
        private readonly IClock clock;
        private readonly HelperMethods helperMethods;
        private readonly Duration CheckDuration;
        private Task checkTask;

        public ScheduleProcessor(ILogger<ScheduleProcessor> logger,
            ILogger<VenmoApi> venmoApiLogger,
            ILogger<MongoDatabase> mongoDatabaseLogger,
            HttpClient httpClient,
            IClock clock,
            HelperMethods helperMethods)
        {
            this.logger = logger;
            this.venmoApiLogger = venmoApiLogger;
            this.mongoDatabaseLogger = mongoDatabaseLogger;
            this.httpClient = httpClient;
            this.clock = clock;
            this.helperMethods = helperMethods;
            CheckDuration = Duration.FromMinutes(15);
            checkTask = CheckSchedules();
            _ = CheckCheckSchedulesTask();
        }

        private async Task CheckSchedules()
        {
            while (true)
            {
                foreach (var workspace in Settings.SettingsObject.Workspaces.Workspaces)
                {
                    WorkspaceInfo workspaceInfo = workspace.Value.ToObject<WorkspaceInfo>()!;
                    MongoDatabase database = new MongoDatabase(workspace.Key, mongoDatabaseLogger);
                    SlackCore slackApi = new SlackCore(workspaceInfo.BotToken);
                    var slackUsers = await slackApi.UsersList();
                    List<Database.Models.VenmoUser> users = database.GetAllUsers();
                    foreach (var user in users)
                    {
                        if (user.Schedule != null && user.Schedule.Count > 0)
                        {
                            VenmoApi venmoApi = new VenmoApi(venmoApiLogger);
                            string? accessToken = await helperMethods.CheckIfVenmoAccessTokenIsExpired(user, venmoApi, database);
                            if (string.IsNullOrEmpty(accessToken))
                            {
                                logger.LogError($"Unable to refresh Venmo access token for {user.UserId}");
                                await WebhookController.SendSlackMessage(workspaceInfo,
                                    "Unable to process scheduled Venmos as your token has expired. Please refresh it.",
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
                                    
                                    string[] splitPaymentMessage = helperMethods.ConvertScheduleMessageIntoPaymentMessage(schedule.Command.Split(' '));

                                    ParsedVenmoPayment parsedVenmoPayment;
                                    try
                                    {
                                        parsedVenmoPayment = helperMethods.ParseVenmoPaymentMessage(splitPaymentMessage);
                                    }
                                    catch (Exception ex)
                                    {
                                        logger.LogWarning(ex, "Failed to parse payment, this shouldn't happen as it was parsed before being saved.");
                                        continue;
                                    }

                                    var response = await helperMethods.VenmoPayment(venmoApi, user, database,
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

                                        if (parsedVenmoPayment.Action == VenmoAction.Charge)
                                        {
                                            await WebhookController.SendSlackMessage(workspaceInfo,
                                                $"Successfully charged {r.Data!.Payment.Target!.User.Username} ${r.Data.Payment.Amount} for {r.Data.Payment.Note}. Audience is {r.Data.Payment.Audience}",
                                                user.UserId, httpClient);
                                        }
                                        else
                                        {
                                            await WebhookController.SendSlackMessage(workspaceInfo,
                                                $"Successfully paid {r.Data!.Payment.Target!.User.Username} ${r.Data.Payment.Amount} for {r.Data.Payment.Note}. Audience is {r.Data.Payment.Audience}",
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
                                        SlackUser? slackUser = helperMethods.GetSlackUser(user.UserId, slackUsers);
                                        if (slackUser == null)
                                        {
                                            // user somehow doesn't exist?
                                            logger.LogError($"While trying to process schedule for a slack user they disappeared? {user.UserId}");
                                            deleteSchedule = true;
                                        }
                                        else
                                        {
                                            ZonedDateTime nextExecution = helperMethods.ConvertScheduleMessageIntoDateTime(
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

        private async Task CheckCheckSchedulesTask()
        {
            while (true)
            {
                if (checkTask.IsFaulted)
                {
                    logger.LogError("Check schedules task has failed, restarting.");
                    if (checkTask.Exception != null)
                    {
                        foreach (var exception in checkTask.Exception.InnerExceptions)
                        {
                            logger.LogError(exception, "InnerException");
                        }
                    }
                    checkTask = CheckSchedules();
                }
                await Task.Delay((int)CheckDuration.TotalMilliseconds / 2);
            }
        }
    }
}
