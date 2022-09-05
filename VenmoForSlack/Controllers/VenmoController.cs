using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using golf1052.SlackAPI;
using golf1052.SlackAPI.BlockKit;
using golf1052.SlackAPI.BlockKit.BlockElements;
using golf1052.SlackAPI.BlockKit.Blocks;
using golf1052.SlackAPI.BlockKit.CompositionObjects;
using golf1052.SlackAPI.Events;
using golf1052.SlackAPI.Objects;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NodaTime;
using VenmoForSlack.Controllers.Models;
using VenmoForSlack.Database;
using VenmoForSlack.Database.Models;
using VenmoForSlack.Models;
using VenmoForSlack.Venmo;
using VenmoForSlack.Venmo.Models;
using VenmoForSlack.Venmo.Models.Responses;

namespace VenmoForSlack.Controllers
{
    [ApiController]
    [Route("/")]
    public class VenmoController : ControllerBase
    {
        private readonly ILogger logger;
        private readonly HttpClient httpClient;
        private readonly VenmoApi venmoApi;
        private readonly ILogger<YNABHandler> ynabHandlerLogger;
        private readonly IClock clock;
        private readonly HelperMethods helperMethods;
        private readonly ILogger<MongoDatabase> mongoDatabaseLogger;
        private readonly JsonSerializerSettings blockKitSerializer;
        private readonly JsonSerializer jsonSerializer;
        private readonly YNABHandler ynabHandler;

        public VenmoController(ILogger<VenmoController> logger,
            HttpClient httpClient,
            VenmoApi venmoApi,
            ILogger<YNABHandler> ynabHandlerLogger,
            IClock clock,
            HelperMethods helperMethods,
            ILogger<MongoDatabase> mongoDatabaseLogger)
        {
            this.logger = logger;
            this.httpClient = httpClient;
            this.venmoApi = venmoApi;
            this.ynabHandlerLogger = ynabHandlerLogger;
            this.clock = clock;
            this.helperMethods = helperMethods;
            this.mongoDatabaseLogger = mongoDatabaseLogger;
            blockKitSerializer = new golf1052.SlackAPI.HelperMethods().GetBlockKitSerializer();
            jsonSerializer = JsonSerializer.CreateDefault(blockKitSerializer);
            ynabHandler = new YNABHandler(ynabHandlerLogger, helperMethods);
        }

        [HttpGet]
        public ActionResult Get([FromQuery] string? code)
        {
            if (string.IsNullOrEmpty(code))
            {
                return File("index.html", "text/html");
            }
            else
            {
                return File("code.html", "text/html");
            }
        }

        [HttpPost("/events")]
        public async Task<string> HandleEvents()
        {
            string bodyString = string.Empty;
            using (System.IO.StreamReader reader = new System.IO.StreamReader(Request.Body))
            {
                bodyString = await reader.ReadToEndAsync();
            }
            JObject bodyObject = JObject.Parse(bodyString);
            if ((string)bodyObject["type"]! == UrlVerification.Type)
            {
                UrlVerification urlVerification = bodyObject.ToObject<UrlVerification>(jsonSerializer)!;
                return urlVerification.Challenge;
            }
            else
            {
                SlackEventWrapper eventWrapper = bodyObject.ToObject<SlackEventWrapper>(jsonSerializer)!;
                string? verifyEventRequest = VerifyRequest(eventWrapper.TeamId, eventWrapper.Token);
                if (!string.IsNullOrEmpty(verifyEventRequest))
                {
                    return "";
                }

                SlackCore slackApi = new SlackCore(GetWorkspaceInfo(eventWrapper.TeamId).BotToken);
                if ((string)eventWrapper.Event["type"]! == AppHomeOpened.Type)
                {
                    AppHomeOpened appHomeOpened = eventWrapper.Event.ToObject<AppHomeOpened>(jsonSerializer)!;
                    if (appHomeOpened.Tab == "home")
                    {
                        await PublishHomeTabView(appHomeOpened.User, slackApi);
                    }
                }
            }
            return "";
        }

        [HttpPost("/interactive")]
        public async Task HandleInteractiveRequest([FromForm]string payload)
        {
            BlockActionResponse payloadObject = JsonConvert.DeserializeObject<BlockActionResponse>(payload, blockKitSerializer)!;
            string? verifyRequestResponse = VerifyRequest(payloadObject.Team.Id!, payloadObject.Token);
            if (!string.IsNullOrEmpty(verifyRequestResponse))
            {
                _ = Respond(verifyRequestResponse, payloadObject.ResponseUrl);
                return;
            }

            SlackCore slackApi = new SlackCore(GetWorkspaceInfo(payloadObject.Team.Id!).BotToken);
            MongoDatabase database = GetTeamDatabase(payloadObject.Team.Id!, mongoDatabaseLogger);

            if (payloadObject.Actions.Count > 0)
            {
                string blockType = (string)payloadObject.Actions[0]["type"]!;
                if (blockType == "button")
                {
                    Button button = payloadObject.Actions[0].ToObject<Button>(jsonSerializer)!;
                    if (button.ActionId == "submitButton")
                    {
                        JObject valuesObject = (JObject)payloadObject.View.State["values"]!;
                        Dictionary<string, string?> values = new Dictionary<string, string?>();
                        foreach (var o in valuesObject)
                        {
                            if (o.Value != null && o.Value.HasValues)
                            {
                                JProperty property = (JProperty)o.Value.First!;
                                JObject propertyValue = (JObject)property.Value;
                                if ((string)propertyValue["type"]! == "radio_buttons")
                                {
                                    values.Add(property.Name, (string?)propertyValue["selected_option"]!["value"]);
                                }
                                else if ((string)propertyValue["type"]! == "plain_text_input")
                                {
                                    values.Add(property.Name, (string?)propertyValue["value"]);
                                }
                            }
                        }

                        List<string> errorList = new List<string>();
                        foreach (var value in values)
                        {
                            if (string.IsNullOrEmpty(value.Value))
                            {
                                if (value.Key == "amountInput")
                                {
                                    errorList.Add("Must include an amount.");
                                }
                                else if (value.Key == "noteInput")
                                {
                                    errorList.Add("Must include a note.");
                                }
                                else if (value.Key == "recipientsInput")
                                {
                                    errorList.Add("Must include at least one recipient.");
                                }
                            }
                        }

                        var channels = await slackApi.ConversationsList(false, "im");
                        string userChannel = channels.FirstOrDefault(c => c.User == payloadObject.User.Id)!.Id;
                        // Interactions from the "Home" tab don't have a responseUrl so to send any messages we need to
                        // use the chat.postMessage API.
                        Action<string, List<IBlock>?> respondAction = (text, blocks) =>
                        {
                            _ = slackApi.ChatPostMessage(text, userChannel, blocks: blocks);
                        };
                        if (errorList.Count > 0)
                        {
                            respondAction.Invoke(string.Join('\n', errorList), null);
                        }
                        else
                        {
                            string venmoString = $"venmo {values["audienceRadio"]} {values["typeRadio"]} {values["amountInput"]} for {values["noteInput"]} to {values["recipientsInput"]}";
                            _ = GetAccessTokenAndParseMessage(venmoString, payloadObject.User.Id, respondAction, database, slackApi);
                        }
                    }
                    else if (button.ActionId == "confirmYesButton")
                    {
                        string[] confirmationActions = button.Value.Split('\n');
                        var respondAction = CreateRespondAction(payloadObject.ResponseUrl);
                        foreach (var action in confirmationActions)
                        {
                            _ = GetAccessTokenAndParseMessage(action, payloadObject.User.Id, respondAction, database, slackApi);
                        }
                    }
                    else if (button.ActionId == "confirmNoButton")
                    {
                        _ = Respond($"Did not Venmo {button.Value}", payloadObject.ResponseUrl);
                    }
                    else
                    {
                        _ = GetAccessTokenAndParseMessage(button.Value, payloadObject.User.Id, CreateRespondAction(payloadObject.ResponseUrl), database, slackApi);
                    }
                }
            }
        }

