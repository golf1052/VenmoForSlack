using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using golf1052.SlackAPI;
using golf1052.SlackAPI.BlockKit.BlockElements;
using golf1052.SlackAPI.BlockKit.Blocks;
using golf1052.SlackAPI.BlockKit.CompositionObjects;
using golf1052.SlackAPI.Objects;
using golf1052.SlackAPI.Objects.Requests;
using golf1052.SlackAPI.Objects.Responses;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using Newtonsoft.Json;
using NodaTime;
using VenmoForSlack.Controllers.Models;
using VenmoForSlack.Database;
using VenmoForSlack.Database.Models;
using VenmoForSlack.Models;
using VenmoForSlack.Venmo;
using VenmoForSlack.Venmo.Models;

namespace VenmoForSlack.Controllers
{
    [ApiController]
    [Route("/")]
    public class VenmoController : ControllerBase
    {
        private readonly ILogger logger;
        private readonly HttpClient httpClient;
        private readonly VenmoApi venmoApi;
        private readonly IClock clock;
        private readonly HelperMethods helperMethods;
        private readonly JsonSerializerSettings blockKitSerializer;

        public VenmoController(ILogger<VenmoController> logger,
            HttpClient httpClient,
            VenmoApi venmoApi,
            IClock clock,
            HelperMethods helperMethods)
        {
            this.logger = logger;
            this.httpClient = httpClient;
            this.venmoApi = venmoApi;
            this.clock = clock;
            this.helperMethods = helperMethods;
            blockKitSerializer = new golf1052.SlackAPI.HelperMethods().GetBlockKitSerializer();
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

        [HttpPost("/interactive")]
        public void HandleInteractiveRequest([FromForm]string payload)
        {
            BlockActionResponse payloadObject = JsonConvert.DeserializeObject<BlockActionResponse>(payload, blockKitSerializer)!;
            logger.LogInformation(payload.ToString());

            string? verifyRequestResponse = VerifyRequest(payloadObject.Team.Id!, payloadObject.Token);
            if (!string.IsNullOrEmpty(verifyRequestResponse))
            {
                _ = Respond(verifyRequestResponse, payloadObject.ResponseUrl);
                return;
            }

            SlackCore slackApi = new SlackCore(GetWorkspaceInfo(payloadObject.Team.Id!).BotToken);
            MongoDatabase database = GetTeamDatabase(payloadObject.Team.Id!);

            if (payloadObject.Actions.Count > 0)
            {
                string blockType = (string)payloadObject.Actions[0]["type"]!;
                if (blockType == "button")
                {
                    Button button = payloadObject.Actions[0].ToObject<Button>()!;
                    _ = GetAccessTokenAndParseMessage(button.Value, payloadObject.User.Id, payloadObject.ResponseUrl, database, slackApi);
                }
            }
        }

        [HttpPost]
        public async Task<string> HandleRequest([FromForm] SlackRequest body)
        {
            string? verifyRequestResponse = VerifyRequest(body.TeamId!, body.Token!);
            if (!string.IsNullOrEmpty(verifyRequestResponse))
            {
                return verifyRequestResponse;
            }

            SlackCore slackApi = new SlackCore(GetWorkspaceInfo(body.TeamId!).BotToken);

            MongoDatabase database = GetTeamDatabase(body.TeamId!);

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
                    return Help.HelpMessage;
                }
            }

            string[] splitMessage = requestText.Split(' ');
            if (splitMessage.Length > 0)
            {
                if (splitMessage[0].ToLower() == "code")
                {
                    if (splitMessage.Length == 1)
                    {
                        return Help.HelpMessage;
                    }
                    else
                    {
                        _ = CompleteAuth(splitMessage[1], body.UserId!, body.ResponseUrl!, database);
                        return "";
                    }
                }
            }

            _ = GetAccessTokenAndParseMessage($"venmo {requestText}", body.UserId!, body.ResponseUrl!, database, slackApi);
            return "";
        }

