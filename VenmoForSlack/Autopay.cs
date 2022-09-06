using golf1052.SlackAPI.BlockKit.Blocks;
using System.Collections.Generic;
using System;
using VenmoForSlack.Database.Models;
using VenmoForSlack.Venmo;
using System.Threading.Tasks;
using VenmoForSlack.Database;
using VenmoForSlack.Venmo.Models;

namespace VenmoForSlack
{
    public class Autopay
    {
        private readonly VenmoApi venmoApi;
        private readonly MongoDatabase database;

        public Autopay(VenmoApi venmoApi,
            MongoDatabase database)
        {
            this.venmoApi = venmoApi;
            this.database = database;
        }

        public async Task Parse(string[] splitMessage,
            Database.Models.VenmoUser venmoUser,
            Action<string, List<IBlock>?> respondAction)
        {
            if (splitMessage[2].ToLower() == "add")
            {
                if (splitMessage[3].ToLower() != "user")
                {
                    respondAction("User should be specified first", null);
                    return;
                }

                if (!IsValidIs(splitMessage[4]))
                {
                    respondAction("Invalid autopay statement.", null);
                    return;
                }

                string username = splitMessage[5];
                
                if (splitMessage.Length < 7)
                {
                    string? knownUserId = await GetKnownUserId(username, venmoUser);
                    if (knownUserId == null)
                    {
                        respondAction("Unknown Venmo user. You must be friends with or alias a Venmo user in order to " +
                            "setup an autopayment with them.", null);
                        return;
                    }
                    SaveAutopayStatement(venmoUser, database, username, knownUserId);
                    respondAction($"Saved autopay statement where user is {username}", null);
                    return;
                }

                if (!IsValidAnd(splitMessage[6]))
                {
                    respondAction("Invalid autopay statement. Need \"and\" between sections.", null);
                    return;
                }

                (double amount, string comparison)? parsedAmount = ParseAmount(7, splitMessage);
                double? amount = null;
                string? comparison = null;
                string? note;
                if (parsedAmount == null)
                {
                    note = ParseNote(7, splitMessage);
                }
                else
                {
                    amount = parsedAmount.Value.amount;
                    comparison = parsedAmount.Value.comparison;
                    if (splitMessage.Length >= 11)
                    {
                        if (!IsValidAnd(splitMessage[10]))
                        {
                            respondAction("Invalid autopay statement. Need \"and\" between sections.", null);
                            return;
                        }
                        note = ParseNote(11, splitMessage);
                    }
                }

                string? userId = await GetKnownUserId(username, venmoUser);
                if (userId == null)
                {
                    respondAction("Unknown Venmo user. You must be friends with or alias a Venmo user in order to " +
                        "setup an autopayment with them.", null);
                    return;
                }
                SaveAutopayStatement(venmoUser, database, username, userId, comparison, amount, note);
                string respondString = $"Saved autopayment where user is {username}";
                if (!string.IsNullOrEmpty(comparison) && amount != null)
                {
                    respondString += $" and amount {comparison} ${amount:F2}";
                }

                if (!string.IsNullOrEmpty(note))
                {
                    respondString += $" and note is {note}";
                }
                respondAction(respondString, null);
                return;
            }
            else if (splitMessage[2].ToLower() == "list")
            {
                if (venmoUser.Autopay == null || venmoUser.Autopay.Count == 0)
                {
                    respondAction("You have no autopayments defined.", null);
                    return;
                }

                for (int i = 0; i < venmoUser.Autopay.Count; i++)
                {
                    VenmoAutopay autopay = venmoUser.Autopay[i];
                    string respondString = $"{i + 1}: Automatically accept charges from {autopay.Username}";
                    bool hasAmount = false;
                    if (!string.IsNullOrEmpty(autopay.Comparison) && autopay.Amount != null)
                    {
                        hasAmount = true;
                        respondString += $" where amount {autopay.Comparison} ${autopay.Amount:F2}";
                    }

                    if (!string.IsNullOrEmpty(autopay.Note))
                    {
                        if (hasAmount)
                        {
                            respondString += $" and note is {autopay.Note}";
                        }
                        else
                        {
                            respondString += $" where note is {autopay.Note}";
                        }
                    }
                    respondAction(respondString, null);
                }
                return;
            }
            else if (splitMessage[2].ToLower() == "delete")
            {
                if (venmoUser.Autopay == null || venmoUser.Autopay.Count == 0)
                {
                    respondAction("You have no autopayments defined.", null);
                    return;
                }

                if (splitMessage.Length != 4)
                {
                    respondAction("Incorrect autopayment delete message. Expected /venmo autopay delete ###", null);
                    return;
                }

                if (int.TryParse(splitMessage[3], out int number))
                {
                    if (number > venmoUser.Autopay.Count || number < 1)
                    {
                        if (venmoUser.Autopay.Count == 1)
                        {
                            respondAction($"Not a valid autopayment number, you only have {venmoUser.Autopay.Count} item.", null);
                        }
                        else
                        {
                            respondAction($"Not a valid autopayment number, you only have {venmoUser.Autopay.Count} items.", null);
                        }
                        return;
                    }

                    VenmoAutopay autopayToRemove = venmoUser.Autopay[number - 1];
                    venmoUser.Autopay.RemoveAt(number - 1);
                    database.SaveUser(venmoUser);
                    string respondString = $"Removed autopayment where user is {autopayToRemove.Username}";
                    if (!string.IsNullOrEmpty(autopayToRemove.Comparison) && autopayToRemove.Amount != null)
                    {
                        respondString += $" and amount {autopayToRemove.Comparison} ${autopayToRemove.Amount:F2}";
                    }

                    if (!string.IsNullOrEmpty(autopayToRemove.Note))
                    {
                        respondString += $" and note is {autopayToRemove.Note}";
                    }
                    respondAction(respondString, null);
                }
                else
                {
                    respondAction($"Expected autopayment number to delete. Got {splitMessage[3]} instead.", null);
                }
                return;
            }
            else
            {
                respondAction("Unknown autopay string. Please specify add, list, or delete.", null);
                return;
            }
        }