        [HttpPost]
        public async Task<string> HandleRequest([FromForm] SlackRequest body)
        {
            if (string.IsNullOrEmpty(body.TeamId))
            {
                logger.LogWarning($"Unknown request.\n" +
                    $"Remote IP: {Request.HttpContext.Connection.RemoteIpAddress}\n" +
                    $"Remote Port: {Request.HttpContext.Connection.RemotePort}");
                Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return string.Empty;
            }
            string? verifyRequestResponse = VerifyRequest(body.TeamId!, body.Token!);
            if (!string.IsNullOrEmpty(verifyRequestResponse))
            {
                return verifyRequestResponse;
            }

            SlackCore slackApi = new SlackCore(GetWorkspaceInfo(body.TeamId!).BotToken);

            MongoDatabase database = GetTeamDatabase(body.TeamId!, mongoDatabaseLogger);

            string requestText = ProcessRequestText(body.Text);
            if (string.IsNullOrEmpty(requestText))
            {
                string? token = await GetAccessToken(body.UserId!, database);
                if (string.IsNullOrEmpty(token))
                {
                    _ = RequestAuth(body.ResponseUrl!);
                    return "";
                }
                else
                {
                    return VenmoHelp.HelpMessage;
                }
            }

            string? unauthResponse = await ParseUnauthenticatedMessage(requestText, body.UserId!, CreateRespondAction(body.ResponseUrl!), database);
            if (unauthResponse == string.Empty)
            {
                return string.Empty;
            }

            _ = GetAccessTokenAndParseMessage($"venmo {requestText}", body.UserId!, CreateRespondAction(body.ResponseUrl!), database, slackApi);
            return string.Empty;
        }

        private string ProcessRequestText(string? text)
        {
            // If the request is sent on mobile (at least the iOS app) body.Text will be of the form: "/venmo balance"
            // On desktop it's just "balance". The desktop form is documented and was the original way it worked.
            // The mobile difference is undocumented and annoying.

            // Also the desktop client will trim body.Text, mobile will send body.Text as is
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            else
            {
                text = text.Trim();
                if (text.StartsWith('/'))
                {
                    return text.Substring(7);
                }
                else
                {
                    return text;
                }
            }
        }

        private string? VerifyRequest(string teamId, string token)
        {
            if (!Settings.SettingsObject.Workspaces.Workspaces.ContainsKey(teamId))
            {
                logger.LogInformation($"Unknown team: {teamId}");
                return "Team not configured to use Venmo";
            }

            WorkspaceInfo workspaceInfo = GetWorkspaceInfo(teamId);
            if (workspaceInfo!.Token != token)
            {
                logger.LogInformation($"Unknown token for team {teamId}: {token}");
                return "Team verification token mismatch";
            }

            return null;
        }

        private WorkspaceInfo GetWorkspaceInfo(string teamId)
        {
            return Settings.SettingsObject.Workspaces.Workspaces[teamId].ToObject<WorkspaceInfo>()!;
        }

        private MongoDatabase GetTeamDatabase(string teamId, ILogger<MongoDatabase> logger)
        {
            return new MongoDatabase(teamId, logger);
        }

        private async Task GetAccessTokenAndParseMessage(string text, string userId, Action<string, List<IBlock>?> respondAction, MongoDatabase database, SlackCore slackApi)
        {
            string? accessToken = await GetAccessToken(userId, database);
            if (string.IsNullOrEmpty(accessToken))
            {
                logger.LogError($"Couldn't refresh Venmo access token for {userId}");
                RequestAuth(respondAction);
            }
            else
            {
                venmoApi.AccessToken = accessToken;
                _ = ParseMessage(text, userId, respondAction, database, slackApi);
            }
        }

        private async Task<string?> GetAccessToken(string userId, MongoDatabase database)
        {
            Database.Models.VenmoUser? venmoUser = database.GetUser(userId);
            if (venmoUser == null)
            {
                venmoUser = new Database.Models.VenmoUser()
                {
                    UserId = userId
                };
            }

            if (venmoUser.Venmo == null || string.IsNullOrEmpty(venmoUser.Venmo.AccessToken))
            {
                venmoUser.Venmo = new VenmoAuthObject()
                {
                    AccessToken = "",
                    ExpiresIn = "",
                    RefreshToken = "",
                    UserId = "",
                    DeviceId = "",
                    OtpSecret = ""
                };
                database.SaveUser(venmoUser);
                return null;
            }

            return await helperMethods.CheckIfVenmoAccessTokenIsExpired(venmoUser, venmoApi, database);
        }

