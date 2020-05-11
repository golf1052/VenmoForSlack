using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Net.Http;
using Flurl;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VenmoForSlack.Venmo.Models;
using Microsoft.Extensions.Logging;

namespace VenmoForSlack.Venmo
{
    public class VenmoApi
    {
        private const string BaseUrl = "https://api.venmo.com/v1/";
        private readonly ILogger logger;
        private HttpClient httpClient;
        public string? AccessToken { private get; set; }
        public string? UserId { private get; set; }

        public VenmoApi(ILogger<VenmoApi> logger)
        {
            this.logger = logger;
            httpClient = new HttpClient();
        }

        public static string GetAuthorizeUrl()
        {
            return $"https://api.venmo.com/v1/oauth/authorize?client_id={Secrets.VenmoClientId}&scope=make_payments%20access_payment_history%20access_feed%20access_profile%20access_email%20access_phone%20access_balance%20access_friends&response_type=code";
        }

        public async Task<VenmoAuthResponse> CompleteAuth(string code)
        {
            Url url = new Url(BaseUrl).AppendPathSegments("oauth", "access_token");
            Dictionary<string, string> data = new Dictionary<string, string>()
            {
                { "client_id", Secrets.VenmoClientId },
                { "client_secret", Secrets.VenmoClientSecret },
                { "code", code }
            };

            HttpResponseMessage responseMessage = await Post(url, new FormUrlEncodedContent(data));
            VenmoAuthResponse response = JsonConvert.DeserializeObject<VenmoAuthResponse>(await responseMessage.Content.ReadAsStringAsync());
            AccessToken = response.AccessToken;
            UserId = response.User.Id;
            return response;
        }

        public async Task<VenmoAuthResponse> RefreshAuth(string refreshToken)
        {
            logger.LogInformation("Attempting to refresh token");
            Url url = new Url(BaseUrl).AppendPathSegments("oauth", "access_token");
            Dictionary<string, string> data = new Dictionary<string, string>()
            {
                { "client_id", Secrets.VenmoClientId },
                { "client_secret", Secrets.VenmoClientSecret },
                { "refresh_token", refreshToken }
            };
            
            HttpResponseMessage responseMessage = await Post(url, new FormUrlEncodedContent(data));
            if (!responseMessage.IsSuccessStatusCode)
            {
                logger.LogError($"Failed to refresh token. " +
                    $"Refresh token: {refreshToken}. Status code: {responseMessage.StatusCode}. " +
                    $"Message: {await responseMessage.Content.ReadAsStringAsync()}");
                throw new Exception("Failed to refresh token");
            }
            string responseString = await responseMessage.Content.ReadAsStringAsync();
            logger.LogInformation(responseString);
            VenmoAuthResponse response = JsonConvert.DeserializeObject<VenmoAuthResponse>(responseString);
            AccessToken = response.AccessToken;
            UserId = response.User.Id;
            logger.LogInformation("Refreshed token successfully");
            return response;
        }

        public async Task<MeResponse> GetMe()
        {
            Url url = new Url(BaseUrl).AppendPathSegment("me");
            HttpResponseMessage responseMessage = await Get(url);
            MeResponse response = GetObject<MeResponse>(await responseMessage.Content.ReadAsStringAsync());
            return response;
        }

        public async Task<FriendsResponse> GetFriends(int limit = 20, int offset = 0)
        {
            Url url = new Url(BaseUrl).AppendPathSegments("users", UserId, "friends")
                .SetQueryParams(new {
                    limit = limit,
                    offset = offset,
                    access_token = AccessToken
                });
            HttpResponseMessage responseMessage = await Get(url);
            FriendsResponse response = GetObject<FriendsResponse>(await responseMessage.Content.ReadAsStringAsync());
            return response;
        }

        public async Task<List<VenmoUser>> GetAllFriends()
        {
            MeResponse me = await GetMe();
            int limit = me.Data.User.FriendsCount!.Value;
            int offset = 0;
            List<VenmoUser> friends = new List<VenmoUser>();
            FriendsResponse? friendsResponse = null;
            do
            {
                friendsResponse = await GetFriends(limit, offset);
                friends.AddRange(friendsResponse.Data);
                limit = friendsResponse.Data.Count;
                offset += friendsResponse.Data.Count;
            }
            while (friendsResponse.Pagination != null && friendsResponse.Pagination.Next != null);
            return friends;
        }

        /// <summary>
        /// Get a single payment.
        /// </summary>
        /// <param name="completionNumber">The payment id</param>
        /// <returns>A Venmo payment</returns>
        public async Task<VenmoPaymentResponse> GetPayment(string completionNumber)
        {
            Url url = new Url(BaseUrl).AppendPathSegments("payments", completionNumber);
            HttpResponseMessage responseMessage = await Get(url);
            VenmoPaymentResponse response = GetObject<VenmoPaymentResponse>(await responseMessage.Content.ReadAsStringAsync());
            return response;
        }

