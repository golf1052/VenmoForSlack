using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using golf1052.SlackAPI;
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

        public VenmoController(ILogger<VenmoController> logger,
            HttpClient httpClient,
            VenmoApi venmoApi,
            IClock clock)
        {
            this.logger = logger;
            this.httpClient = httpClient;
            this.venmoApi = venmoApi;
            this.clock = clock;
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

        [HttpPost]
        public async Task<string> HandleRequest([FromForm] SlackRequest body)
        {
            if (!Settings.SettingsObject.Workspaces.Workspaces.ContainsKey(body.TeamId!))
            {
                logger.LogInformation($"Unknown team: {body.TeamId}");
                return "Team not configured to use Venmo";
            }

            WorkspaceInfo workspaceInfo = Settings.SettingsObject.Workspaces.Workspaces[body.TeamId!].ToObject<WorkspaceInfo>()!;
            if (workspaceInfo!.Token != body.Token)
            {
                logger.LogInformation($"Unknown token for team {body.TeamId}: {body.Token}");
                return "Team verification token mismatch";
            }
            SlackCore slackApi = new SlackCore(workspaceInfo.BotToken);

            MongoDatabase database = new MongoDatabase(body.TeamId!);
            if (string.IsNullOrEmpty(body.Text))
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

            string[] splitMessage = body.Text.Split(' ');
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
            
            string? accessToken = await GetAccessToken(body.UserId!, database);
            if (string.IsNullOrEmpty(accessToken))
            {
                logger.LogError($"Couldn't refresh access token for {body.UserId!}");
                _ = RequestAuth(body.ResponseUrl!);
            }
            else
            {
                venmoApi.AccessToken = accessToken;
                _ = ParseMessage($"venmo {body.Text!}", body.UserId!, body.ResponseUrl!, database, slackApi);
            }
            return "";
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
                UserId = response.User.Id
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

            return await CheckIfAccessTokenIsExpired(venmoUser, venmoApi, database);
        }

        public static async Task<string?> CheckIfAccessTokenIsExpired(Database.Models.VenmoUser venmoUser,
            VenmoApi venmoApi,
            MongoDatabase database)
        {
            DateTime expiresDate = (DateTime)venmoUser.Venmo!.ExpiresIn!;
            if (expiresDate < DateTime.UtcNow)
            {
                VenmoAuthResponse response;
                try
                {
                    response = await venmoApi.RefreshAuth(venmoUser.Venmo.RefreshToken!);
                }
                catch (Exception)
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

                venmoUser.Venmo.AccessToken = response.AccessToken;
                venmoUser.Venmo.ExpiresIn = DateTime.UtcNow + TimeSpan.FromSeconds(response.ExpiresIn);
                venmoUser.Venmo.RefreshToken = response.RefreshToken;

                database.SaveUser(venmoUser);
            }
            return venmoUser.Venmo.AccessToken;
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
                        parsedVenmoPayment = ParseVenmoPaymentMessage(splitMessage);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, $"Unable to parse message. Message: {string.Join(' ', splitMessage)}");
                        await Respond(ex.Message, responseUrl);
                        return;
                    }

                    var response = await VenmoPayment(venmoApi, venmoUser, database, parsedVenmoPayment.Amount,
                        parsedVenmoPayment.Note, parsedVenmoPayment.Recipients, parsedVenmoPayment.Action,
                        parsedVenmoPayment.Audience);
                    
                    foreach (var r in response.responses)
                    {
                        if (!string.IsNullOrEmpty(r.Error))
                        {
                            await Respond($"Venmo error: {r.Error}", responseUrl);
                            continue;
                        }

                        if (parsedVenmoPayment.Amount < 0)
                        {
                            await Respond($"Successfully charged {r.Data!.Payment.Target.User.Username} ${r.Data.Payment.Amount} for {r.Data.Payment.Note}. Audience is {r.Data.Payment.Audience}", responseUrl);
                        }
                        else
                        {
                            await Respond($"Successfully paid {r.Data!.Payment.Target.User.Username} ${r.Data.Payment.Amount} for {r.Data.Payment.Note}. Audience is {r.Data.Payment.Audience}", responseUrl);
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
                    SlackUser? slackUser = HelperMethods.GetSlackUser(venmoUser.UserId, slackUsers);
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

        public static ParsedVenmoPayment ParseVenmoPaymentMessage(string[] splitMessage)
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

            int forIndex = HelperMethods.FindStringInList(splitMessage, "for");
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

            int toIndex = HelperMethods.FindLastStringInList(splitMessage, "to");
            if (toIndex < 5)
            {
                throw new Exception("Could not find recipients");
            }

            string note = string.Join(' ', splitMessage[(forIndex + 1)..toIndex]);
            List<string> recipients = splitMessage[(toIndex + 1)..].ToList();
            return new ParsedVenmoPayment(amount, note, recipients, action, audience);
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
                        paymentInfo = ParseVenmoPaymentMessage(ConvertScheduleMessageIntoPaymentMessage(schedule.Command.Split(' ')));
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
                scheduledTime = ScheduleProcessor.ConvertScheduleMessageIntoDateTime(splitMessage, userTimeZone, clock);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, $"{string.Join(' ', splitMessage)}");
                await Respond(ex.Message, responseUrl);
                return;
            }

            string[] newSplitMessage = ConvertScheduleMessageIntoPaymentMessage(splitMessage);
            ParsedVenmoPayment parsedVenmoPayment;
            try
            {
                parsedVenmoPayment = ParseVenmoPaymentMessage(newSplitMessage);
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
                Verb = ScheduleProcessor.GetScheduleMessageVerb(splitMessage),
                NextExecution = scheduledTime.ToDateTimeUtc(),
                Command = string.Join(' ', splitMessage)
            });

            database.SaveUser(venmoUser);
            await Respond($"Scheduled Venmo set! Next execution: {scheduledTime.GetFriendlyZonedDateTimeString()}.", responseUrl);
        }

        public static string[] ConvertScheduleMessageIntoPaymentMessage(string[] splitMessage)
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

            string[] newSplitMessage = new string[splitMessage.Length - payOrChargeIndex + 1];
            Array.Copy(splitMessage, payOrChargeIndex, newSplitMessage, 1, splitMessage.Length - payOrChargeIndex);
            newSplitMessage[0] = splitMessage[0];
            return newSplitMessage;
        }

        public static async Task<(List<VenmoPaymentWithBalanceResponse> responses, List<string> unprocessedRecipients)> VenmoPayment(
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
            Dictionary<string, string> ids = new Dictionary<string, string>();
            List<string> unprocessedRecipients = new List<string>();
            foreach (var recipient in recipients)
            {
                if (recipient.StartsWith("phone:"))
                {
                    ids.Add("phone", recipient.Substring(6));
                }
                else if (recipient.StartsWith("email:"))
                {
                    ids.Add("email", recipient.Substring(6));
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
                            id = VenmoApi.FindFriend(recipient, friendsList);
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
                    ids.Add("user_id", id);
                }
            }

            List<VenmoPaymentWithBalanceResponse> responses = await venmoApi.PostPayment(amount, note, ids, action, venmoAudience);
            return (responses, unprocessedRecipients);
        }

        private static void AddUsernameToCache(string username,
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

        public static double CalculateTotal(List<string> amountStringList)
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

        public static double Mathify(double? num1, string? sign, double? num2)
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

        private async Task Respond(string message, string responseUrl)
        {
            JObject o = new JObject();
            o["text"] = message;
            await httpClient.PostAsync(responseUrl, new StringContent(o.ToString(Formatting.None), Encoding.UTF8, "application/json"));
        }
    }
}
