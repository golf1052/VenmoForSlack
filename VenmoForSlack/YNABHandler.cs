using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using golf1052.SlackAPI.BlockKit.Blocks;
using golf1052.YNABAPI.Api;
using golf1052.YNABAPI.Client;
using golf1052.YNABAPI.Model;
using Microsoft.Extensions.Logging;
using VenmoForSlack.Database;
using VenmoForSlack.Database.Models;
using VenmoForSlack.Database.Models.YNAB;

namespace VenmoForSlack
{
    public class YNABHandler
    {
        private readonly ILogger logger;
        private readonly HelperMethods helperMethods;
        private ApiClient ynabApi;
        private readonly SlackOAuthHandler<YNABTokenResponse> ynabOAuthHandler;

        public YNABHandler(ILogger<YNABHandler> logger,
            HelperMethods helperMethods)
        {
            this.logger = logger;
            this.helperMethods = helperMethods;
            Configuration configuration = helperMethods.CreateConfiguration();
            ynabApi = configuration.CreateApiClient();
            ynabOAuthHandler = new SlackOAuthHandler<YNABTokenResponse>("YNAB", ynabApi.GetAuthorizeUrl(), "venmo ynab code <CODE>", ynabApi.CompleteAuth);
        }

        public async Task ParseMessage(string[] splitMessage,
            string venmoUserId,
            Action<string, List<IBlock>?> respondAction,
            MongoDatabase database)
        {
            VenmoUser venmoUser = database.GetUser(venmoUserId)!;
            if (splitMessage.Length == 4 && splitMessage[2].ToLower() == "code")
            {
                await CompleteAuth(splitMessage[3], venmoUser, database);
                Configuration config = helperMethods.CreateConfiguration(venmoUser.YNAB!.Auth!.AccessToken!);
                BudgetSummary defaultBudget = await GetDefaultBudget(config);
                database.SaveUser(venmoUser);
                string accounts = ListAccounts(defaultBudget.Accounts);
                respondAction.Invoke($"YNAB authentication complete. Please select the account your Venmo transactions import into and set it as your default account using /venmo ynab set account {{number}}\n{accounts}", null);
                return;
            }

            if (venmoUser.YNAB == null || venmoUser.YNAB.Auth == null || string.IsNullOrEmpty(venmoUser.YNAB.Auth.AccessToken))
            {
                respondAction(ynabOAuthHandler.RequestAuthString, null);
                return;
            }

            string? accessToken = await helperMethods.CheckIfYNABAccessTokenIsExpired(venmoUser, database, ynabApi);
            if (accessToken == null)
            {
                respondAction(ynabOAuthHandler.RequestAuthString, null);
                return;
            }

            Configuration configuration = helperMethods.CreateConfiguration(venmoUser.YNAB.Auth!.AccessToken!);

            if (splitMessage[2].ToLower() == "help")
            {
                // help
                respondAction(YNABHelp.HelpMessage, null);
            }
            else if (splitMessage[2].ToLower() == "list")
            {
                if (splitMessage[3].ToLower() == "account" || splitMessage[3].ToLower() == "accounts")
                {
                    // list accounts
                    respondAction(await ListAccounts(configuration), null);
                }
                else if (splitMessage[3].ToLower() == "categories")
                {
                    // list categories
                    respondAction(await ListCategories(configuration), null);
                }
                else if (splitMessage[3].ToLower() == "mapping" || splitMessage[3].ToLower() == "mappings")
                {
                    // list mapping
                    respondAction(ListMappings(venmoUser), null);
                }
            }
            else if (splitMessage[2].ToLower() == "set")
            {
                if (splitMessage[3].ToLower() == "account")
                {
                    // set default account
                    respondAction(await SetDefaultAccount(splitMessage[4], venmoUser, database, configuration), null);
                }
                else if (splitMessage[3].ToLower() == "mapping")
                {
                    // create/update mapping
                    respondAction(await CreateOrUpdateMapping(splitMessage, venmoUser, database, configuration), null);
                }
            }
            else if (splitMessage[2].ToLower() == "delete")
            {
                if (splitMessage[3].ToLower() == "mapping")
                {
                    // delete mapping
                    respondAction(DeleteMapping(splitMessage[4], venmoUser, database), null);
                }
                else if (splitMessage[3].ToLower() == "auth")
                {
                    // delete auth
                    venmoUser.YNAB.Auth = null;
                    database.SaveUser(venmoUser);
                    respondAction("Deleted YNAB authentication information from database. If you want to delete all of your YNAB user information from the database run /venmo ynab delete everything", null);
                }
                else if (splitMessage[3].ToLower() == "everything")
                {
                    // delete everything
                    venmoUser.YNAB = null;
                    database.SaveUser(venmoUser);
                    respondAction("Deleted all of your YNAB information from the database.", null);
                }
            }
        }