        /// <summary>
        /// Pays a single payment.
        /// </summary>
        /// <param name="completionNumber">The payment id</param>
        /// <param name="action">The Venmo action, one of approve, deny, or cancel</param>
        /// <returns>The completed Venmo payment</returns>
        public async Task<VenmoPaymentResponse> PutPayment(string completionNumber, string action)
        {
            Url url = new Url(BaseUrl).AppendPathSegments("payments", completionNumber);
            Dictionary<string, string> data = new Dictionary<string, string>()
            {
                { "access_token", AccessToken! },
                { "action", action }
            };
            HttpResponseMessage responseMessage = await Put(url, new FormUrlEncodedContent(data));
            VenmoPaymentResponse response = GetObject<VenmoPaymentResponse>(await responseMessage.Content.ReadAsStringAsync());
            return response;
        }

        /// <summary>
        /// Creates a new payment
        /// </summary>
        /// <param name="amount">The amount</param>
        /// <param name="note">The note</param>
        /// <param name="recipients">The recipients of the payment</param>
        /// <param name="action">The Venmo action, one of pay or charge</param>
        /// <param name="venmoAudience">The Venmo audience, one of private, friends, or public, defaults to private</param>
        /// <returns>List of payment responses</returns>
        public async Task<List<VenmoPaymentWithBalanceResponse>> PostPayment(double amount,
            string note,
            Dictionary<string, string> recipients,
            VenmoAction action,
            VenmoAudience venmoAudience = VenmoAudience.Private)
        {
            Url url = new Url(BaseUrl).AppendPathSegment("payments");
            string amountString = amount.ToString("F2");
            if (action == VenmoAction.Charge)
            {
                amountString = $"-{amountString}";
            }

            List<VenmoPaymentWithBalanceResponse> responses = new List<VenmoPaymentWithBalanceResponse>();
            foreach (var recipient in recipients)
            {
                Dictionary<string, string> data = new Dictionary<string, string>()
                {
                    { "access_token", AccessToken! },
                    { recipient.Key, recipient.Value }
                };
                data.Add("note", note);
                data.Add("amount", amountString);
                data.Add("audience", venmoAudience.ToString());
                HttpResponseMessage responseMessage = await Post(url, new FormUrlEncodedContent(data));
                VenmoPaymentWithBalanceResponse response;
                try
                {
                    response = GetObject<VenmoPaymentWithBalanceResponse>(await responseMessage.Content.ReadAsStringAsync());
                }
                catch (VenmoException ex)
                {
                    response = new VenmoPaymentWithBalanceResponse()
                    {
                        Error = ex.Message
                    };
                }
                responses.Add(response);
            }
            return responses;
        }

        public async Task<VenmoPaymentPendingResponse> GetPending(int limit = 20, int offset = 0)
        {
            Url url = new Url(BaseUrl).AppendPathSegment("payments")
                .SetQueryParams(new {
                    status = "pending",
                    access_token = AccessToken
                });
            HttpResponseMessage responseMessage = await Get(url);
            VenmoPaymentPendingResponse response = GetObject<VenmoPaymentPendingResponse>(await responseMessage.Content.ReadAsStringAsync());
            return response;
        }

        public async Task<List<VenmoPaymentPending>> GetAllPayments()
        {
            int limit = 100;
            int offset = 0;
            List<VenmoPaymentPending> pendingPayments = new List<VenmoPaymentPending>();
            VenmoPaymentPendingResponse? response = null;
            do
            {
                try
                {
                    response = await GetPending(limit, offset);
                }
                catch (VenmoException)
                {
                    throw;
                }
                pendingPayments.AddRange(response.Data);
                limit = response.Data.Count;
                offset += response.Data.Count;
            }
            while (response.Pagination != null && response.Pagination.Next != null);
            return pendingPayments;
        }

        public static string? FindFriend(string recipient, List<VenmoUser> friends)
        {
            foreach (var friend in friends)
            {
                if (friend.Username.ToUpper() == recipient.ToUpper())
                {
                    return friend.Id;
                }
            }
            return null;
        }

        private async Task<HttpResponseMessage> Get(Url url)
        {
            url.SetQueryParam("access_token", AccessToken);
            HttpResponseMessage responseMessage = await httpClient.GetAsync(url);
            logger.LogInformation(await responseMessage.Content.ReadAsStringAsync());
            return responseMessage;
        }

        private async Task<HttpResponseMessage> Post(Url url, HttpContent httpContent)
        {
            HttpResponseMessage responseMessage = await httpClient.PostAsync(url, httpContent);
            logger.LogInformation(await responseMessage.Content.ReadAsStringAsync());
            return responseMessage;
        }

        private async Task<HttpResponseMessage> Put(Url url, HttpContent httpContent)
        {
            HttpResponseMessage responseMessage = await httpClient.PutAsync(url, httpContent);
            logger.LogInformation(await responseMessage.Content.ReadAsStringAsync());
            return responseMessage;
        }

        private T GetObject<T>(string responseString)
        {
            JObject o = JObject.Parse(responseString);
            if (o["error"] != null)
            {
                throw CreateVenmoError(o);
            }
            else
            {
                return o.ToObject<T>()!;
            }
        }

        private VenmoException CreateVenmoError(JObject o)
        {
            return new VenmoException((string)o["error"]!["message"]!);
        }
    }
}