        public async Task<(bool autopaid, string? message)> CheckForAutopayment(VenmoWebhookRequest request,
            Database.Models.VenmoUser venmoUser)
        {
            if (request.Type != "payment.created" || request.Data.Action != "charge")
            {
                return (false, "Autopay only supports created charges;");
            }

            if (venmoUser.Autopay == null || venmoUser.Autopay.Count == 0)
            {
                return (false, string.Empty);
            }

            string? matchString = null;

            int autopaymentIndex = -1;
            foreach (var autopayment in venmoUser.Autopay)
            {
                autopaymentIndex += 1;

                // First check id matches
                if (request.Data.Actor.Id != autopayment.UserId)
                {
                    continue;
                }

                // Next check if amount matches, if there is an amount
                if (!string.IsNullOrEmpty(autopayment.Comparison) && autopayment.Amount != null)
                {
                    if (autopayment.Comparison == "=")
                    {
                        if (autopayment.Amount != request.Data.Amount)
                        {
                            if (matchString == null)
                            {
                                matchString = GetFailedAutopaymentAmountString(autopayment, request, autopaymentIndex);
                            }
                            else
                            {
                                matchString += $"\n{GetFailedAutopaymentAmountString(autopayment, request, autopaymentIndex)}";
                            }
                            continue;
                        }
                    }
                    else if (autopayment.Comparison == "<")
                    {
                        if (autopayment.Amount >= request.Data.Amount)
                        {
                            if (matchString == null)
                            {
                                matchString = GetFailedAutopaymentAmountString(autopayment, request, autopaymentIndex);
                            }
                            else
                            {
                                matchString += $"\n{GetFailedAutopaymentAmountString(autopayment, request, autopaymentIndex)}";
                            }
                            continue;
                        }
                    }
                    else if (autopayment.Comparison == "<=")
                    {
                        if (autopayment.Amount > request.Data.Amount)
                        {
                            if (matchString == null)
                            {
                                matchString = GetFailedAutopaymentAmountString(autopayment, request, autopaymentIndex);
                            }
                            else
                            {
                                matchString += $"\n{GetFailedAutopaymentAmountString(autopayment, request, autopaymentIndex)}";
                            }
                            continue;
                        }
                    }
                    else
                    {
                        return (false, $"Unknown comparison value of {autopayment.Comparison}");
                    }
                }

                // Next check if the note matches, if there is a note
                if (!string.IsNullOrEmpty(autopayment.Note))
                {
                    if (!string.Equals(autopayment.Note.Trim(), request.Data.Note.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        if (matchString == null)
                        {
                            matchString = GetFailedAutopaymentNoteString(autopayment, request, autopaymentIndex);
                        }
                        else
                        {
                            matchString += $"\n{GetFailedAutopaymentNoteString(autopayment, request, autopaymentIndex)}";
                        }
                        continue;
                    }
                }

                // All other checks have passed at this point so now check that this autopayment hasn't run in the last 5 minutes.
                // This is to prevent abuse of autopayments.
                if (autopayment.LastRun != null)
                {
                    if (autopayment.LastRun.Value + TimeSpan.FromMinutes(5) >= DateTime.Now)
                    {
                        if (matchString == null)
                        {
                            matchString = $"Autopayment {autopaymentIndex + 1} was run in the last 5 minutes." +
                                $" Last execution time: {autopayment.LastRun}";
                        }
                        else
                        {
                            matchString += $"\nAutopayment {autopaymentIndex + 1} was run in the last 5 minutes." +
                                $" Last execution time: {autopayment.LastRun}";
                        }

                        return (false, matchString);
                    }
                }

                autopayment.LastRun = DateTime.Now;

                string? errorMessage = null;
                try
                {
                    await venmoApi.PutPayment(request.Data.Id, "approve");
                }
                catch (VenmoException ex)
                {
                    errorMessage = ex.Message;
                }

                if (errorMessage != null)
                {
                    return (false, errorMessage);
                }
                else
                {
                    matchString = $"Payment complete! Matched autopayment for {autopayment.Username}";
                    bool hasAmount = false;
                    if (!string.IsNullOrEmpty(autopayment.Comparison) && autopayment.Amount != null)
                    {
                        hasAmount = true;
                        matchString += $" where amount {autopayment.Comparison} ${autopayment.Amount}";
                    }

                    if (!string.IsNullOrEmpty(autopayment.Note))
                    {
                        if (hasAmount)
                        {
                            matchString += $" and note is {autopayment.Note}";
                        }
                        else
                        {
                            matchString += $" where note is {autopayment.Note}";
                        }
                    }

                    return (true, matchString);
                }
            }

            return (false, matchString);
        }

        private string GetFailedAutopaymentAmountString(VenmoAutopay autopayment, VenmoWebhookRequest request, int autopaymentIndex)
        {
            string failString = $"Didn't match autopayment {autopaymentIndex + 1} for {autopayment.Username} where" +
                $" amount {autopayment.Comparison} ${autopayment.Amount}";

            if (!string.IsNullOrEmpty(autopayment.Note))
            {
                failString += $" and note is {autopayment.Note}";
            }

            return $"{failString} because requested amount was {request.Data.Amount:F2}.";
        }

        private string GetFailedAutopaymentNoteString(VenmoAutopay autopayment, VenmoWebhookRequest request, int autopaymentIndex)
        {
            string failString = $"Didn't match autopayment {autopaymentIndex + 1} for {autopayment.Username} where" +
                $" note is {autopayment.Note}";

            if (!string.IsNullOrEmpty(autopayment.Comparison) && autopayment.Amount != null)
            {
                failString += $" and amount {autopayment.Comparison} {autopayment.Amount}";
            }

            return $"{failString} because request note was {request.Data.Note}.";
        }

        private (double amount, string comparison)? ParseAmount(int startIndex, string[] splitMessage)
        {
            if (splitMessage[startIndex].ToLower() == "amount")
            {
                double amount;
                string comparison;
                if (IsValidIs(splitMessage[startIndex + 1]))
                {
                    comparison = "=";
                }
                else if (IsValidLessThan(splitMessage[startIndex + 1]))
                {
                    comparison = "<";
                }
                else if (IsValidLessThanOrEqualTo(splitMessage[startIndex + 1]))
                {
                    comparison = "<=";
                }
                else
                {
                    return null;
                }

                if (splitMessage[startIndex + 2].StartsWith('$'))
                {
                    amount = double.Parse(splitMessage[startIndex + 2].Substring(1));
                }
                else
                {
                    amount = double.Parse(splitMessage[startIndex + 2]);
                }
                return (amount, comparison);
            }
            else
            {
                return null;
            }
        }

        private string? ParseNote(int startIndex, string[] splitMessage)
        {
            if (splitMessage[startIndex].ToLower() == "note")
            {
                string note;
                if (!IsValidIs(splitMessage[startIndex + 1]))
                {
                    return null;
                }

                note = string.Join(' ', splitMessage[(startIndex + 2)..]);
                return note;
            }
            else
            {
                return null;
            }
        }

        private void SaveAutopayStatement(Database.Models.VenmoUser venmoUser,
            MongoDatabase database,
            string username,
            string userId,
            string? comparison = null,
            double? amount = null,
            string? note = null)
        {
            VenmoAutopay autopayStatement = new VenmoAutopay()
            {
                Username = username,
                UserId = userId
            };

            if (!string.IsNullOrEmpty(comparison) && amount != null)
            {
                autopayStatement.Comparison = comparison;
                autopayStatement.Amount = amount;
            }

            if (!string.IsNullOrEmpty(note))
            {
                autopayStatement.Note = note;
            }

            if (venmoUser.Autopay == null)
            {
                venmoUser.Autopay = new List<VenmoAutopay>();
            }

            venmoUser.Autopay.Add(autopayStatement);
            database.SaveUser(venmoUser);
        }

        /// <summary>
        /// Gets the Venmo id of a known user. A known user is defined as an aliased user or a friend of the user.
        /// </summary>
        /// <param name="username">Username of Venmo user</param>
        /// <param name="venmoUser">Current Venmo user database model</param>
        /// <returns>Venmo id of username. Null if username is an unknown user.</returns>
        private async Task<string?> GetKnownUserId(string username, Database.Models.VenmoUser venmoUser)
        {
            string? id = venmoUser.GetAliasId(username);
            if (id == null)
            {
                List<Venmo.Models.VenmoUser> friends = await venmoApi.GetAllFriends();
                id = VenmoApi.FindFriendId(username, friends);
            }
            return id;
        }
        
        public bool IsValidIs(string text)
        {
            text = text.ToLower();
            return text == "is" || text == "=" || text == "==";
        }

        public bool IsValidLessThan(string text)
        {
            return text == "<";
        }

        public bool IsValidLessThanOrEqualTo(string text)
        {
            return text == "<=";
        }

        public bool IsValidAnd(string text)
        {
            text = text.ToLower();
            return text == "and";
        }
    }
}