        private async Task CompleteAuth(string code, VenmoUser venmoUser, MongoDatabase database)
        {
            YNABTokenResponse response = await ynabOAuthHandler.CompleteAuth(code);
            venmoUser.YNAB = new Database.Models.YNAB.YNABUser()
            {
                Auth = new Database.Models.YNAB.YNABAuthObject()
                {
                    AccessToken = response.AccessToken,
                    ExpiresIn = DateTime.UtcNow + TimeSpan.FromSeconds(response.ExpiresIn),
                    RefreshToken = response.RefreshToken
                }
            };
            database.SaveUser(venmoUser);

            Configuration config = helperMethods.CreateConfiguration(response.AccessToken);
            ynabApi = config.CreateApiClient();
        }

        private async Task<BudgetSummaryResponse> GetBudgets(Configuration configuration)
        {
            BudgetsApi budgetsApi = new BudgetsApi(configuration);
            return await budgetsApi.GetBudgetsAsync(true);
        }

        private async Task<BudgetSummary> GetDefaultBudget(Configuration configuration)
        {
            BudgetSummaryResponse budgets = await GetBudgets(configuration);
            return budgets.DefaultBudget;
        }

        private async Task<string> ListAccounts(Configuration configuration)
        {
            var openAccounts = await GetAccounts(configuration);
            List<string> accountsStrings = new List<string>();
            for (int i = 0; i < openAccounts.Count; i++)
            {
                accountsStrings.Add($"{i + 1}: {openAccounts[i].Name}");
            }
            return string.Join('\n', accountsStrings);
        }

        private string ListAccounts(List<Account> accounts)
        {
            var openAccounts = accounts.Where(a => a.Closed == false).ToList();
            List<string> accountStrings = new List<string>();
            for (int i = 0; i < openAccounts.Count; i++)
            {
                accountStrings.Add($"{i + 1}: {openAccounts[i].Name}");
            }
            return string.Join('\n', accountStrings);
        }

        private async Task<string> SetDefaultAccount(string numberString, VenmoUser venmoUser, MongoDatabase database, Configuration configuration)
        {
            bool isNumber = int.TryParse(numberString, out int number);
            if (!isNumber)
            {
                return "You must select an number";
            }

            var accounts = await GetAccounts(configuration);
            if (number < 1 || number > accounts.Count)
            {
                return $"You must select a number between 1 and {accounts.Count}";
            }

            number -= 1;
            venmoUser.YNAB!.DefaultAccount = accounts[number].Id.ToString();
            database.SaveUser(venmoUser);
            return $"Default account set to {accounts[number].Name}";
        }

        private async Task<List<Account>> GetAccounts(Configuration configuration)
        {
            AccountsApi accountsApi = new AccountsApi(configuration);
            AccountsResponse accounts = await accountsApi.GetAccountsAsync("default");
            return accounts.Accounts.Where(a => a.Closed == false).ToList();
        }

        private async Task<List<(string, string)>> GetCategories(Configuration configuration)
        {
            CategoriesApi categoriesApi = new CategoriesApi(configuration);
            CategoriesResponse categories = await categoriesApi.GetCategoriesAsync("default");
            List<(string, string)> categoryStringsToIds = new List<(string, string)>();
            foreach (var categoryGroup in categories.CategoryGroups)
            {
                string categoryGroupName = categoryGroup.Name;
                foreach (var category in categoryGroup.Categories)
                {
                    categoryStringsToIds.Add(($"{categoryGroupName.Trim()}: {category.Name.Trim()}", category.Id!.Value.ToString()));
                }
            }
            return categoryStringsToIds;
        }

        private async Task<string> ListCategories(Configuration configuration)
        {
            var categoryDict = await GetCategories(configuration);
            return string.Join('\n', categoryDict.Select(c => c.Item1).ToList());
        }

