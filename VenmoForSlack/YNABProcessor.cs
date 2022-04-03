using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using golf1052.SlackAPI;
using golf1052.YNABAPI.Api;
using golf1052.YNABAPI.Client;
using golf1052.YNABAPI.Model;
using Microsoft.Extensions.Logging;
using NodaTime;
using VenmoForSlack.Controllers;
using VenmoForSlack.Database;
using VenmoForSlack.Models;
using VenmoForSlack.Venmo;
using VenmoForSlack.Venmo.Models;

namespace VenmoForSlack
{
    public class YNABProcessor
    {
        private const string Default = "default";
        private readonly ILogger<YNABProcessor> logger;
        private readonly ILogger<VenmoApi> venmoApiLogger;
        private readonly ILogger<MongoDatabase> mongoDatabaseLogger;
        private readonly HttpClient httpClient;
        private readonly HelperMethods helperMethods;
        private readonly Duration CheckDuration;
        private Task checkTask;

        public YNABProcessor(ILogger<YNABProcessor> logger,
            ILogger<VenmoApi> venmoApiLogger,
            ILogger<MongoDatabase> mongoDatabaseLogger,
            HttpClient httpClient,
            HelperMethods helperMethods)
        {
            this.logger = logger;
            this.venmoApiLogger = venmoApiLogger;
            this.mongoDatabaseLogger = mongoDatabaseLogger;
            this.httpClient = httpClient;
            this.helperMethods = helperMethods;
            CheckDuration = Duration.FromHours(1);
            checkTask = CheckForVenmoDeposits();
            _ = CheckCheckVenmoTask();
        }

