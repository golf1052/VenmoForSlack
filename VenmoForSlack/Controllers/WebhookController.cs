using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using golf1052.SlackAPI;
using golf1052.SlackAPI.BlockKit.BlockElements;
using golf1052.SlackAPI.BlockKit.Blocks;
using golf1052.SlackAPI.BlockKit.CompositionObjects;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VenmoForSlack.Database;
using VenmoForSlack.Models;
using VenmoForSlack.Venmo;
using VenmoForSlack.Venmo.Models;

namespace VenmoForSlack.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class WebhookController : ControllerBase
    {
        private static JsonSerializerSettings blockKitSerializer;
        static WebhookController()
        {
            blockKitSerializer = new golf1052.SlackAPI.HelperMethods().GetBlockKitSerializer();
        }

        private readonly ILogger logger;
        private readonly HttpClient httpClient;
        private readonly HelperMethods helperMethods;
        private readonly ILogger<MongoDatabase> mongoDatabaseLogger;
        private readonly ILogger<VenmoApi> venmoApiLogger;

        public WebhookController(ILogger<WebhookController> logger,
            HttpClient httpClient,
            HelperMethods helperMethods,
            ILogger<MongoDatabase> mongoDatabaseLogger,
            ILogger<VenmoApi> venmoApiLogger)
        {
            this.logger = logger;
            this.httpClient = httpClient;
            this.helperMethods = helperMethods;
            this.mongoDatabaseLogger = mongoDatabaseLogger;
            this.venmoApiLogger = venmoApiLogger;
        }

        [HttpGet]
        public string HandleVenmoChallenge([FromQuery]string venmoChallenge)
        {
            return venmoChallenge;
        }

        [HttpPost]
        public async Task<string> HandleWebhook([FromBody] JObject data)
        {
            logger.LogInformation(data.ToString(Formatting.None));
            VenmoWebhookRequest request = data.ToObject<VenmoWebhookRequest>()!;
            string message = string.Empty;
            if (request.Type == "payment.created")
            {
                // Because Venmo webhooks don't have any info that we can use to match the webhook to a specific
                // user in a specific database get all matching databases so we can try and find the right
                // database for a user's autopayments later.
                var matchingDbs = GetMatchingDatabases(request.Data.Target.User.Id);
                if (matchingDbs == null)
                {
                    return "";
                }

                int matchingDbIndex = 0;
                var databaseInfo = matchingDbs[0];

                if (WebhookSeenBefore(databaseInfo.user, request.Data.Id, request.Data.Status))
                {
                    return "";
                }

                SaveWebhookId(databaseInfo.user, request.Data.Id, request.Data.Status, databaseInfo.database);
                SlackCore slackApi = new SlackCore(databaseInfo.workspaceInfo.BotToken);
                message += $"{request.Data.Actor.DisplayName} ({request.Data.Actor.Username}) ";

                if (request.Data.Action == "pay")
                {
                    message += "paid you ";
                }
                else if (request.Data.Action == "charge")
                {
                    message += "charged you ";
                }
                message += $"${request.Data.Amount:F2} for {request.Data.Note}";

                if (request.Data.Action == "pay")
                {
                    await SendSlackMessage(databaseInfo.workspaceInfo, message, databaseInfo.user.UserId, httpClient);
                }
                
                if (request.Data.Action == "charge")
                {
                    // Now look for the DB the user is in that has autopayments defined
                    (bool autopaid, string? autopayMessage)? autopayResponse = null;
                    foreach (var db in matchingDbs)
                    {
                        if (db.user.Autopay == null || db.user.Autopay.Count == 0)
                        {
                            continue;
                        }

                        VenmoApi? venmoApi = await GetVenmoApi(db.user,
                            db.workspaceInfo,
                            db.database);

                        if (venmoApi != null)
                        {
                            Autopay autopay = new Autopay(venmoApi, db.database);
                            autopayResponse = await autopay.CheckForAutopayment(request, db.user);
                            if (autopayResponse != null && autopayResponse.Value.autopaid)
                            {
                                // If autopayments are defined and an autopayment was successful assume this is the
                                // correct database
                                databaseInfo = db;
                                break;
                            }
                        }
                    }

                    var channels = await slackApi.ConversationsList(false, "im");
                    var userChannel = channels.FirstOrDefault(c => c.User == databaseInfo.user.UserId);

                    List<IBlock>? blocks = null;
                    if (!autopayResponse.HasValue || !autopayResponse.Value.autopaid)
                    {
                        if (autopayResponse.HasValue && !string.IsNullOrEmpty(autopayResponse.Value.autopayMessage))
                        {
                            if (userChannel != null)
                            {
                                await SendSlackMessage(databaseInfo.workspaceInfo,
                                    autopayResponse.Value.autopayMessage, userChannel.Id, httpClient);
                            }
                        }

                        message += $" | ID: {request.Data.Id}";
                        string acceptCommand = $"venmo complete accept {request.Data.Id}";
                        blocks = new List<IBlock>();
                        blocks.Add(new Section(TextObject.CreatePlainTextObject($"{message}\n/{acceptCommand}"), null, null,
                            new golf1052.SlackAPI.BlockKit.BlockElements.Image(request.Data.Actor.ProfilePictureUrl,
                                $"Venmo profile picture of {request.Data.Actor.DisplayName}")));
                        blocks.Add(new Actions(
                            new Button("Accept", "acceptButton", null, acceptCommand, null, null),
                            new Button("Reject", "rejectButton", null, $"venmo complete reject {request.Data.Id}", null, null)));
                        message += $"{message}\n/{acceptCommand}";

                        if (userChannel != null)
                        {
                            await SendSlackMessage(databaseInfo.workspaceInfo,
                                $"{message}\n/{acceptCommand}", blocks, userChannel.Id, httpClient);
                        }
                    }
                    else
                    {
                        if (userChannel != null)
                        {
                            await SendSlackMessage(databaseInfo.workspaceInfo,
                                message, userChannel.Id, httpClient);
                            if (!string.IsNullOrEmpty(autopayResponse.Value.autopayMessage))
                            {
                                await SendSlackMessage(databaseInfo.workspaceInfo,
                                    autopayResponse.Value.autopayMessage, userChannel.Id, httpClient);
                            }
                        }
                    }
                }
            }
            else if (request.Type == "payment.updated")
            {
                if (request.Data.Target.Type != "user")
                {
                    return "";
                }

                // When user charges someone else and their payment is completed the user is the actor.
                // When user is charged by someone else and their payment is completed the user is the target.
                var matchingDbs = GetMatchingDatabases(request.Data.Actor.Id);
                if (matchingDbs == null)
                {
                    return "";
                }

                var databaseInfo = matchingDbs[0];
                if (WebhookSeenBefore(databaseInfo.user, request.Data.Id, request.Data.Status))
                {
                    return "";
                }
                SaveWebhookId(databaseInfo.user, request.Data.Id, request.Data.Status, databaseInfo.database);
                message += $"{request.Data.Target.User.DisplayName} ({request.Data.Target.User.Username}) ";
                
                if (request.Data.Status == "settled")
                {
                    message += "accepted your ";
                }
                else if (request.Data.Status == "cancelled")
                {
                    message += "rejected your ";
                }
                message += $"${request.Data.Amount:F2} charge for {request.Data.Note}";
                await SendSlackMessage(databaseInfo.workspaceInfo, message, databaseInfo.user.UserId, httpClient);
            }
            return "";
        }

        public static async Task SendSlackMessage(WorkspaceInfo workspaceInfo,
            string message,
            string channel,
            HttpClient httpClient)
        {
            await SendSlackMessage(workspaceInfo, message, null, channel, httpClient);
        }

        public static async Task SendSlackMessage(WorkspaceInfo workspaceInfo,
            string text,
            List<IBlock>? blocks,
            string channel,
            HttpClient httpClient)
        {
            string botToken = workspaceInfo.BotToken;
            JObject o = new JObject();
            o["channel"] = channel;
            o["text"] = text;
            if (blocks != null)
            {
                o["blocks"] = JsonConvert.SerializeObject(blocks, Formatting.None, blockKitSerializer);
            }
            o["username"] = "Venmo";
            o["icon_url"] = "https://s3-us-west-2.amazonaws.com/slack-files2/avatars/2015-11-10/14228813844_49fae5f9cad227c8c1b5_72.jpg";
            HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Post, "https://slack.com/api/chat.postMessage");
            requestMessage.Content = new StringContent(o.ToString(Formatting.None), Encoding.UTF8, "application/json");
            requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", botToken);
            await httpClient.SendAsync(requestMessage);
        }

        private async Task<VenmoApi?> GetVenmoApi(Database.Models.VenmoUser venmoUser,
            WorkspaceInfo workspaceInfo,
            MongoDatabase database)
        {
            VenmoApi venmoApi = new VenmoApi(venmoApiLogger);
            string? accessToken = await helperMethods.CheckIfVenmoAccessTokenIsExpired(venmoUser, venmoApi, database);
            if (string.IsNullOrEmpty(accessToken))
            {
                logger.LogError($"Unable to refresh Venmo access token for {venmoUser.UserId}");
                await SendSlackMessage(workspaceInfo,
                    "Unable to complete autopayments as your Venmo token has expired. Please refresh it.",
                    venmoUser.UserId, httpClient);
                return null;
            }

            venmoApi.AccessToken = accessToken;
            return venmoApi;
        }

        private List<(MongoDatabase database, Database.Models.VenmoUser user, WorkspaceInfo workspaceInfo)>? GetMatchingDatabases(string venmoId)
        {
            List<(MongoDatabase database, Database.Models.VenmoUser user, WorkspaceInfo workspaceInfo)> matchingDbs =
                new List<(MongoDatabase database, Database.Models.VenmoUser user, WorkspaceInfo workspaceInfo)>();
            foreach (var workspace in Settings.SettingsObject.Workspaces.Workspaces)
            {
                WorkspaceInfo workspaceInfo = workspace.Value.ToObject<WorkspaceInfo>()!;
                MongoDatabase database = new MongoDatabase(workspace.Key, mongoDatabaseLogger);
                List<Database.Models.VenmoUser> users = database.GetAllUsers();
                foreach (var user in users)
                {
                    if (user.Venmo?.UserId == venmoId)
                    {
                        matchingDbs.Add((database, user, workspaceInfo));
                    }
                }
            }

            if (matchingDbs.Any())
            {
                return matchingDbs;
            }
            return null;
        }

        private bool WebhookSeenBefore(Database.Models.VenmoUser venmoUser, string webhookId, string status)
        {
            if (!string.IsNullOrEmpty(venmoUser.LastWebhook))
            {
                string lastWebhook = venmoUser.LastWebhook;
                if ($"{status}.{webhookId}" == lastWebhook)
                {
                    return true;
                }
            }
            return false;
        }

        private void SaveWebhookId(Database.Models.VenmoUser venmoUser, string webhookId, string status, MongoDatabase database)
        {
            // Need to save both the status and webhook id because the webhook id is the same between the payment.created and the payment.updated
            // which means if only the webhook id is stored when the payment is updated the notification will not be routed to the user because
            // the webhook id was seen during its creation.
            venmoUser.LastWebhook = $"{status}.{webhookId}";
            database.SaveUser(venmoUser);
        }
    }
}
