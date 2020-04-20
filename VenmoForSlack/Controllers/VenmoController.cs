using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VenmoForSlack;
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

        public VenmoController(ILogger<VenmoController> logger,
            HttpClient httpClient,
            VenmoApi venmoApi)
        {
            this.logger = logger;
            this.httpClient = httpClient;
            this.venmoApi = venmoApi;
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

            MongoDatabase database = new MongoDatabase(body.TeamId!);

            string[] splitMessage = body.Text!.Split(' ');
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
            
            string? accessToken = await GetAccessToken(body.UserId!, body.ResponseUrl!, database);
            if (string.IsNullOrEmpty(accessToken))
            {
                _ = Respond("Access token is expired, Sanders needs to debug this. Go bother him or something.", body.ResponseUrl!);
            }
            else
            {
                venmoApi.AccessToken = accessToken;
                _ = ParseMessage($"venmo {body.Text!}", body.UserId!, body.ResponseUrl!, database);
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

        private async Task<string?> GetAccessToken(string userId, string responseUrl, MongoDatabase database)
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
                await RequestAuth(responseUrl);
                return null;
            }

            DateTime expiresDate = (DateTime)venmoUser.Venmo.ExpiresIn!;
            if (expiresDate < DateTime.UtcNow)
            {
                VenmoAuthResponse response;
                try
                {
                    logger.LogInformation($"Trying to refresh token for {userId}");
                    response = await venmoApi.RefreshAuth(venmoUser.Venmo.RefreshToken!);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, $"Failed to refresh token for {userId}");
                    venmoUser.Venmo = new VenmoAuthObject()
                    {
                        AccessToken = "",
                        ExpiresIn = "",
                        RefreshToken = "",
                        UserId = ""
                    };
                    database.SaveUser(venmoUser);
                    await RequestAuth(responseUrl);
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
            MongoDatabase database)
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
                    string audienceString = "private";
                    if (splitMessage[2].ToLower() == "charge" || splitMessage[2].ToLower() == "pay")
                    {
                        audienceString = splitMessage[1].ToLower();
                        if (audienceString != "public" && audienceString != "friends" && audienceString != "private")
                        {
                            await Respond("Valid payment sharing commands\npublic\nfriend\nprivate", responseUrl);
                            return;
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
                        await Respond("Valid payment sharing commands\npublic\nfriend\nprivate", responseUrl);
                        return;
                    }

                    string which = splitMessage[1];
                    VenmoAction action;
                    
                    try
                    {
                        action = VenmoActionHelperMethods.FromString(which);
                    }
                    catch (Exception)
                    {
                        await Respond("Unknown payment type, must be either 'pay' or 'charge'.", responseUrl);
                        return;
                    }

                    if (splitMessage.Length <= 6)
                    {
                        await Respond("Invalid payment string.", responseUrl);
                        return;
                    }
                    int forIndex = FindStringInList(splitMessage, "for");
                    if (forIndex == -1)
                    {
                        await Respond("Invalid payment string, you need to include what the payment is \"for\".", responseUrl);
                        return;
                    }
                    string[] amountStringArray = splitMessage[2..forIndex];
                    double? amount = await CalculateTotal(amountStringArray.ToList(), responseUrl);
                    if (amount == null)
                    {
                        return;
                    }
                    int toIndex = FindLastStringInList(splitMessage, "to");
                    if (toIndex < 5)
                    {
                        await Respond("Could not find recipients", responseUrl);
                        return;
                    }
                    string note = string.Join(' ', splitMessage[(forIndex + 1)..toIndex]);
                    List<string> recipients = splitMessage[(toIndex + 1)..].ToList();
                    await VenmoPayment(venmoUser, database, responseUrl, amount.Value, note, recipients, action,
                        audience);
                }
            }
        }

        private async Task VenmoPayment(Database.Models.VenmoUser venmoUser,
            MongoDatabase database,
            string responseUrl,
            double amount,
            string note,
            List<string> recipients,
            VenmoAction action,
            VenmoAudience venmoAudience = VenmoAudience.Private)
        {
            List<Venmo.Models.VenmoUser>? friendsList = null;
            Dictionary<string, string> ids = new Dictionary<string, string>();
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
                        await Respond($"You are not friends with {recipient}.", responseUrl);
                        continue;
                    }
                    ids.Add("user_id", id);
                }
            }

            List<VenmoPaymentWithBalanceResponse> responses =  await venmoApi.PostPayment(amount, note, ids, action, venmoAudience);
            foreach (var response in responses)
            {
                if (amount < 0)
                {
                    await Respond($"Successfully charged {response.Data.Payment.Target.User.Username} ${response.Data.Payment.Amount} for {response.Data.Payment.Note}. Audience is {response.Data.Payment.Audience}", responseUrl);
                }
                else
                {
                    await Respond($"Successfully paid {response.Data.Payment.Target.User.Username} ${response.Data.Payment.Amount} for {response.Data.Payment.Note}. Audience is {response.Data.Payment.Audience}", responseUrl);
                }
            }
        }

        private void AddUsernameToCache(string username,
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

        private async Task<double?> CalculateTotal(List<string> amountStringList, string responseUrl)
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
                                await Respond("Invalid arithmetic string", responseUrl);
                                return null;
                            }
                            currentSign = copy;
                        }
                        else
                        {
                            await Respond("Invalid arithmetic string", responseUrl);
                            return null;
                        }
                    }
                    else if (previousNumber == null)
                    {
                        bool canParse = double.TryParse(copy, out double number);
                        if (!canParse)
                        {
                            await Respond("Invalid arithmetic string", responseUrl);
                            return null;
                        }
                        previousNumber = number;
                    }
                    else if (currentNumber == null)
                    {
                        bool canParse = double.TryParse(copy, out double number);
                        if (!canParse)
                        {
                            await Respond("Invalid arithmetic string", responseUrl);
                            return null;
                        }
                        currentNumber = number;

                        double result;
                        try
                        {
                            result = Mathify(previousNumber, currentSign, currentNumber);
                        }
                        catch (ArithmeticException)
                        {
                            await Respond("Invalid arithmetic string", responseUrl);
                            return null;
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
                await Respond("Could not calculate total", responseUrl);
                return null;
            }
            return final;
        }

        private double Mathify(double? num1, string? sign, double? num2)
        {
            if (num1 == null || sign == null || num2 == null)
            {
                logger.LogError($"{nameof(num1)}: {num1}. {nameof(sign)}: {sign}. {nameof(num2)}: {num2}");
                throw new ArithmeticException("An argument is null");
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
                logger.LogError($"Unknown sign: {sign}");
                throw new ArithmeticException($"Unknown sign: {sign}");
            }
        }

        private int FindStringInList(IEnumerable<string> enumerable, string str)
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

        private int FindLastStringInList(IEnumerable<string> enumerable, string str)
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