        private string ProcessRequestText(string? text)
        {
            // If the request is sent on mobile (at least the iOS app) body.Text will be of the form: "/venmo balance"
            // On desktop it's just "balance". The desktop form is documented and was the original way it worked.
            // The mobile difference is undocumnted and annoying.

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

        private MongoDatabase GetTeamDatabase(string teamId)
        {
            return new MongoDatabase(teamId);
        }

        private async Task GetAccessTokenAndParseMessage(string text, string userId, string responseUrl, MongoDatabase database, SlackCore slackApi)
        {
            string? accessToken = await GetAccessToken(userId, database);
            if (string.IsNullOrEmpty(accessToken))
            {
                logger.LogError($"Couldn't refresh access token for {userId}");
                _ = RequestAuth(responseUrl);
            }
            else
            {
                venmoApi.AccessToken = accessToken;
                _ = ParseMessage(text, userId, responseUrl, database, slackApi);
            }
        }

        private async Task CompleteAuth(string code, string userId, string responseUrl, MongoDatabase database)
        {
            VenmoAuthResponse response = await venmoApi.CompleteAuth(code);
            // The user gets created before we hit this so it's always not null
            Database.Models.VenmoUser venmoUser = database.GetUser(userId)!;
            venmoUser.Venmo = new Database.Models.VenmoAuthObject()
            {
                AccessToken = response.AccessToken,
                ExpiresIn = DateTime.UtcNow + TimeSpan.FromSeconds(response.ExpiresIn),
                RefreshToken = response.RefreshToken,
                UserId = response.User!.Id
            };
            database.SaveUser(venmoUser);
            await Respond("Authentication complete", responseUrl);
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
                    UserId = ""
                };
                database.SaveUser(venmoUser);
                return null;
            }