        private async Task<string> CreateOrUpdateMapping(string[] splitMessage, VenmoUser venmoUser, MongoDatabase database, Configuration configuration)
        {
            // index 3 is mapping (venmo ynab set mapping)
            // pull out note and category name from quotation marks
            // 0 means we just started, 1 means building note, 2 means building category
            // I don't want to create an enum up top just for this 💤
            int buildingTracker = 0;
            StringBuilder noteBuilder = new StringBuilder();
            StringBuilder categoryBuilder = new StringBuilder();
            bool seenQuote = false;
            foreach (char c in string.Join(' ', splitMessage))
            {
                if (c == '"')
                {
                    if (!seenQuote)
                    {
                        buildingTracker += 1;
                        seenQuote = true;
                        continue;
                    }
                    else
                    {
                        seenQuote = false;
                    }
                }
                
                if (seenQuote)
                {
                    if (buildingTracker == 1)
                    {
                        noteBuilder.Append(c);
                    }
                    else if (buildingTracker == 2)
                    {
                        categoryBuilder.Append(c);
                    }
                }
            }

            string note = noteBuilder.ToString().Trim();
            string categoryName = categoryBuilder.ToString().Trim();

            if (string.IsNullOrEmpty(note))
            {
                return "You must include a Venmo note to map from. This might have happened because you didn't surround your note and/or category in \"quotes\".";
            }

            if (string.IsNullOrEmpty(categoryName))
            {
                return "You must include a YNAB category to map to. This might have happened because you didn't surround your note and/or category in \"quotes\".";
            }

            var categories = await GetCategories(configuration);
            (string, string)? foundCategory = categories.Find(c =>
            {
                return c.Item1.Equals(categoryName, StringComparison.InvariantCultureIgnoreCase);
            });

            if (!foundCategory.HasValue)
            {
                return $"Did not find a matching YNAB category for {categoryName}";
            }

            if (venmoUser.YNAB!.Mapping == null)
            {
                venmoUser.YNAB.Mapping = new List<YNABCategoryMapping>();
            }

            foreach (var existingMapping in venmoUser.YNAB.Mapping)
            {
                if (existingMapping.VenmoNote.Equals(note, StringComparison.InvariantCultureIgnoreCase))
                {
                    return $"{existingMapping.VenmoNote} is already mapped to {existingMapping.CategoryName}";
                }
            }

            YNABCategoryMapping mapping = new YNABCategoryMapping(note,
                foundCategory.Value.Item1,
                foundCategory.Value.Item2);

            venmoUser.YNAB.Mapping.Add(mapping);
            database.SaveUser(venmoUser);

            return $"Successfully mapped {mapping.VenmoNote} to {mapping.CategoryName}";
        }

        private string ListMappings(VenmoUser venmoUser)
        {
            if (venmoUser.YNAB!.Mapping == null || venmoUser.YNAB.Mapping.Count == 0)
            {
                return "You have no YNAB mappings.";
            }

            List<string> mappings = new List<string>();
            for (int i = 0; i < venmoUser.YNAB.Mapping.Count; i++)
            {
                var mapping = venmoUser.YNAB.Mapping[i];
                mappings.Add($"{i + 1}: {mapping.VenmoNote} -> {mapping.CategoryName}");
            }
            return string.Join('\n', mappings);
        }

        private string DeleteMapping(string numberString, VenmoUser venmoUser, MongoDatabase database)
        {
            if (venmoUser.YNAB!.Mapping == null || venmoUser.YNAB.Mapping.Count == 0)
            {
                return "You have no YNAB mappings.";
            }

            bool isNumber = int.TryParse(numberString, out int number);
            if (!isNumber)
            {
                return "You must select a number";
            }

            if (number < 1 || number > venmoUser.YNAB.Mapping.Count)
            {
                return $"You must return a number between 1 and {venmoUser.YNAB.Mapping.Count}";
            }

            number -= 1;
            var mapping = venmoUser.YNAB.Mapping[number];
            venmoUser.YNAB.Mapping.RemoveAt(number);
            database.SaveUser(venmoUser);
            return $"Removed mapping {mapping.VenmoNote} -> {mapping.CategoryName}";
        }
    }
}
