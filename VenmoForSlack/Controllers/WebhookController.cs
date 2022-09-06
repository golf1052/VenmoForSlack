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
        private readonly ILogger<MongoDatabase> mongoDatabaseLogger;

        public WebhookController(ILogger<WebhookController> logger,
            HttpClient httpClient,
            ILogger<MongoDatabase> mongoDatabaseLogger)
        {
            this.logger = logger;
            this.httpClient = httpClient;
            this.mongoDatabaseLogger = mongoDatabaseLogger;
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
                var databaseInfo = GetUserFromDatabases(request.Data.Target.User.Id);
                if (databaseInfo == null)
                {
                    return "";
                }

                if (WebhookSeenBefore(databaseInfo.Value.user, request.Data.Id, request.Data.Status))
                {
                    return "";
                }

                SaveWebhookId(databaseInfo.Value.user, request.Data.Id, request.Data.Status, databaseInfo.Value.database);
                SlackCore slackApi = new SlackCore(databaseInfo.Value.workspaceInfo.BotToken);
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
                    await SendSlackMessage(databaseInfo.Value.workspaceInfo, message, databaseInfo.Value.user.UserId, httpClient);
                }
                
                if (request.Data.Action == "charge")
                {
                    message += $" | ID: {request.Data.Id}";
                    string acceptCommand = $"venmo complete accept {request.Data.Id}";
                    List<IBlock> blocks = new List<IBlock>();
                    blocks.Add(new Section(TextObject.CreatePlainTextObject($"{message}\n/{acceptCommand}"), null, null,
                        new golf1052.SlackAPI.BlockKit.BlockElements.Image(request.Data.Actor.ProfilePictureUrl,
                            $"Venmo profile picture of {request.Data.Actor.DisplayName}")));
                    blocks.Add(new Actions(
                        new Button("Accept", "acceptButton", null, acceptCommand, null, null),
                        new Button("Reject", "rejectButton", null, $"venmo complete reject {request.Data.Id}", null, null)));

                    var channels = await slackApi.ConversationsList(false, "im");
                    var userChannel = channels.FirstOrDefault(c => c.User == databaseInfo.Value.user.UserId);
                    if (userChannel != null)
                    {
                        await SendSlackMessage(databaseInfo.Value.workspaceInfo, $"{message}\n/{acceptCommand}", blocks, userChannel.Id, httpClient);
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
                var databaseInfo = GetUserFromDatabases(request.Data.Actor.Id);
                if (databaseInfo == null)
                {
                    return "";
                }

                if (WebhookSeenBefore(databaseInfo.Value.user, request.Data.Id, request.Data.Status))
                {
                    return "";
                }
                SaveWebhookId(databaseInfo.Value.user, request.Data.Id, request.Data.Status, databaseInfo.Value.database);
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
                await SendSlackMessage(databaseInfo.Value.workspaceInfo, message, databaseInfo.Value.user.UserId, httpClient);
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

        private (MongoDatabase database, Database.Models.VenmoUser user, WorkspaceInfo workspaceInfo)? GetUserFromDatabases(string venmoId)
        {
            foreach (var workspace in Settings.SettingsObject.Workspaces.Workspaces)
            {
                WorkspaceInfo workspaceInfo = workspace.Value.ToObject<WorkspaceInfo>()!;
                MongoDatabase database = new MongoDatabase(workspace.Key, mongoDatabaseLogger);
                List<Database.Models.VenmoUser> users = database.GetAllUsers();
                foreach (var user in users)
                {
                    if (user.Venmo?.UserId == venmoId)
                    {
                        return (database, user, workspaceInfo);
                    }
                }
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