            return await helperMethods.CheckIfAccessTokenIsExpired(venmoUser, venmoApi, database);
        }

        private async Task ParseMessage(string message,
            string userId,
            string responseUrl,
            MongoDatabase database,
            SlackCore slackApi)
        {
            MeResponse me = await venmoApi.GetMe();
            string venmoId = me.Data.User.Id;
            venmoApi.UserId = venmoId;
            Database.Models.VenmoUser venmoUser = database.GetUser(userId)!;
            string[] splitMessage = message.Split(' ');
            if (splitMessage.Length == 1)
            {
                await Respond(Help.HelpMessage, responseUrl);
            }
            else if (splitMessage[1].ToLower() == "help")
            {
                await Respond(Help.HelpMessage, responseUrl);
            }
            else if (splitMessage[1].ToLower() == "last")
            {
                await GetLastMessage(venmoUser, responseUrl);
            }
            else if (splitMessage[1].ToLower() == "code")
            {
                await CompleteAuth(splitMessage[2], userId, responseUrl, database);
            }
            else
            {
                SaveLastMessage(message, venmoUser, database);
                if (splitMessage[1].ToLower() == "balance")
                {
                    await GetVenmoBalance(responseUrl);
                }
                else if (splitMessage[1].ToLower() == "pending")
                {
                    if (splitMessage.Length == 2)
                    {
                        await GetVenmoPending("to", me.Data.User.Id, responseUrl);
                    }
                    else if (splitMessage.Length == 3)
                    {
                        string which = splitMessage[2].ToLower();
                        if (which == "to" || which == "from")
                        {
                            await GetVenmoPending(which, me.Data.User.Id, responseUrl);
                        }
                        else
                        {
                            await Respond("Valid pending commands\npending\npending to\npending from", responseUrl);
                        }
                    }
                    else
                    {
                        await Respond("Valid pending commands\npending\npending to\npending from", responseUrl);
                    }
                }
                else if (splitMessage[1].ToLower() == "search")
                {
                    if (splitMessage.Length == 2)
                    {
                        await Respond("User search requires a search query", responseUrl);
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
                            await Respond(ex.Message, responseUrl);
                            return;
                        }
                        List<string> responseLines = new List<string>();
                        List<IBlock> sections = new List<IBlock>();
                        Section userCountSection;
                        string userCountText;
                        if (response.Data.Count >= 50)
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

                        await Respond(string.Join('\n', responseLines), sections, responseUrl);
                    }
                }
                else if (splitMessage[1].ToLower() == "accept" ||
                    splitMessage[1].ToLower() == "reject" ||
                    splitMessage[1].ToLower() == "cancel")
                {
                    if (splitMessage.Length == 3)
                    {
                        await Respond($"You probably meant: /venmo complete {splitMessage[1].ToLower()} {splitMessage[2].ToLower()}", responseUrl);
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
                                await VenmoCompleteAll(which, venmoId, responseUrl);
                            }
                            else
                            {
                                List<string> completionNumbers = splitMessage[3..].ToList();
                                await VenmoComplete(which, completionNumbers, venmoId, responseUrl);
                            }
                        }
                        else
                        {
                            await Respond("Valid complete commands\nvenmo complete accept #\nvenmo complete reject #\nvenmo complete cancel #", responseUrl);
                        }
                    }
                    else
                    {
                        await Respond("Valid complete commands\nvenmo complete accept #\nvenmo complete reject #\nvenmo complete cancel #", responseUrl);
                    }
                }
                else if (splitMessage[1].ToLower() == "alias")
                {
                    if (splitMessage.Length == 4)
                    {
                        if (splitMessage[2].ToLower() == "delete")
                        {
                            await DeleteAlias(venmoUser, splitMessage[3].ToLower(), responseUrl, database);
                        }
                        else
                        {
                            string friendUsername = splitMessage[2];
                            string alias = splitMessage[3].ToLower();
                            await AliasUser(venmoUser, friendUsername, alias, responseUrl, database);
                        }
                    }
                    else if (splitMessage.Length == 3 && splitMessage[2].ToLower() == "list")
                    {
                        await ListAliases(venmoUser, responseUrl);
                    }
                    else
                    {
                        await Respond("Invalid alias command, your alias probably has a space in it.", responseUrl);
                    }
                }
                else if (splitMessage.Length <= 2)
                {
                    await Respond("Invalid payment string", responseUrl);
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
                        await Respond(ex.Message, responseUrl);
                        return;
                    }

                    var response = await helperMethods.VenmoPayment(venmoApi, venmoUser, database, parsedVenmoPayment.Amount,
                        parsedVenmoPayment.Note, parsedVenmoPayment.Recipients, parsedVenmoPayment.Action,
                        parsedVenmoPayment.Audience);
                    
                    foreach (var r in response.responses)
                    {
                        if (!string.IsNullOrEmpty(r.Error))
                        {
                            await Respond($"Venmo error: {r.Error}", responseUrl);
                            continue;
                        }

                        if (parsedVenmoPayment.Action == VenmoAction.Charge)
                        {
                            string responseText = $"Successfully charged {r.Data!.Payment.Target.User.DisplayName} ({r.Data!.Payment.Target.User.Username}) ${r.Data.Payment.Amount} for {r.Data.Payment.Note}. Audience is {r.Data.Payment.Audience}";
                            List<IBlock> blocks = new List<IBlock>()
                            {
                                new Section(TextObject.CreatePlainTextObject(responseText), null, null,
                                    helperMethods.GetVenmoUserProfileImage(r.Data!.Payment.Target.User)),
                                new Actions(new Button("Cancel", "cancelButton", null, $"venmo complete cancel {r.Data.Payment.Id}", null, null))
                            };
                            await Respond(responseText, blocks, responseUrl);
                        }
                        else if (parsedVenmoPayment.Action == VenmoAction.Pay)
                        {
                            await Respond($"Successfully paid {r.Data!.Payment.Target.User.DisplayName} ({r.Data!.Payment.Target.User.Username}) ${r.Data.Payment.Amount} for {r.Data.Payment.Note}. Audience is {r.Data.Payment.Audience}", responseUrl);
                        }
                        else
                        {
                            await Respond($"Successfully ??? {r.Data!.Payment.Target.User.DisplayName} ({r.Data!.Payment.Target.User.Username}) ${r.Data.Payment.Amount} for {r.Data.Payment.Note}. Audience is {r.Data.Payment.Audience}", responseUrl);
                        }
                    }

                    foreach (var u in response.unprocessedRecipients)
                    {
                        await Respond($"You are not friends with {u}.", responseUrl);
                    }
                }
                else if (splitMessage[1].ToLower() == "schedule")
                {
                    var slackUsers = await slackApi.UsersList();
                    SlackUser? slackUser = helperMethods.GetSlackUser(venmoUser.UserId, slackUsers);
                    if (slackUser == null)
                    {
                        logger.LogError($"While trying to get slack user timezone they disappeared? {venmoUser.UserId}");
                        await Respond("While trying to get your timzone you disappeared?", responseUrl);
                        return;
                    }
                    await ParseScheduleMessage(splitMessage, slackUser.TimeZone, venmoUser, database, responseUrl);
                }
            }
        }

        private async Task ParseScheduleMessage(string[] splitMessage,
            string userTimeZone,
            Database.Models.VenmoUser venmoUser,
            MongoDatabase database,
            string responseUrl)
        {
            if (splitMessage[2].ToLower() == "list")
            {
                if (venmoUser.Schedule == null || venmoUser.Schedule.Count == 0)
                {
                    await Respond("You have no scheduled Venmos.", responseUrl);
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

                    await Respond($"{i + 1}: {scheduleVerb} {VenmoAudienceHelperMethods.ToString(paymentInfo.Audience)} " +
                        $"{actionString} of ${paymentInfo.Amount} for {paymentInfo.Note} to " +
                        $"{string.Join(' ', paymentInfo.Recipients)}. This will be {processedString} at " +
                        $"{nextExecutionInTimeZone.GetFriendlyZonedDateTimeString()}.", responseUrl);
                }
                return;
            }

            if (splitMessage[2].ToLower() == "delete")
            {
                if (venmoUser.Schedule == null || venmoUser.Schedule.Count == 0)
                {
                    await Respond("You have no scheduled Venmos.", responseUrl);
                    return;
                }

                if (splitMessage.Length != 4)
                {
                    await Respond("Incorrect schedule delete message. Expected /venmo schedule delete ###", responseUrl);
                    return;
                }

                if (int.TryParse(splitMessage[3], out int number))
                {
                    if (number > venmoUser.Schedule.Count || number < 1)
                    {
                        if (venmoUser.Schedule.Count == 1)
                        {
                            await Respond($"Not a valid schedule number, you only have {venmoUser.Schedule.Count} scheduled item.", responseUrl);
                        }
                        else
                        {
                            await Respond($"Not a valid schedule number, you only have {venmoUser.Schedule.Count} scheduled items.", responseUrl);
                        }
                        return;
                    }

                    string commandToRemove = venmoUser.Schedule[number - 1].Command;
                    venmoUser.Schedule.RemoveAt(number - 1);
                    database.SaveUser(venmoUser);
                    await Respond($"Removed /{commandToRemove}", responseUrl);
                }
                else
                {
                    await Respond($"Expected schedule number to delete. Got {splitMessage[3]} instead.", responseUrl);
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
                await Respond(ex.Message, responseUrl);
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
                await Respond(ex.Message, responseUrl);
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
            await Respond($"Scheduled Venmo set! Next execution: {scheduledTime.GetFriendlyZonedDateTimeString()}.", responseUrl);
        }

        private async Task AliasUser(Database.Models.VenmoUser venmoUser,
            string friendUsername,
            string alias,
            string responseUrl,
            MongoDatabase database)
        {
            List<Venmo.Models.VenmoUser> friends = await venmoApi.GetAllFriends();
            string? friendId = VenmoApi.FindFriend(friendUsername, friends);
            if (friendId == null)
            {
                await Respond($"You are not friends with {friendUsername}", responseUrl);
                return;
            }

            BsonDocument aliasDoc = new BsonDocument()
            {
                { "username", friendUsername },
                { "id", friendId }
            };
            if (venmoUser.Alias == null)
            {
                venmoUser.Alias = new BsonDocument();
            }
            venmoUser.Alias.Add(new BsonElement(alias, aliasDoc));
            database.SaveUser(venmoUser);
            await Respond("Alias set!", responseUrl);
        }

        private async Task ListAliases(Database.Models.VenmoUser venmoUser,
            string responseUrl)
        {
            if (venmoUser.Alias != null)
            {
                List<string> aliasList = new List<string>();
                foreach (var alias in venmoUser.Alias.Elements)
                {
                    aliasList.Add($"{alias.Name} points to {alias.Value.AsBsonDocument.GetElement("username").Value.AsString}");
                }
                await Respond(string.Join('\n', aliasList), responseUrl);
            }
            else
            {
                await Respond("You have no aliases set.", responseUrl);
            }
        }

        private async Task DeleteAlias(Database.Models.VenmoUser venmoUser,
            string alias,
            string responseUrl,
            MongoDatabase database)
        {
            VenmoAlias? venmoAlias = venmoUser.GetAlias(alias);
            if (venmoAlias != null)
            {
                venmoUser.Alias!.Remove(alias);
                database.SaveUser(venmoUser);
                await Respond("Alias deleted!", responseUrl);
            }
            else
            {
                await Respond("That alias does not exist.", responseUrl);
            }
        }

        private async Task VenmoComplete(string which,
            List<string> completionNumbers,
            string venmoId,
            string responseUrl)
        {
            foreach (var stringNumber in completionNumbers)
            {
                bool canParse = long.TryParse(stringNumber, out long number);
                if (!canParse)
                {
                    _ = Respond($"Payment completion number, {stringNumber}, must be a number", responseUrl);
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
                    _ = Respond(ex.Message, responseUrl);
                    continue;
                }
                
                if (checkResponse.Data.Actor.Id != venmoId)
                {
                    if (action == "cancel")
                    {
                        _ = Respond($"{checkResponse.Data.Actor.DisplayName} requested ${checkResponse.Data.Amount:F2} for {checkResponse.Data.Note}. You cannot cancel it!", responseUrl);
                        continue;
                    }
                }
                else
                {
                    if (action == "approve" || action == "deny")
                    {
                        _ = Respond($"You requested ${checkResponse.Data.Amount:F2} for {checkResponse.Data.Note}. You can try `/venmo complete cancel {stringNumber}` if you don't want to be paid back.", responseUrl);
                        continue;
                    }
                }

                try
                {
                    VenmoPaymentResponse response = await venmoApi.PutPayment(stringNumber, action);
                }
                catch (VenmoException ex)
                {
                    _ = Respond(ex.Message, responseUrl);
                    continue;
                }
                
                if (action == "approve")
                {
                    _ = Respond("Venmo completed!", responseUrl);
                }
                else if (action == "deny")
                {
                    _ = Respond("Venmo rejected!", responseUrl);
                }
                else if (action == "cancel")
                {
                    _ = Respond("Venmo canceled!", responseUrl);
                }
            }
        }

        private async Task VenmoCompleteAll(string which, string venmoId, string responseUrl)
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
                await Respond(ex.Message, responseUrl);
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
                    await Respond("No Venmos to accept", responseUrl);
                }
                else if (action == "deny")
                {
                    await Respond("No Venmos to reject", responseUrl);
                }
                else if (action == "cancel")
                {
                    await Respond("No venmos to cancel", responseUrl);
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
                    _ = Respond(ex.Message, responseUrl);
                    continue;
                }
            }

            if (action == "approve")
            {
                await Respond("All Venmos accepted!", responseUrl);
            }
            else if (action == "deny")
            {
                await Respond("All Venmos rejected!", responseUrl);
            }
            else if (action == "cancel")
            {
                await Respond("All Venmos canceled!", responseUrl);
            }
        }

        private async Task GetLastMessage(Database.Models.VenmoUser venmoUser, string responseUrl)
        {
            if (!string.IsNullOrEmpty(venmoUser.Last))
            {
                await Respond($"/{venmoUser.Last}", responseUrl);
            }
            else
            {
                await Respond("No last message", responseUrl);
            }
        }

        private void SaveLastMessage(string message, Database.Models.VenmoUser venmoUser, MongoDatabase database)
        {
            venmoUser.Last = message;
            database.SaveUser(venmoUser);
        }

        private async Task GetVenmoPending(string which, string venmoId, string responseUrl)
        {
            List<string> strings = new List<string>();
            try
            {
                List<VenmoPaymentPending> pendingPayments = await venmoApi.GetAllPayments();
                foreach (var payment in pendingPayments)
                {
                    if (which == "to")
                    {
                        if (payment.Actor.Id != venmoId)
                        {
                            strings.Add($"{payment.Actor.DisplayName} requests ${payment.Amount:F2} for {payment.Note} | ID: {payment.Id}");
                        }
                    }
                    else if (which == "from")
                    {
                        if (payment.Actor.Id == venmoId && payment.Target.Type == "user")
                        {
                            strings.Add($"{payment.Target.User.DisplayName} owes you ${payment.Amount:F2} {payment.Note} | ID: {payment.Id}");
                        }
                    }
                }
                
                if (strings.Count == 0)
                {
                    await Respond("No pending Venmos", responseUrl);
                }
                else
                {
                    await Respond(string.Join('\n', strings), responseUrl);
                }
            }
            catch (VenmoException ex)
            {
                await Respond(ex.Message, responseUrl);
            }
        }

        private async Task GetVenmoBalance(string responseUrl)
        {
            try
            {
                MeResponse response = await venmoApi.GetMe();
                await Respond(response.Data.Balance!, responseUrl);
            }
            catch (VenmoException ex)
            {
                await Respond(ex.Message, responseUrl);
            }
        }

        private async Task RequestAuth(string responseUrl)
        {
            string authUrl = VenmoApi.GetAuthorizeUrl();
            await Respond($"Authenticate to Venmo with the following URL: {authUrl} then send back the auth code in " +
                "this format\nvenmo code CODE", responseUrl);
        }

        private async Task Respond(string text, string responseUrl)
        {
            await Respond(text, null, responseUrl);
        }

        private async Task Respond(string text, List<IBlock>? blocks, string responseUrl)
        {
            SlackMessage message = new SlackMessage(text, blocks);
            await httpClient.PostAsync(responseUrl, new StringContent(JsonConvert.SerializeObject(message, blockKitSerializer), Encoding.UTF8, "application/json"));
        }
    }
}