        private async Task ParseMessage(string message,
            string userId,
            Action<string, List<IBlock>?> respondAction,
            MongoDatabase database,
            SlackCore slackApi)
        {
            Database.Models.VenmoUser venmoUser = database.GetUser(userId)!;
            string[] splitMessage = message.Split(' ');
            if (splitMessage.Length == 1)
            {
                respondAction.Invoke(VenmoHelp.HelpMessage, null);
            }
            else if (splitMessage[1].ToLower() == "help")
            {
                respondAction.Invoke(VenmoHelp.HelpMessage, null);
            }
            else if (splitMessage[1].ToLower() == "last")
            {
                GetLastMessage(venmoUser, respondAction);
            }
            else if (splitMessage[1].ToLower() == "code")
            {
                respondAction("code is no longer supported. Please use /venmo auth instead", null);
            }
            else if (splitMessage[1].ToLower() == "create")
            {
                await PublishHomeTabView(userId, slackApi);
            }
            else if (splitMessage[1].ToLower() == "delete")
            {
                if (splitMessage.Length == 2)
                {
                    venmoUser.Venmo = new VenmoAuthObject()
                    {
                        AccessToken = "",
                        ExpiresIn = "",
                        RefreshToken = "",
                        UserId = ""
                    };
                    database.SaveUser(venmoUser);
                    respondAction.Invoke("Deleted Venmo authentication information from database. If you want to delete all of your user information from the database run /venmo delete everything", null);
                }
                else if (splitMessage.Length == 3 && splitMessage[2].ToLower() == "everything")
                {
                    bool? deleteResult = database.DeleteUser(userId);
                    if (deleteResult == null)
                    {
                        respondAction.Invoke("Error while deleting you from the database. Ask Sanders to investigate.", null);
                    }
                    else if (!deleteResult.Value)
                    {
                        respondAction.Invoke("It looks like you're not in the database? Ask Sanders to investigate.", null);
                    }
                    else
                    {
                        respondAction.Invoke("Deleted all of your information from the database.", null);
                    }
                }
            }
            else if (splitMessage[1].ToLower() == "ynab")
            {
                SaveLastMessage(message, venmoUser, database);
                await ynabHandler.ParseMessage(splitMessage, userId, respondAction, database);
            }
            else
            {
                SaveLastMessage(message, venmoUser, database);
                if (splitMessage[1].ToLower() == "balance")
                {
                    await GetVenmoBalance(respondAction);
                }
                else if (splitMessage[1].ToLower() == "pending")
                {
                    if (splitMessage.Length == 2)
                    {
                        await GetVenmoPending("incoming", await GetVenmoUserId(), respondAction);
                    }
                    else if (splitMessage.Length == 3)
                    {
                        string which = splitMessage[2].ToLower();
                        if (which == "incoming" || which == "outgoing")
                        {
                            await GetVenmoPending(which, await GetVenmoUserId(), respondAction);
                        }
                        else
                        {
                            respondAction.Invoke("Valid pending commands\npending\npending incoming\npending outgoing", null);
                        }
                    }
                    else
                    {
                        respondAction.Invoke("Valid pending commands\npending\npending incoming\npending outgoing", null);
                    }
                }
                else if (splitMessage[1].ToLower() == "search")
                {
                    if (splitMessage.Length == 2)
                    {
                        respondAction.Invoke("User search requires a search query", null);
                    }
                    else
                    {
                        string searchQuery = string.Join(' ', splitMessage, 2, splitMessage.Length - 2);
                        VenmoUserSearchResponse response;
                        try
                        {
                            response = await venmoApi.SearchUsers(searchQuery);
                        }
                        catch (VenmoException ex)
                        {
                            logger.LogWarning(ex, $"Exception while searching users. Search query: {searchQuery}");
                            respondAction.Invoke(ex.Message, null);
                            return;
                        }
                        List<string> responseLines = new List<string>();
                        List<IBlock> sections = new List<IBlock>();
                        Section userCountSection;
                        string userCountText;
                        if (response.Data!.Count >= 50)
                        {
                            userCountText = "Found more than 50 users";
                        }
                        else if (response.Data.Count == 1)
                        {
                            userCountText = $"Found {response.Data.Count} user";
                        }
                        else
                        {
                            userCountText = $"Found {response.Data.Count} users";
                        }
                        userCountSection = new Section(userCountText);
                        responseLines.Add(userCountText);
                        sections.Add(userCountSection);

                        foreach (var user in response.Data.Take(10))
                        {
                            string text = $"{user.DisplayName} ({user.Username})\nID: {user.Id}";
                            Section section = new Section(TextObject.CreatePlainTextObject(text),
                                null, null, helperMethods.GetVenmoUserProfileImage(user));
                            responseLines.Add(text);
                            sections.Add(section);
                        }

                        respondAction.Invoke(string.Join('\n', responseLines), sections);
                    }
                }
                else if (splitMessage[1].ToLower() == "accept" ||
                    splitMessage[1].ToLower() == "reject" ||
                    splitMessage[1].ToLower() == "cancel")
                {
                    if (splitMessage.Length == 3)
                    {
                        respondAction.Invoke($"You probably meant: /venmo complete {splitMessage[1].ToLower()} {splitMessage[2].ToLower()}", null);
                    }
                }
                else if (splitMessage[1].ToLower() == "complete")
                {
                    if (splitMessage.Length >= 4)
                    {
                        string which = splitMessage[2].ToLower();
                        if (which == "accept" || which == "reject" || which == "cancel")
                        {
                            if (splitMessage[3].ToLower() == "all")
                            {
                                await VenmoCompleteAll(which, await GetVenmoUserId(), respondAction);
                            }
                            else
                            {
                                List<string> completionNumbers = splitMessage[3..].ToList();
                                await VenmoComplete(which, completionNumbers, await GetVenmoUserId(), respondAction);
                            }
                        }
                        else
                        {
                            respondAction.Invoke("Valid complete commands\nvenmo complete accept #\nvenmo complete reject #\nvenmo complete cancel #", null);
                        }
                    }
                    else
                    {
                        respondAction.Invoke("Valid complete commands\nvenmo complete accept #\nvenmo complete reject #\nvenmo complete cancel #", null);
                    }
                }
                else if (splitMessage[1].ToLower() == "alias")
                {
                    if (splitMessage.Length == 4)
                    {
                        if (splitMessage[2].ToLower() == "delete")
                        {
                            DeleteAlias(venmoUser, splitMessage[3].ToLower(), respondAction, database);
                        }
                        else
                        {
                            string friendUsername = splitMessage[2];
                            string alias = splitMessage[3].ToLower();
                            await AliasUser(venmoUser, friendUsername, alias, respondAction, database);
                        }
                    }
                    else if (splitMessage.Length == 3 && splitMessage[2].ToLower() == "list")
                    {
                        ListAliases(venmoUser, respondAction);
                    }
                    else
                    {
                        respondAction.Invoke("Invalid alias command, your alias probably has a space in it.", null);
                    }
                }
                else if (splitMessage[1].ToLower() == "cache")
                {
                    if (splitMessage.Length == 4)
                    {
                        helperMethods.AddUsernameToCache(splitMessage[2], splitMessage[3], venmoUser, database);
                    }
                }
                else if (splitMessage[1].ToLower() == "history")
                {
                    await GetVenmoHistory(respondAction);
                }
                else if (splitMessage.Length <= 2)
                {
                    respondAction.Invoke("Invalid payment string", null);
                }
                else if (splitMessage[1].ToLower() == "charge" ||
                    splitMessage[2].ToLower() == "charge" ||
                    splitMessage[1].ToLower() == "pay" ||
                    splitMessage[2].ToLower() == "pay")
                {
                    ParsedVenmoPayment parsedVenmoPayment;
                    try
                    {
                        parsedVenmoPayment = helperMethods.ParseVenmoPaymentMessage(splitMessage);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, $"Unable to parse message. Message: {string.Join(' ', splitMessage)}");
                        respondAction.Invoke(ex.Message, null);
                        return;
                    }

                    var response = await helperMethods.VenmoPayment(venmoApi, venmoUser, database, parsedVenmoPayment.Amount,
                        parsedVenmoPayment.Note, parsedVenmoPayment.Recipients, parsedVenmoPayment.Action,
                        parsedVenmoPayment.Audience);
                    
                    foreach (var r in response.responses)
                    {
                        if (!string.IsNullOrEmpty(r.Error))
                        {
                            respondAction.Invoke($"Venmo error: {r.Error}", null);
                            continue;
                        }

                        if (parsedVenmoPayment.Action == VenmoAction.Charge)
                        {
                            string responseText = $"Successfully charged {r.Data!.Payment.Target!.User.DisplayName} ({r.Data!.Payment.Target.User.Username}) ${r.Data.Payment.Amount} for {r.Data.Payment.Note}. Audience is {r.Data.Payment.Audience}";
                            List<IBlock> blocks = new List<IBlock>()
                            {
                                new Section(TextObject.CreatePlainTextObject(responseText), null, null,
                                    helperMethods.GetVenmoUserProfileImage(r.Data!.Payment.Target.User)),
                                new Actions(new Button("Cancel", "cancelButton", null, $"venmo complete cancel {r.Data.Payment.Id}", null, null))
                            };
                            respondAction.Invoke(responseText, blocks);
                        }
                        else if (parsedVenmoPayment.Action == VenmoAction.Pay)
                        {
                            respondAction.Invoke($"Successfully paid {r.Data!.Payment.Target!.User.DisplayName} ({r.Data!.Payment.Target.User.Username}) ${r.Data.Payment.Amount} for {r.Data.Payment.Note}. Audience is {r.Data.Payment.Audience}", null);
                        }
                        else
                        {
                            respondAction.Invoke($"Successfully ??? {r.Data!.Payment.Target!.User.DisplayName} ({r.Data!.Payment.Target.User.Username}) ${r.Data.Payment.Amount} for {r.Data.Payment.Note}. Audience is {r.Data.Payment.Audience}", null);
                        }
                    }

                    var searchResponse = await helperMethods.ProcessUnknownRecipients(response.unprocessedRecipients, venmoApi, venmoUser, database);
                    foreach (var foundUser in searchResponse.foundUsers)
                    {
                        string confirmationText = $"You are not friends with {foundUser.DisplayName} ({foundUser.Username}). Are you sure you want to Venmo them?";
                        List<IBlock> blocks = new List<IBlock>();
                        blocks.Add(new Section(TextObject.CreatePlainTextObject(confirmationText), null, null, helperMethods.GetVenmoUserProfileImage(foundUser)));
                        string confirmationActions = $"venmo cache {foundUser.Username} {foundUser.Id}\n" +
                            $"venmo {VenmoAudienceHelperMethods.ToString(parsedVenmoPayment.Audience)} {VenmoActionHelperMethods.ToString(parsedVenmoPayment.Action)} {parsedVenmoPayment.Amount:F2} for {parsedVenmoPayment.Note} to user_id:{foundUser.Id}";
                        blocks.Add(new Actions(new Button("Yes", "confirmYesButton", null, confirmationActions, null, null),
                            new Button("No", "confirmNoButton", null, $"{foundUser.DisplayName} ({foundUser.Username})", null, null)));
                        respondAction.Invoke(confirmationText, blocks);
                    }

                    foreach (var u in searchResponse.unprocessedRecipients)
                    {
                        respondAction.Invoke($"Could not find Venmo user with username {u}.", null);
                    }
                }
                else if (splitMessage[1].ToLower() == "schedule")
                {
                    var slackUsers = await slackApi.UsersList();
                    SlackUser? slackUser = helperMethods.GetSlackUser(venmoUser.UserId, slackUsers);
                    if (slackUser == null)
                    {
                        logger.LogError($"While trying to get slack user timezone they disappeared? {venmoUser.UserId}");
                        respondAction.Invoke("While trying to get your timezone you disappeared?", null);
                        return;
                    }
                    ParseScheduleMessage(splitMessage, slackUser.TimeZone, venmoUser, database, respondAction);
                }
            }
        }

