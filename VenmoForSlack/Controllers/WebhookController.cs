using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VenmoForSlack.Database;
using VenmoForSlack.Database.Models;
using VenmoForSlack.Models;
using VenmoForSlack.Venmo.Models;

namespace VenmoForSlack.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class WebhookController : ControllerBase
    {
        private readonly ILogger logger;
        private readonly HttpClient httpClient;

        public WebhookController(ILogger<WebhookController> logger,
            HttpClient httpClient)
        {
            this.logger = logger;
            this.httpClient = httpClient;
        }

        [HttpGet]
        public string HandleVenmoChallenge([FromQuery]string venmoChallenge)
        {
            return venmoChallenge;
        }

        [HttpPost]
        public async Task<string> HandleWebhook([FromBody] JObject data)
        {
            VenmoWebhookRequest request = data.ToObject<VenmoWebhookRequest>()!;
            string message = string.Empty;
            if (request.Type == "payment.created")
            {
                var databaseInfo = GetUserFromDatabases(request.Data.Target.User.Id);
                if (databaseInfo == null)
                {
                    return "";
                }

                if (WebhookSeenBefore(databaseInfo.Value.user, request.Data.Id))
                {
                    return "";
                }
                SaveWebhookId(databaseInfo.Value.user, request.Data.Id, databaseInfo.Value.database);
                message += $"{request.Data.Actor.DisplayName} ";

                if (request.Data.Action == "pay")
                {
                    message += "paid you ";
                }
                else if (request.Data.Action == "charge")
                {
                    message += "charged you ";
                }
                message += $"${request.Data.Amount:F2} for {request.Data.Note}";

                if (request.Data.Action == "charge")
                {
                    message += $" | ID: {request.Data.Id}";
                }
                await SendSlackMessage(databaseInfo.Value.workspaceInfo, message, databaseInfo.Value.user.UserId);
                if (request.Data.Action == "charge")
                {
                    string acceptCommand = $"/venmo complete accept {request.Data.Id}";
                    await SendSlackMessage(databaseInfo.Value.workspaceInfo, acceptCommand, databaseInfo.Value.user.UserId);
                }
            }
            else if (request.Type == "payment.updated")
            {
                if (request.Data.Target.Type != "user")
                {
                    return "";
                }
                var databaseInfo = GetUserFromDatabases(request.Data.Target.User.Id);
                if (databaseInfo == null)
                {
                    return "";
                }

                if (WebhookSeenBefore(databaseInfo.Value.user, request.Data.Id))
                {
                    return "";
                }
                SaveWebhookId(databaseInfo.Value.user, request.Data.Id, databaseInfo.Value.database);
                message += $"{request.Data.Target.User.DisplayName} ";
                
                if (request.Data.Status == "settled")
                {
                    message += "accepted your ";
                }
                else if (request.Data.Status == "cancelled")
                {
                    message += "rejected your ";
                }
                message += $"${request.Data.Amount:F2} charge for {request.Data.Note}";
                await SendSlackMessage(databaseInfo.Value.workspaceInfo, message, databaseInfo.Value.user.UserId);
            }
            return "";
        }

        private async Task SendSlackMessage(WorkspaceInfo workspaceInfo, string message, string channel)
        {
            string botToken = workspaceInfo.BotToken;
            JObject o = new JObject();
            o["token"] = botToken;
            o["channel"] = channel;
            o["text"] = message;
            o["username"] = "Venmo";
            o["icon_url"] = "https://s3-us-west-2.amazonaws.com/slack-files2/avatars/2015-11-10/14228813844_49fae5f9cad227c8c1b5_72.jpg";
            await httpClient.PostAsync("https://slack.com/api/chat.postMessage",
                new StringContent(o.ToString(Formatting.None), Encoding.UTF8, "application/json"));
        }

        private (MongoDatabase database, Database.Models.VenmoUser user, WorkspaceInfo workspaceInfo)? GetUserFromDatabases(string venmoId)
        {
            foreach (var workspace in Settings.SettingsObject.Workspaces.Workspaces)
            {
                WorkspaceInfo workspaceInfo = workspace.Value.ToObject<WorkspaceInfo>()!;
                MongoDatabase database = new MongoDatabase(workspace.Key);
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

        private bool WebhookSeenBefore(Database.Models.VenmoUser venmoUser, string webhookId)
        {
            if (!string.IsNullOrEmpty(venmoUser.LastWebhook))
            {
                string lastWebhookId = venmoUser.LastWebhook;
                if (webhookId == lastWebhookId)
                {
                    return true;
                }
            }
            return false;
        }

        private void SaveWebhookId(Database.Models.VenmoUser venmoUser, string webhookId, MongoDatabase database)
        {
            venmoUser.LastWebhook = webhookId;
            database.SaveUser(venmoUser);
        }
    }
}
