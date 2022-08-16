using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Flurl;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Polly;
using Polly.Contrib.WaitAndRetry;
using VenmoForSlack.Venmo.Models;
using VenmoForSlack.Venmo.Models.Responses;

namespace VenmoForSlack.Venmo
{
    public class VenmoApi
    {
        private const string BaseUrl = "https://api.venmo.com/v1/";
        private readonly ILogger logger;
        private HttpClient httpClient;
        public string? AccessToken { private get; set; }
        public string? UserId { get; set; }

        public VenmoApi(ILogger<VenmoApi> logger) : this(logger, new HttpClient())
        {
        }

        public VenmoApi(ILogger<VenmoApi> logger, HttpClient httpClient)
        {
            this.logger = logger;
            this.httpClient = httpClient;
        }

        public static string GetAuthorizeUrl()
        {
            // RIP OAuth
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
            VenmoAuthResponse response = JsonConvert.DeserializeObject<VenmoAuthResponse>(await responseMessage.Content.ReadAsStringAsync())!;
            AccessToken = response.AccessToken;
            // User id will not be null here, it's returned by the Venmo API
            UserId = response.User?.Id;
            return response;
        }

        /// <summary>
        /// Authenticates a user to Venmo with a device id, username, and password.
        /// </summary>
        /// <param name="phoneEmailOrUsername">User's phone number, email, or username</param>
        /// <param name="password">User's password</param>
        /// <param name="deviceId">Device id</param>
        /// <returns>A Venmo authentication response</returns>
        /// <exception cref="VenmoException">Venmo exception or 2-factor authorization required</exception>
        public async Task<VenmoAuthResponse> AuthorizeWithUsernameAndPassword(string phoneEmailOrUsername, string password, string deviceId)
        {
            Url url = new Url(BaseUrl).AppendPathSegments("oauth", "access_token");
            JObject body = new JObject()
            {
                { "client_id", 1 },
                { "phone_email_or_username", phoneEmailOrUsername },
                { "password", password }
            };
            HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Post, url);
            requestMessage.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
            return await Authorize(deviceId, requestMessage);
        }