        private async Task<string?> ParseUnauthenticatedMessage(string message,
            string userId,
            Action<string, List<IBlock>?> respondAction,
            MongoDatabase database)
        {
            string[] splitMessage = message.Split(' ');
            if (splitMessage.Length > 0)
            {
                if (splitMessage[0].ToLower() == "auth")
                {
                    Database.Models.VenmoUser? venmoUser = database.GetUser(userId);
                    if (venmoUser == null)
                    {
                        venmoUser = new Database.Models.VenmoUser()
                        {
                            UserId = userId,
                            Venmo = new VenmoAuthObject()
                            {
                                DeviceId = Guid.NewGuid().ToString()
                            }
                        };
                        database.SaveUser(venmoUser);
                    }
                    else
                    {
                        if (venmoUser.Venmo == null)
                        {
                            venmoUser.Venmo = new VenmoAuthObject()
                            {
                                DeviceId = Guid.NewGuid().ToString()
                            };
                            database.SaveUser(venmoUser);
                        }
                        else
                        {
                            if (string.IsNullOrEmpty(venmoUser.Venmo.DeviceId))
                            {
                                venmoUser.Venmo.DeviceId = Guid.NewGuid().ToString();
                                database.SaveUser(venmoUser);
                            }
                        }
                    }

                    string username = splitMessage[1];
                    string password = string.Join(' ', splitMessage[2..]);
                    VenmoAuthResponse? response;
                    try
                    {
                        response = await venmoApi.AuthorizeWithUsernameAndPassword(username, password, venmoUser.Venmo!.DeviceId!);
                    }
                    catch (VenmoException ex)
                    {
                        if (!string.IsNullOrEmpty(ex.VenmoOtpSecret))
                        {
                            venmoUser.Venmo.OtpSecret = ex.VenmoOtpSecret;
                            database.SaveUser(venmoUser);
                            respondAction("2FA required. Please enter the 2FA code sent to your phone in this format `/venmo otp <CODE>`", null);
                        }
                        else
                        {
                            respondAction("Venmo OTP secret not found.", null);
                        }
                        return string.Empty;
                    }

                    if (response != null)
                    {
                        venmoUser.Venmo.AccessToken = response.AccessToken;
                        venmoUser.Venmo.UserId = response.User!.Id;
                        venmoUser.Venmo.OtpSecret = null;
                        database.SaveUser(venmoUser);
                        respondAction("Authenication complete", null);
                    }
                }
                else if (splitMessage[0].ToLower() == "otp")
                {
                    Database.Models.VenmoUser? venmoUser = database.GetUser(userId);
                    if (venmoUser == null)
                    {
                        respondAction("Maybe you forgot to try and authenticate first? `/venmo auth _username_ _password_`", null);
                        return string.Empty;
                    }
                    string otp = splitMessage[1];
                    VenmoAuthResponse response;
                    try
                    {
                        response = await venmoApi.AuthorizeWith2FA(otp, venmoUser.Venmo!.OtpSecret!, venmoUser.Venmo!.DeviceId!);
                    }
                    catch (VenmoException ex)
                    {
                        throw ex;
                    }

                    venmoUser.Venmo.AccessToken = response.AccessToken;
                    venmoUser.Venmo.UserId = response.User!.Id;
                    venmoUser.Venmo.OtpSecret = null;
                    database.SaveUser(venmoUser);
                    respondAction("Authentication complete", null);
                    return string.Empty;
                }
                else if (splitMessage[0].ToLower() == "code")
                {
                    respondAction("code is no longer supported. Please use /venmo auth instead", null);
                    return string.Empty;
                }
            }
            return null;
        }