        private async Task CheckForVenmoDeposits()
        {
            while (true)
            {
                foreach (var workspace in Settings.SettingsObject.Workspaces.Workspaces)
                {
                    WorkspaceInfo workspaceInfo = workspace.Value.ToObject<WorkspaceInfo>()!;
                    logger.LogDebug($"Processing workspace ${workspace.Key}");
                    MongoDatabase database = new MongoDatabase(workspace.Key, mongoDatabaseLogger);
                    SlackCore slackApi = new SlackCore(workspaceInfo.BotToken);
                    var slackUsers = await slackApi.UsersList();
                    List<Database.Models.VenmoUser> users = database.GetAllUsers();
                    foreach (var user in users)
                    {
                        if (user.YNAB != null && user.YNAB.DefaultAccount != null)
                        {
                            // Get YNAB access token
                            Configuration config = helperMethods.CreateConfiguration(user.YNAB.Auth!.AccessToken!);
                            string? ynabAccessToken = await helperMethods.CheckIfYNABAccessTokenIsExpired(user, database, new ApiClient(config));
                            if (ynabAccessToken == null)
                            {
                                logger.LogError($"Unable to refresh YNAB access token for {user.UserId}");
                                await WebhookController.SendSlackMessage(workspaceInfo, "Unable to import your Venmo deposits into YNAB because your YNAB authentication information is invalid. Please refresh it.",
                                    user.UserId, httpClient);
                                continue;
                            }
                            config = helperMethods.CreateConfiguration(ynabAccessToken);

                            // Get Venmo access token
                            VenmoApi venmoApi = new VenmoApi(venmoApiLogger);
                            string? venmoAccessToken = await helperMethods.CheckIfVenmoAccessTokenIsExpired(user, venmoApi, database);
                            if (string.IsNullOrEmpty(venmoAccessToken))
                            {
                                logger.LogError($"Unable to refresh Venmo access token for {user.UserId}");
                                await WebhookController.SendSlackMessage(workspaceInfo,
                                    "Unable to process scheduled Venmos as your token has expired. Please refresh it.",
                                    user.UserId, httpClient);
                                continue;
                            }
                            venmoApi.AccessToken = venmoAccessToken;

                            logger.LogDebug($"Processing slack user {user.UserId}");

                            List<TransactionDetail> venmoDeposits = await GetVenmoDeposits(user, config);
                            if (!ShouldProcessLatestVenmoDeposit(venmoDeposits))
                            {
                                continue;
                            }

                            // (fromDate - toDate]. We can adjust which transactions get included after if the
                            // subtransactions total doesn't match the deposit total
                            DateOnly toDate = DateOnly.FromDateTime(venmoDeposits[0].Date!.Value);
                            DateOnly fromDate;
                            // if multiple deposits were made on the same day (due to Venmo transfer limits) then
                            // make sure we get the first deposit not on the same day
                            foreach (var deposit in venmoDeposits)
                            {
                                fromDate = DateOnly.FromDateTime(deposit.Date!.Value);
                                if (fromDate != toDate)
                                {
                                    break;
                                }
                            }
                            fromDate = fromDate.AddDays(1);

                            var venmoTransactions = await GetVenmoTransactionsBetweenDates(fromDate, toDate, venmoApi, user);

                            TransactionDetail ynabDeposit = venmoDeposits[0];
                            SaveTransaction? newTransaction = null;
                            List<SaveSubTransaction> subTransactions = new List<SaveSubTransaction>();
                            VenmoTransaction? previousTransaction = null;
                            decimal originalDepositAmount = 0;
                            decimal depositAmount = 0;
                            bool multipleTransfers = false;
                            foreach (VenmoTransaction transaction in venmoTransactions)
                            {
                                if (previousTransaction == null)
                                {
                                    depositAmount = transaction.Transfer!.Amount;
                                    originalDepositAmount = depositAmount;
                                }
                                else
                                {
                                    if (previousTransaction.Type == "transfer" && transaction.Type == "transfer")
                                    {
                                        multipleTransfers = true;
                                        depositAmount += transaction.Transfer!.Amount;
                                        originalDepositAmount = depositAmount;
                                    }

                                    if (transaction.Type == "payment")
                                    {
                                        if (previousTransaction.Type == "transfer" && multipleTransfers)
                                        {
                                            long milliunitAmount = (long)(depositAmount * 1000m);
                                            string importId = $"YNAB:{milliunitAmount}:{ynabDeposit.Date:yyyy-MM-dd}";
                                            newTransaction = new SaveTransaction(ynabDeposit.AccountId, ynabDeposit.Date,
                                                amount: milliunitAmount, ynabDeposit.PayeeId, ynabDeposit.PayeeName,
                                                ynabDeposit.CategoryId, memo: null, golf1052.YNABAPI.HelperMethods.ClearedEnum.Uncleared,
                                                ynabDeposit.Approved, ynabDeposit.FlagColor, importId: importId,
                                                subTransactions);
                                        }

                                        if (depositAmount - transaction.Payment!.Amount > 0)
                                        {
                                            string? memo = transaction.Payment.Note;
                                            Guid? categoryId = null;
                                            if (user.YNAB.Mapping != null && user.YNAB.Mapping.Count > 0 && memo != null)
                                            {
                                                foreach (var mapping in user.YNAB.Mapping)
                                                {
                                                    if (mapping.VenmoNote.Equals(memo, StringComparison.InvariantCultureIgnoreCase))
                                                    {
                                                        categoryId = new Guid(mapping.CategoryId);
                                                        break;
                                                    }
                                                }
                                            }
                                            var subTransaction = new SaveSubTransaction(amount: (long)(transaction.Payment.Amount * 1000),
                                                payeeId: null, payeeName: null, categoryId: categoryId, memo);
                                            subTransactions.Add(subTransaction);
                                            depositAmount -= transaction.Payment.Amount;
                                        }
                                    }
                                }
                                previousTransaction = transaction;
                            }

                            if (depositAmount > 0)
                            {
                                var unknownSubTransaction = new SaveSubTransaction(amount: (long)(depositAmount * 1000),
                                    payeeId: null, payeeName: null, categoryId: null, memo: "UNKNOWN");
                                subTransactions.Add(unknownSubTransaction);
                            }

                            TransactionsApi transactionsApi = new TransactionsApi(config);
                            if (multipleTransfers)
                            {
                                // we created a new transaction so POST it
                                var createTransactionsResponse = await transactionsApi.CreateTransactionAsync(Default,
                                    new SaveTransactionsWrapper(newTransaction));

                                // then "delete" the other transfers
                                List<UpdateTransaction> transactionsToDelete = new List<UpdateTransaction>();
                                for (int i = 0; i < venmoDeposits.Count; i++)
                                {
                                    TransactionDetail transactionDetail = venmoDeposits[i];
                                    VenmoTransaction venmoTransaction = venmoTransactions[i];
                                    if (venmoTransaction.Type != "transfer")
                                    {
                                        break;
                                    }

                                    UpdateTransaction updateTransaction = new UpdateTransaction(transactionDetail.AccountId,
                                        transactionDetail.Date, transactionDetail.Amount, transactionDetail.PayeeId,
                                        transactionDetail.PayeeName, transactionDetail.CategoryId,
                                        memo: $"<DELETE ME: Combined into ${originalDepositAmount:F2} Transfer from Venmo> {transactionDetail.Memo}",
                                        golf1052.YNABAPI.HelperMethods.ClearedEnum.Uncleared,
                                        transactionDetail.Approved, transactionDetail.FlagColor, transactionDetail.ImportId,
                                        new List<SaveSubTransaction>());
                                    transactionsToDelete.Add(updateTransaction);
                                }

                                var updateTransactionsResponse = await transactionsApi.UpdateTransactionsAsync(Default, new UpdateTransactionsWrapper(transactionsToDelete));
                                await WebhookController.SendSlackMessage(workspaceInfo,
                                    $"Imported and created a new YNAB Venmo transaction totaling ${originalDepositAmount:F2} with " +
                                    $"{subTransactions.Count} Venmo subtransactions combining {transactionsToDelete.Count} " +
                                    $"transactions which occured on {newTransaction!.Date:d}. " +
                                    $"Please go into YNAB, confirm the new transaction and delete the old transactions.",
                                    user.UserId, httpClient);
                            }
                            else
                            {
                                // we didn't create a new transaction so just PUT the existing transaction
                                newTransaction = new SaveTransaction(ynabDeposit.AccountId, ynabDeposit.Date,
                                    ynabDeposit.Amount, ynabDeposit.PayeeId, ynabDeposit.PayeeName, ynabDeposit.CategoryId,
                                    ynabDeposit.Memo, golf1052.YNABAPI.HelperMethods.ClearedEnum.Uncleared, ynabDeposit.Approved,
                                    ynabDeposit.FlagColor, ynabDeposit.ImportId, subTransactions);
                                var response = await transactionsApi.UpdateTransactionAsync(Default, ynabDeposit.Id,
                                    new SaveTransactionWrapper(newTransaction));
                                await WebhookController.SendSlackMessage(workspaceInfo,
                                    $"Updated a YNAB Venmo transaction with {subTransactions.Count} Venmo subtransactions which occured" +
                                    $"on {newTransaction.Date:d}. Please go into YNAB and confirm the updated transaction.",
                                    user.UserId, httpClient);
                            }
                        }
                    }
                }
                await Task.Delay(CheckDuration.ToTimeSpan());
            }
        }