        public async Task<VenmoAuthResponse> AuthorizeWith2FA(string otp, string venmoOtpSecret, string deviceId)
        {
            Url url = new Url(BaseUrl).AppendPathSegments("oauth", "access_token");
            JObject body = new JObject()
            {
                { "client_id", 1 }
            };
            HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Post, url);
            requestMessage.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
            requestMessage.Headers.Add("venmo-otp", otp);
            requestMessage.Headers.Add("venmo-otp-secret", venmoOtpSecret);
            return await Authorize(deviceId, requestMessage);
        }

        private async Task<VenmoAuthResponse> Authorize(string deviceId, HttpRequestMessage requestMessage)
        {
            requestMessage.Headers.Add("device-id", deviceId);
            HttpResponseMessage responseMessage = await Send(requestMessage);
            if (responseMessage.IsSuccessStatusCode)
            {
                VenmoAuthResponse response = JsonConvert.DeserializeObject<VenmoAuthResponse>(await responseMessage.Content.ReadAsStringAsync())!;
                AccessToken = response.AccessToken;
                // User id will not be null here, it's returned by the Venmo API
                UserId = response.User?.Id;
                return response;
            }
            else if (responseMessage.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                throw CreateVenmoError(await responseMessage.Content.ReadAsStringAsync());
            }
            else if (responseMessage.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                VenmoException venmoException = CreateVenmoError(await responseMessage.Content.ReadAsStringAsync());
                if (venmoException.Error != null && venmoException.Error.Code.HasValue)
                {
                    if (venmoException.Error.Code != 81109)
                    {
                        logger.LogWarning($"Unexpected 2FA error code. Expected: 81109. Actual: {venmoException.Error.Code.Value}");
                    }
                }
                List<string> secretList = responseMessage.Headers.GetValues("venmo-otp-secret").ToList();
                if (secretList.Count == 0)
                {
                    logger.LogError("venmo-otp-secret doesn't exist in error headers");
                    throw venmoException;
                }
                string venmoOtpSecret = secretList[0];
                venmoException.VenmoOtpSecret = venmoOtpSecret;
                await SendTwoFactorCode(venmoOtpSecret, deviceId);
                throw venmoException;
            }
            else
            {
                throw CreateVenmoError(await responseMessage.Content.ReadAsStringAsync());
            }
        }

        public async Task SendTwoFactorCode(string venmoOtpSecret, string deviceId)
        {
            Url url = new Url(BaseUrl).AppendPathSegments("account", "two-factor", "token");
            JObject body = new JObject()
            {
                { "via", "sms" }
            };
            HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Post, url);
            requestMessage.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
            requestMessage.Headers.Add("device-id", deviceId);
            requestMessage.Headers.Add("venmo-otp-secret", venmoOtpSecret);
            HttpResponseMessage responseMessage = await Send(requestMessage);
            if (responseMessage.IsSuccessStatusCode)
            {
                return;
            }
            else if (responseMessage.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                throw CreateVenmoError(await responseMessage.Content.ReadAsStringAsync());
            }
        }

        public async Task<VenmoAuthResponse> RefreshAuth(string refreshToken)
        {
            logger.LogInformation("Attempting to refresh Venmo token");
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
            VenmoAuthResponse response = JsonConvert.DeserializeObject<VenmoAuthResponse>(responseString)!;
            AccessToken = response.AccessToken;
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
            if (string.IsNullOrEmpty(UserId))
            {
                // If UserId hasn't been loaded yet then retrieve it
                MeResponse me = await GetMe();
                UserId = me.Data!.User.Id;
            }
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
            int limit = me.Data!.User.FriendsCount!.Value;
            int offset = 0;
            List<VenmoUser> friends = new List<VenmoUser>();
            FriendsResponse? friendsResponse;
            do
            {
                friendsResponse = await GetFriends(limit, offset);
                friends.AddRange(friendsResponse.Data!);
                limit = friendsResponse.Data!.Count;
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
            List<string> recipients,
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
                string[] splitRecipient = recipient.Split(':');
                if (splitRecipient.Length != 2)
                {
                    responses.Add(new VenmoPaymentWithBalanceResponse()
                    {
                        Error = $"Malformed recipient: {recipient}"
                    });
                    continue;
                }
                Dictionary<string, string> data = new Dictionary<string, string>()
                {
                    { "access_token", AccessToken! },
                    { splitRecipient[0], splitRecipient[1] }
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
                pendingPayments.AddRange(response.Data!);
                limit = response.Data!.Count;
                offset += response.Data.Count;
            }
            while (response.Pagination != null && response.Pagination.Next != null);
            return pendingPayments;
        }

        public async Task<VenmoUserSearchResponse> SearchUsers(string query, int limit = 50, int offset = 0)
        {
            Url url = new Url(BaseUrl).AppendPathSegment("users")
                .SetQueryParam("query", query)
                .SetQueryParam("limit", limit)
                .SetQueryParam("offset", offset)
                .SetQueryParam("access_token", AccessToken);
            HttpResponseMessage responseMessage = await Get(url);
            VenmoUserSearchResponse response = GetObject<VenmoUserSearchResponse>(await responseMessage.Content.ReadAsStringAsync());
            return response;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="beforeId"></param>
        /// <param name="afterId"></param>
        /// <remarks>Only beforeId or afterId can be set, not both</remarks>
        /// <returns></returns>
        public async Task<VenmoTransactionResponse> GetTransactions(string? beforeId = null, string? afterId = null)
        {
            if (string.IsNullOrEmpty(UserId))
            {
                // If UserId hasn't been loaded yet then retrieve it
                MeResponse me = await GetMe();
                UserId = me.Data!.User.Id;
            }
            int limit = 50;
            Url url = new Url(BaseUrl).AppendPathSegments("stories", "target-or-actor", UserId)
                .SetQueryParams(new
                {
                    limit = limit,
                    access_token = AccessToken
                });

            if (beforeId != null)
            {
                url.SetQueryParam("before_id", beforeId);
            }
            else if (afterId != null)
            {
                url.SetQueryParam("after_id", afterId);
            }
            HttpResponseMessage responseMessage = await Get(url);
            VenmoTransactionResponse response = GetObject<VenmoTransactionResponse>(await responseMessage.Content.ReadAsStringAsync());
            return response;
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
            HttpResponseMessage responseMessage = await Polly.Policy
                .HandleResult<HttpResponseMessage>(response => response.StatusCode == System.Net.HttpStatusCode.InternalServerError)
                .WaitAndRetryAsync(Backoff.DecorrelatedJitterBackoffV2(TimeSpan.FromSeconds(5), 30))
                .ExecuteAsync(async () => await httpClient.PostAsync(url, httpContent));
            logger.LogInformation(await responseMessage.Content.ReadAsStringAsync());
            return responseMessage;
        }

        private async Task<HttpResponseMessage> Put(Url url, HttpContent httpContent)
        {
            HttpResponseMessage responseMessage = await httpClient.PutAsync(url, httpContent);
            logger.LogInformation(await responseMessage.Content.ReadAsStringAsync());
            return responseMessage;
        }

        private async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            HttpResponseMessage responseMessage = await Polly.Policy
                .HandleResult<HttpResponseMessage>(response => response.StatusCode == System.Net.HttpStatusCode.InternalServerError)
                .WaitAndRetryAsync(Backoff.DecorrelatedJitterBackoffV2(TimeSpan.FromSeconds(5), 30))
                .ExecuteAsync(async () => await httpClient.SendAsync(request));
            // Don't log response as this could contain an access token that doesn't expire
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

        private VenmoException CreateVenmoError(string responseString)
        {
            VenmoErrorWrapper<VenmoErrorObject> error =
                JsonConvert.DeserializeObject<VenmoErrorWrapper<VenmoErrorObject>>(responseString)!;
            return new VenmoException(error.Error!);
        }
    }
}