        private async Task GetVenmoHistory(Action<string, List<IBlock>?> respondAction)
        {
            VenmoTransactionResponse response = await venmoApi.GetTransactions();
            string historyMessage = string.Empty;

            if (response.Data == null)
            {
                respondAction.Invoke("No transaction history.", null);
                return;
            }

            foreach (VenmoTransaction transaction in response.Data)
            {
                if (transaction.Type == "payment")
                {
                    string paymentMessage = string.Empty;
                    if (transaction.Payment!.Action == "pay")
                    {
                        if (transaction.Payment.Target!.User.Id == venmoApi.UserId)
                        {
                            // Someone else paid user
                            paymentMessage += $"{transaction.Payment.Actor!.DisplayName} ({transaction.Payment.Actor.Username}) paid you ";
                        }
                        else if (transaction.Payment.Actor!.Id == venmoApi.UserId)
                        {
                            // User paid someone else
                            paymentMessage += $"You paid {transaction.Payment.Target.User.DisplayName} ({transaction.Payment.Target.User.Username}) ";
                        }
                    }
                    else if (transaction.Payment.Action == "charge")
                    {
                        if (transaction.Payment.Target!.User.Id == venmoApi.UserId)
                        {
                            // Someone else charged user
                            paymentMessage += $"{transaction.Payment.Actor!.DisplayName} ({transaction.Payment.Actor.Username}) charged you ";
                        }
                        else if (transaction.Payment.Actor!.Id == venmoApi.UserId)
                        {
                            // User charged someone else
                            paymentMessage += $"You charged {transaction.Payment.Target.User.DisplayName} ({transaction.Payment.Target.User.Username}) ";
                        }
                    }

                    // Add Z to DateCreated to show that it's UTC
                    paymentMessage += $"${transaction.Payment.Amount:F2} for {transaction.Payment.Note} on {transaction.DateCreated}Z";

                    historyMessage += $"{paymentMessage}\n";
                }
                else if (transaction.Type == "transfer")
                {
                    historyMessage += $"Transferred ${transaction.Transfer!.Amount} to {transaction.Transfer.Destination!.Name} (last four digits: {transaction.Transfer.Destination.LastFour}) on {transaction.DateCreated}Z\n";
                }
                else
                {
                    historyMessage += $"Unknown transaction type ({transaction.Type})! Ask Sanders to investigate.\n";
                }
            }

            respondAction.Invoke(historyMessage, null);
        }

        private async Task<string> GetVenmoUserId()
        {
            if (string.IsNullOrEmpty(venmoApi.UserId))
            {
                MeResponse me = await venmoApi.GetMe();
                string venmoId = me.Data!.User.Id;
                venmoApi.UserId = venmoId;
            }
            return venmoApi.UserId;
        }