        private async Task<List<TransactionDetail>> GetVenmoDeposits(Database.Models.VenmoUser venmoUser, Configuration configuration)
        {
            TransactionsApi transactionsApi = new TransactionsApi(configuration);
            TransactionsResponse transactions = await transactionsApi.GetTransactionsByAccountAsync(Default, venmoUser.YNAB!.DefaultAccount);
            var venmoDeposits = transactions.Transactions
                .Where(t => t.PayeeName != null && t.PayeeName.ToLower().Contains("from venmo"))
                .OrderByDescending(t => t.Date)
                .ToList();
            return venmoDeposits;
        }

        private bool ShouldProcessLatestVenmoDeposit(List<TransactionDetail> venmoDeposits)
        {
            return venmoDeposits != null && venmoDeposits.Count > 1 &&
                venmoDeposits[0].Date.HasValue && venmoDeposits[1].Date.HasValue &&
                (venmoDeposits[0].Subtransactions == null || venmoDeposits[0].Subtransactions.Count == 0);
        }

        // This will look for payments between the two dates but also transfers up to 5 days before the toDate and
        // 5 days before the fromDate
        private async Task<List<VenmoTransaction>> GetVenmoTransactionsBetweenDates(DateOnly fromDate, DateOnly toDate, VenmoApi venmoApi, Database.Models.VenmoUser venmoUser)
        {
            logger.LogDebug($"Finding Venmo transactions between {fromDate} and {toDate}");
            List<VenmoTransaction> transactions = new List<VenmoTransaction>();
            string? beforeId = null;
            bool foundAllTransactions = false;
            VenmoTransaction? toTransfer = null;
            VenmoTransaction? fromTransfer = null;
            VenmoTransaction? previousTransaction = null;
            do
            {
                var transactionsResponse = await venmoApi.GetTransactions(beforeId);
                if (transactionsResponse.Data != null)
                {
                    foreach (var transaction in transactionsResponse.Data)
                    {
                        if (transaction.DateCreated != null && transaction.Type != null)
                        {
                            if (transaction.Type == "transfer")
                            {
                                DateTime transactionDate = DateTime.Parse(transaction.DateCreated);
                                logger.LogDebug($"Found transfer on {transactionDate}. ID: {transaction.Id}");

                                // if the transfer initiated from Venmo was before the toDate and after 5 days before
                                // the toDate set the transaction as the toTransfer
                                if (toTransfer == null &&
                                    transactionDate < toDate.ToDateTime(TimeOnly.MinValue) &&
                                    transactionDate > toDate.AddDays(-5).ToDateTime(TimeOnly.MinValue))
                                {
                                    logger.LogDebug($"Set transfer {transaction.Id} as toTransfer");
                                    toTransfer = transaction;
                                    transactions.Add(transaction);
                                }

                                // need to handle transfers done one after another (due to Venmo transfer limit)
                                if (toTransfer != null && previousTransaction != null && previousTransaction.Type == "transfer")
                                {
                                    logger.LogDebug($"Found transfers one after another\n" +
                                        $"Transfer 1: {previousTransaction.Id} at {previousTransaction.DateCreated}\n" +
                                        $"Transfer 2: {transaction.Id} at {transaction.DateCreated}");
                                    transactions.Add(transaction);
                                }

                                if (fromTransfer == null &&
                                    transactionDate < fromDate.ToDateTime(TimeOnly.MinValue) &&
                                    transactionDate > fromDate.AddDays(-5).ToDateTime(TimeOnly.MinValue))
                                {
                                    logger.LogDebug($"Set transfer {transaction.Id} as fromTransfer");
                                    fromTransfer = transaction;
                                    transactions.Add(transaction);
                                    foundAllTransactions = true;
                                    break;
                                }
                            }
                            else if (transaction.Type == "payment")
                            {
                                // if the user received money, either someone paid them or they charged someone
                                if ((transaction.Payment!.Action == "pay" && transaction.Payment.Target!.User.Id == venmoUser.Venmo!.UserId) ||
                                    (transaction.Payment.Action == "charge" && transaction.Payment.Actor!.Id == venmoUser.Venmo!.UserId))
                                {
                                    DateTime transactionDate = DateTime.Parse(transaction.DateCreated);
                                    if (toTransfer != null)
                                    {
                                        if (fromTransfer == null)
                                        {
                                            if (transactionDate < DateTime.Parse(toTransfer.DateCreated!))
                                            {
                                                transactions.Add(transaction);
                                            }
                                        }
                                    }
                                }
                            }
                            previousTransaction = transaction;
                        }
                    }

                    if (transactionsResponse.Data.Count == 0)
                    {
                        break;
                    }
                    beforeId = previousTransaction!.Id;
                }
            }
            while (!foundAllTransactions);

            return transactions;
        }

        private bool AreTransfersOnSameDate(VenmoTransaction transaction1, VenmoTransaction transaction2)
        {
            return DateOnly.FromDateTime(DateTime.Parse(transaction1.DateCreated!)) ==
                DateOnly.FromDateTime(DateTime.Parse(transaction2.DateCreated!));
        }

        private async Task CheckCheckVenmoTask()
        {
            while (true)
            {
                if (checkTask.IsFaulted)
                {
                    logger.LogError("CheckForVenmoDeposits task has failed, restarting.");
                    if (checkTask.Exception != null)
                    {
                        foreach (var exception in checkTask.Exception.InnerExceptions)
                        {
                            logger.LogError(exception, "InnerException");
                        }
                    }
                    checkTask = CheckForVenmoDeposits();
                }
                await Task.Delay((int)CheckDuration.TotalMilliseconds / 2);
            }
        }
    }
}
