using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using golf1052.SlackAPI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NodaTime;
using VenmoForSlack.Controllers;
using VenmoForSlack.Database;
using VenmoForSlack.Models;
using VenmoForSlack.Venmo;
using VenmoForSlack.Venmo.Models;

namespace VenmoForSlack
{
    public class AutopayProcessor
    {
        private readonly ILogger<AutopayProcessor> logger;
        private readonly ILogger<MongoDatabase> mongoDatabaseLogger;
        private readonly ILogger<VenmoApi> venmoApiLogger;
        private readonly Dictionary<string, SemaphoreSlim> slackApiRateLimits;
        private readonly Duration checkDuration;
        private readonly HttpClient httpClient;
        private readonly Settings settings;
        private readonly HelperMethods helperMethods;
        private readonly IMemoryCache slackUsersCache;
        private readonly TimeSpan cacheItemLifetime;
        private readonly Dictionary<string, HashSet<string>> notifiedUserOfFailure;
        private Task checkTask;

        public AutopayProcessor(ILogger<AutopayProcessor> logger,
            ILogger<MongoDatabase> mongoDatabaseLogger,
            ILogger<VenmoApi> venmoApiLogger,
            Dictionary<string, SemaphoreSlim> slackApiRateLimits,
            Duration checkDuration,
            HttpClient httpClient,
            Settings settings,
            HelperMethods helperMethods,
            IMemoryCache slackUsersCache,
            TimeSpan cacheItemLifetime)
        {
            this.logger = logger;
            this.mongoDatabaseLogger = mongoDatabaseLogger;
            this.venmoApiLogger = venmoApiLogger;
            this.slackApiRateLimits = slackApiRateLimits;
            this.checkDuration = checkDuration;
            this.httpClient = httpClient;
            this.settings = settings;
            this.helperMethods = helperMethods;
            this.slackUsersCache = slackUsersCache;
            this.cacheItemLifetime = cacheItemLifetime;
            notifiedUserOfFailure = new Dictionary<string, HashSet<string>>();
            checkTask = CheckAutopayments();
            _ = CheckCheckAutopayments();
        }

        private async Task CheckAutopayments()
        {
            while (true)
            {
                foreach (var workspace in settings.SettingsObject.Workspaces.Workspaces)
                {
                    WorkspaceInfo workspaceInfo = workspace.Value.ToObject<WorkspaceInfo>()!;
                    MongoDatabase database = new MongoDatabase(workspace.Key, mongoDatabaseLogger);
                    SlackCore slackApi = new SlackCore(workspaceInfo.BotToken, httpClient, slackApiRateLimits);
                    var slackUsers = await helperMethods.GetCachedSlackUsers(workspaceInfo.BotToken, cacheItemLifetime,
                        slackApi, slackUsersCache);
                    List<Database.Models.VenmoUser> users = database.GetAllUsers();
                    foreach (var user in users)
                    {
                        if (user.Autopay != null && user.Autopay.Count > 0)
                        {
                            VenmoApi venmoApi = new VenmoApi(venmoApiLogger);
                            string? accessToken = await helperMethods.CheckIfVenmoAccessTokenIsExpired(user, venmoApi, database);
                            if (string.IsNullOrEmpty(accessToken))
                            {
                                logger.LogError($"Unable to refresh Venmo access token for {user.UserId}");
                                if (!notifiedUserOfFailure.ContainsKey(workspace.Key) || !notifiedUserOfFailure[workspace.Key].Contains(user.UserId))
                                {
                                    await WebhookController.SendSlackMessage(workspaceInfo,
                                        "Unable to process autopayments as your token has expired. Please refresh it.",
                                        user.UserId, httpClient);
                                }
                                if (!notifiedUserOfFailure.ContainsKey(workspace.Key))
                                {
                                    notifiedUserOfFailure.Add(workspace.Key, new HashSet<string>());
                                }
                                if (!notifiedUserOfFailure[workspace.Key].Contains(user.UserId))
                                {
                                    notifiedUserOfFailure[workspace.Key].Add(user.UserId);
                                }
                                continue;
                            }

                            venmoApi.AccessToken = accessToken;
                            string venmoId = (await venmoApi.GetMe()).Data!.User!.Id!;
                            venmoApi.UserId = venmoId;
                            List<VenmoPaymentPending> pendingPayments;
                            try
                            {
                                pendingPayments = await venmoApi.GetAllPayments();
                            }
                            catch (VenmoException ex)
                            {
                                logger.LogError(ex, $"Venmo exception when getting pending payments for {user.UserId}");
                                continue;
                            }

                            foreach (var payment in pendingPayments)
                            {
                                if (payment.Actor!.Id != venmoId && payment.Action == "charge")
                                {
                                    Autopay autopay = new Autopay(venmoApi, database);
                                    var autopayResponse = await autopay.CheckForAutopayment(payment, user);
                                    if (autopayResponse.autopaid)
                                    {
                                        await WebhookController.SendSlackMessage(workspaceInfo, autopayResponse.message!,
                                            user.UserId, httpClient);
                                    }
                                }
                            }
                        }
                    }
                }
                await Task.Delay(checkDuration.ToTimeSpan());
            }
        }

        private async Task CheckCheckAutopayments()
        {
            while (true)
            {
                if (checkTask.IsFaulted)
                {
                    logger.LogError("Check autopayments task has failed, restarting.");
                    if (checkTask.Exception != null)
                    {
                        foreach (var exception in checkTask.Exception.InnerExceptions)
                        {
                            logger.LogError(exception, "InnerException");
                        }
                    }
                    checkTask = CheckAutopayments();
                }
                await Task.Delay((int)checkDuration.TotalMilliseconds / 2);
            }
        }
    }
}