        private async Task<List<string>> ProcessVenmoPayments(
            VenmoApi venmoApi, Database.Models.VenmoUser venmoUser, MongoDatabase database,
            ParsedVenmoPayment parsedVenmoPayment, Action<string, List<IBlock>?> respondAction)
        {
            var response = await helperMethods.VenmoPayment(venmoApi, venmoUser, database, parsedVenmoPayment.Amount,
                        parsedVenmoPayment.Note, parsedVenmoPayment.Recipients, parsedVenmoPayment.Action,
                        parsedVenmoPayment.Audience);

            foreach (var r in response.responses)
            {
                if (!string.IsNullOrEmpty(r.Error))
                {
                    respondAction.Invoke($"Venmo error: {r.Error}", null);
                    continue;
                }

                if (parsedVenmoPayment.Action == VenmoAction.Charge)
                {
                    string responseText = $"Successfully charged {r.Data!.Payment.Target!.User.DisplayName} ({r.Data!.Payment.Target.User.Username}) ${r.Data.Payment.Amount} for {r.Data.Payment.Note}. Audience is {r.Data.Payment.Audience}";
                    List<IBlock> blocks = new List<IBlock>()
                            {
                                new Section(TextObject.CreatePlainTextObject(responseText), null, null,
                                    helperMethods.GetVenmoUserProfileImage(r.Data!.Payment.Target.User)),
                                new Actions(new Button("Cancel", "cancelButton", null, $"venmo complete cancel {r.Data.Payment.Id}", null, null))
                            };
                    respondAction.Invoke(responseText, blocks);
                }
                else if (parsedVenmoPayment.Action == VenmoAction.Pay)
                {
                    respondAction.Invoke($"Successfully paid {r.Data!.Payment.Target!.User.DisplayName} ({r.Data!.Payment.Target.User.Username}) ${r.Data.Payment.Amount} for {r.Data.Payment.Note}. Audience is {r.Data.Payment.Audience}", null);
                }
                else
                {
                    respondAction.Invoke($"Successfully ??? {r.Data!.Payment.Target!.User.DisplayName} ({r.Data!.Payment.Target.User.Username}) ${r.Data.Payment.Amount} for {r.Data.Payment.Note}. Audience is {r.Data.Payment.Audience}", null);
                }
            }
            return response.unprocessedRecipients;
        }

        private void ParseScheduleMessage(string[] splitMessage,
            string userTimeZone,
            Database.Models.VenmoUser venmoUser,
            MongoDatabase database,
            Action<string, List<IBlock>?> respondAction)
        {
            if (splitMessage[2].ToLower() == "list")
            {
                if (venmoUser.Schedule == null || venmoUser.Schedule.Count == 0)
                {
                    respondAction.Invoke("You have no scheduled Venmos.", null);
                    return;
                }

                for (int i = 0; i < venmoUser.Schedule.Count; i++)
                {
                    VenmoSchedule schedule = venmoUser.Schedule[i];
                    ParsedVenmoPayment paymentInfo;
                    try
                    {
                        paymentInfo = helperMethods.ParseVenmoPaymentMessage(helperMethods.ConvertScheduleMessageIntoPaymentMessage(schedule.Command.Split(' ')));
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, $"Unable to parse saved schedule command {schedule.Command}");
                        continue;
                    }

                    ZonedDateTime nextExecutionInTimeZone = Instant.FromDateTimeUtc(schedule.NextExecution)
                        .InZone(DateTimeZoneProviders.Tzdb[userTimeZone]);

                    string scheduleVerb;
                    string processedString;
                    if (schedule.Verb == "every")
                    {
                        scheduleVerb = "Recurring";
                        processedString = "next processed";
                    }
                    else if (schedule.Verb == "at" || schedule.Verb == "on")
                    {
                        scheduleVerb = "One-time";
                        processedString = "processed";
                    }
                    else
                    {
                        scheduleVerb = "???";
                        processedString = "???";
                    }

                    string actionString;
                    if (paymentInfo.Action == VenmoAction.Charge)
                    {
                        actionString = "charge";
                    }
                    else if (paymentInfo.Action == VenmoAction.Pay)
                    {
                        actionString = "payment";
                    }
                    else
                    {
                        actionString = "???";
                    }

                    respondAction.Invoke($"{i + 1}: {scheduleVerb} {VenmoAudienceHelperMethods.ToString(paymentInfo.Audience)} " +
                        $"{actionString} of ${paymentInfo.Amount} for {paymentInfo.Note} to " +
                        $"{string.Join(' ', paymentInfo.Recipients)}. This will be {processedString} at " +
                        $"{nextExecutionInTimeZone.GetFriendlyZonedDateTimeString()}.", null);
                }
                return;
            }

            if (splitMessage[2].ToLower() == "delete")
            {
                if (venmoUser.Schedule == null || venmoUser.Schedule.Count == 0)
                {
                    respondAction.Invoke("You have no scheduled Venmos.", null);
                    return;
                }

                if (splitMessage.Length != 4)
                {
                    respondAction.Invoke("Incorrect schedule delete message. Expected /venmo schedule delete ###", null);
                    return;
                }

                if (int.TryParse(splitMessage[3], out int number))
                {
                    if (number > venmoUser.Schedule.Count || number < 1)
                    {
                        if (venmoUser.Schedule.Count == 1)
                        {
                            respondAction.Invoke($"Not a valid schedule number, you only have {venmoUser.Schedule.Count} scheduled item.", null);
                        }
                        else
                        {
                            respondAction.Invoke($"Not a valid schedule number, you only have {venmoUser.Schedule.Count} scheduled items.", null);
                        }
                        return;
                    }

                    string commandToRemove = venmoUser.Schedule[number - 1].Command;
                    venmoUser.Schedule.RemoveAt(number - 1);
                    database.SaveUser(venmoUser);
                    respondAction.Invoke($"Removed /{commandToRemove}", null);
                }
                else
                {
                    respondAction.Invoke($"Expected schedule number to delete. Got {splitMessage[3]} instead.", null);
                }
                return;
            }

            ZonedDateTime scheduledTime;
            try
            {
                scheduledTime = helperMethods.ConvertScheduleMessageIntoDateTime(splitMessage, userTimeZone, clock);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, $"{string.Join(' ', splitMessage)}");
                respondAction.Invoke(ex.Message, null);
                return;
            }

            string[] newSplitMessage = helperMethods.ConvertScheduleMessageIntoPaymentMessage(splitMessage);
            ParsedVenmoPayment parsedVenmoPayment;
            try
            {
                parsedVenmoPayment = helperMethods.ParseVenmoPaymentMessage(newSplitMessage);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, $"{string.Join(' ', splitMessage)}");
                respondAction.Invoke(ex.Message, null);
                return;
            }

            if (venmoUser.Schedule == null)
            {
                venmoUser.Schedule = new List<VenmoSchedule>();
            }

            venmoUser.Schedule.Add(new VenmoSchedule()
            {
                Verb = helperMethods.GetScheduleMessageVerb(splitMessage),
                NextExecution = scheduledTime.ToDateTimeUtc(),
                Command = string.Join(' ', splitMessage)
            });

            database.SaveUser(venmoUser);
            respondAction.Invoke($"Scheduled Venmo set! Next execution: {scheduledTime.GetFriendlyZonedDateTimeString()}.", null);
        }

        private async Task AliasUser(Database.Models.VenmoUser venmoUser,
            string username,
            string alias,
            Action<string, List<IBlock>?> respondAction,
            MongoDatabase database)
        {
            List<Venmo.Models.VenmoUser> friends = await venmoApi.GetAllFriends();
            string? friendId = VenmoApi.FindFriend(username, friends);
            if (friendId == null)
            {
                var searchResponse = await helperMethods.ProcessUnknownRecipients(new List<string>() { username }, venmoApi,
                venmoUser, database);
                foreach (var foundUser in searchResponse.foundUsers)
                {
                    if (username.ToLower() == foundUser.Username.ToLower())
                    {
                        friendId = foundUser.Id;
                        break;
                    }
                }
            }

            if (friendId == null)
            {
                respondAction.Invoke($"Could not find {username}", null);
                return;
            }

            BsonDocument aliasDoc = new BsonDocument()
            {
                { "username", username },
                { "id", friendId }
            };
            if (venmoUser.Alias == null)
            {
                venmoUser.Alias = new BsonDocument();
            }
            venmoUser.Alias.Add(new BsonElement(alias, aliasDoc));
            database.SaveUser(venmoUser);
            respondAction.Invoke($"Alias set! ({alias} points to {username})", null);
        }

        private void ListAliases(Database.Models.VenmoUser venmoUser,
            Action<string, List<IBlock>?> respondAction)
        {
            if (venmoUser.Alias != null)
            {
                List<string> aliasList = new List<string>();
                foreach (var alias in venmoUser.Alias.Elements)
                {
                    aliasList.Add($"{alias.Name} points to {alias.Value.AsBsonDocument.GetElement("username").Value.AsString}");
                }
                respondAction.Invoke(string.Join('\n', aliasList), null);
            }
            else
            {
                respondAction.Invoke("You have no aliases set.", null);
            }
        }

        private void DeleteAlias(Database.Models.VenmoUser venmoUser,
            string alias,
            Action<string, List<IBlock>?> respondAction,
            MongoDatabase database)
        {
            VenmoAlias? venmoAlias = venmoUser.GetAlias(alias);
            if (venmoAlias != null)
            {
                venmoUser.Alias!.Remove(alias);
                database.SaveUser(venmoUser);
                respondAction.Invoke("Alias deleted!", null);
            }
            else
            {
                respondAction.Invoke("That alias does not exist.", null);
            }
        }

        private async Task VenmoComplete(string which,
            List<string> completionNumbers,
            string venmoId,
            Action<string, List<IBlock>?> respondAction)
        {
            foreach (var stringNumber in completionNumbers)
            {
                bool canParse = long.TryParse(stringNumber, out long number);
                if (!canParse)
                {
                    respondAction.Invoke($"Payment completion number, {stringNumber}, must be a number", null);
                    continue;
                }

                string action = string.Empty;
                if (which == "accept")
                {
                    action = "approve";
                }
                else if (which == "reject")
                {
                    action = "deny";
                }
                else if (which == "cancel")
                {
                    action = "cancel";
                }

                VenmoPaymentResponse checkResponse;
                try
                {
                    checkResponse = await venmoApi.GetPayment(stringNumber);
                }
                catch (VenmoException ex)
                {
                    respondAction.Invoke(ex.Message, null);
                    continue;
                }
                
                if (checkResponse.Data!.Actor!.Id != venmoId)
                {
                    if (action == "cancel")
                    {
                        respondAction.Invoke($"{checkResponse.Data.Actor.DisplayName} requested ${checkResponse.Data.Amount:F2} for {checkResponse.Data.Note}. You cannot cancel it!", null);
                        continue;
                    }
                }
                else
                {
                    if (action == "approve" || action == "deny")
                    {
                        respondAction.Invoke($"You requested ${checkResponse.Data.Amount:F2} for {checkResponse.Data.Note}. You can try `/venmo complete cancel {stringNumber}` if you don't want to be paid back.", null);
                        continue;
                    }
                }

                try
                {
                    VenmoPaymentResponse response = await venmoApi.PutPayment(stringNumber, action);
                }
                catch (VenmoException ex)
                {
                    respondAction.Invoke(ex.Message, null);
                    continue;
                }
                
                if (action == "approve")
                {
                    respondAction.Invoke("Venmo completed!", null);
                }
                else if (action == "deny")
                {
                    respondAction.Invoke("Venmo rejected!", null);
                }
                else if (action == "cancel")
                {
                    respondAction.Invoke("Venmo canceled!", null);
                }
            }
        }

        private async Task VenmoCompleteAll(string which, string venmoId, Action<string, List<IBlock>?> respondAction)
        {
            string action = string.Empty;
            if (which == "accept")
            {
                action = "approve";
            }
            else if (which == "reject")
            {
                action = "deny";
            }
            else if (which == "cancel")
            {
                action = "cancel";
            }

            List<VenmoPaymentPending> pendingPayments;
            try
            {
                pendingPayments = await venmoApi.GetAllPayments();
            }
            catch (VenmoException ex)
            {
                respondAction.Invoke(ex.Message, null);
                return;
            }

            List<string> pendingIds = new List<string>();
            foreach (var payment in pendingPayments)
            {
                if (action == "approve" || action == "deny")
                {
                    if (payment.Actor.Id != venmoId)
                    {
                        pendingIds.Add(payment.Id);
                    }
                }
                else if (action == "cancel")
                {
                    if (payment.Actor.Id == venmoId)
                    {
                        pendingIds.Add(payment.Id);
                    }
                }
            }

            if (pendingIds.Count == 0)
            {
                if (action == "approve")
                {
                    respondAction.Invoke("No Venmos to accept", null);
                }
                else if (action == "deny")
                {
                    respondAction.Invoke("No Venmos to reject", null);
                }
                else if (action == "cancel")
                {
                    respondAction.Invoke("No venmos to cancel", null);
                }
                return;
            }

            foreach (var id in pendingIds)
            {
                try
                {
                    await venmoApi.PutPayment(id, action);
                }
                catch (VenmoException ex)
                {
                    respondAction.Invoke(ex.Message, null);
                    continue;
                }
            }

            if (action == "approve")
            {
                respondAction.Invoke("All Venmos accepted!", null);
            }
            else if (action == "deny")
            {
                respondAction.Invoke("All Venmos rejected!", null);
            }
            else if (action == "cancel")
            {
                respondAction.Invoke("All Venmos canceled!", null);
            }
        }

        private void GetLastMessage(Database.Models.VenmoUser venmoUser, Action<string, List<IBlock>?> respondAction)
        {
            if (!string.IsNullOrEmpty(venmoUser.Last))
            {
                respondAction.Invoke($"/{venmoUser.Last}", null);
            }
            else
            {
                respondAction.Invoke("No last message", null);
            }
        }

        private void SaveLastMessage(string message, Database.Models.VenmoUser venmoUser, MongoDatabase database)
        {
            venmoUser.Last = message;
            database.SaveUser(venmoUser);
        }

        private async Task GetVenmoPending(string which, string venmoId, Action<string, List<IBlock>?> respondAction)
        {
            try
            {
                List<string> strings = new List<string>();
                List<IBlock> blocks = new List<IBlock>();
                List<VenmoPaymentPending> pendingPayments = await venmoApi.GetAllPayments();
                foreach (var payment in pendingPayments)
                {
                    if (which == "incoming")
                    {
                        if (payment.Actor.Id != venmoId)
                        {
                            string acceptCommand = $"venmo complete accept {payment.Id}";
                            string str = $"{payment.Actor.DisplayName} requests ${payment.Amount:F2} for {payment.Note} | ID: {payment.Id}\n/{acceptCommand}";
                            strings.Add(str);
                            blocks.Add(new Section(TextObject.CreatePlainTextObject(str), null, null, helperMethods.GetVenmoUserProfileImage(payment.Actor)));
                            blocks.Add(new Actions(
                                new Button("Accept", "acceptButton", null, acceptCommand, null, null),
                                new Button("Reject", "rejectButton", null, $"venmo complete reject {payment.Id}", null, null)));
                        }
                    }
                    else if (which == "outgoing")
                    {
                        if (payment.Actor.Id == venmoId && payment.Target.Type == "user")
                        {
                            string str = $"{payment.Target.User.DisplayName} ({payment.Target.User.Username}) owes you ${payment.Amount:F2} {payment.Note} | ID: {payment.Id}";
                            strings.Add(str);
                            blocks.Add(new Section(TextObject.CreatePlainTextObject(str), null, null, helperMethods.GetVenmoUserProfileImage(payment.Target.User)));
                            blocks.Add(new Actions(new Button("Cancel", "cancelButton", null, $"venmo complete cancel {payment.Id}", null, null)));
                        }
                    }
                }
                
                if (strings.Count == 0)
                {
                    respondAction.Invoke("No pending Venmos", null);
                }
                else
                {
                    respondAction.Invoke(string.Join('\n', strings), blocks);
                }
            }
            catch (VenmoException ex)
            {
                respondAction.Invoke(ex.Message, null);
            }
        }

        private async Task GetVenmoBalance(Action<string, List<IBlock>?> respondAction)
        {
            try
            {
                MeResponse response = await venmoApi.GetMe();
                respondAction.Invoke(response.Data!.Balance!, null);
            }
            catch (VenmoException ex)
            {
                respondAction.Invoke(ex.Message, null);
            }
        }

        private async Task RequestAuth(string responseUrl)
        {
            await Respond("Authenticate to Venmo using the following command: `/venmo auth _username_ _password_` where _username_ " +
                $"is the email, phone number, or username you use to login to Venmo and _password_ is your Venmo password.", responseUrl);
        }

        private void RequestAuth(Action<string, List<IBlock>?> respondAction)
        {
            respondAction("Authenticate to Venmo using the following command: `/venmo auth _username_ _password_` where _username_ " +
                $"is the email, phone number, or username you use to login to Venmo and _password_ is your Venmo password.", null);
        }

        private async Task PublishHomeTabView(string userId, SlackCore slackApi)
        {
            List<IBlock> blocks = new List<IBlock>();
            OptionObject initialAudienceOption = new OptionObject("Private", "private");
            List<OptionObject> audienceOptions = new List<OptionObject>()
                {
                    initialAudienceOption,
                    new OptionObject("Friends", "friends"),
                    new OptionObject("Public", "public")
                };
            blocks.Add(new Input("Audience", new RadioButton("audienceRadio", audienceOptions, initialAudienceOption, null)));
            OptionObject initialTypeOption = new OptionObject("Charge", "charge");
            List<OptionObject> typeOptions = new List<OptionObject>()
                {
                    initialTypeOption,
                    new OptionObject("Pay", "pay")
                };
            blocks.Add(new Input("Type", new RadioButton("typeRadio", typeOptions, initialTypeOption, null)));
            blocks.Add(new Input("Amount", new PlainTextInput("amountInput", "Enter an amount or an arithmetic statement", null, false, null, null, null)));
            blocks.Add(new Input("Note", new PlainTextInput("noteInput", "Enter a note", null, false, null, null, null)));
            blocks.Add(new Input("Recipients", new PlainTextInput("recipientsInput", "Enter your recipients, separated by spaces", null, false, null, null, null)));
            blocks.Add(new Actions(new Button("Submit", "submitButton", null, null, "primary", null)));
            await slackApi.ViewsPublish(userId, new SlackViewObject(blocks));
        }

        private async Task Respond(string text, string responseUrl)
        {
            await Respond(text, null, responseUrl);
        }

        private async Task Respond(string text, List<IBlock>? blocks, string responseUrl)
        {
            var message = new golf1052.SlackAPI.Objects.Requests.SlackMessage(text, blocks);
            await httpClient.PostAsync(responseUrl, new StringContent(JsonConvert.SerializeObject(message, blockKitSerializer), Encoding.UTF8, "application/json"));
        }

        private Action<string, List<IBlock>?> CreateRespondAction(string responseUrl)
        {
            Action<string, List<IBlock>?> action = (text, blocks) =>
            {
                _ = Respond(text, blocks, responseUrl);
            };
            return action;
        }
    }
}
