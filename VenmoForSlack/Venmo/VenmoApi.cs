using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
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

        /// <summary>
        /// Venmo rejects requests that don't look like they're coming from one of their own clients so
        /// pretend to be the Venmo iOS app.
        /// </summary>
        private const string VenmoUserAgent = "Venmo/26.1.0 (iPhone; iOS 18.6.2; Scale/3.0)";
        private const string VenmoAccept = "application/json; charset=utf-8";
        private const string VenmoAcceptLanguage = "en-US;q=1.0";

        /// <summary>
        /// Error code Venmo returns (with a 403) when it won't issue an access token to the device
        /// the login was attempted from.
        /// </summary>
        public const int UntrustedDeviceErrorCode = 240;

        /// <summary>
        /// Prefix Venmo uses for the device ids it hands out to its own clients.
        /// </summary>
        private const string DeviceIdPrefix = "fp01-";

        /// <summary>
        /// Matches secrets in urls (access_token=abc) and JSON bodies ("access_token": "abc") so they
        /// can be kept out of logs.
        /// </summary>
        private static readonly Regex SecretRegex = new Regex(
            @"(access_token|refresh_token|client_secret)(=|""\s*:\s*"")([^&""\s]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly ILogger logger;
        private HttpClient httpClient;
        private readonly string sessionId;
        public string? AccessToken { private get; set; }
        public string? UserId { get; set; }

        public VenmoApi(ILogger<VenmoApi> logger) : this(logger, new HttpClient())
        {
        }

        public VenmoApi(ILogger<VenmoApi> logger, HttpClient httpClient)
        {
            this.logger = logger;
            this.httpClient = httpClient;
            sessionId = Random.Shared.NextInt64(0, long.MaxValue).ToString();
        }

        /// <summary>
        /// Generates a device id in the format Venmo currently hands out to its own clients.
        /// Randomly generated bare UUIDs are rejected by Venmo when authenticating.
        /// </summary>
        /// <returns>A device id</returns>
        public static string GenerateDeviceId()
        {
            return $"{DeviceIdPrefix}{Guid.NewGuid()}";
        }

        /// <summary>
        /// Cleans up a device id pasted by a user. Cookie viewers and JSON blobs tend to bring along
        /// quotes and whitespace.
        /// </summary>
        /// <param name="deviceId">The pasted device id</param>
        /// <returns>The cleaned up device id</returns>
        public static string NormalizeDeviceId(string? deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return string.Empty;
            }
            return deviceId.Trim().Trim('"', '\'', ',', ';').Trim();
        }

        /// <summary>
        /// Checks if the given device id is in the format Venmo currently expects, which is
        /// <see cref="DeviceIdPrefix"/> followed by a GUID. Users copy these out of their browser by
        /// hand so this also catches values that were only partially copied.
        /// </summary>
        /// <param name="deviceId">The device id to check</param>
        /// <returns>True if the device id is in the expected format</returns>
        public static bool IsSupportedDeviceId(string? deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId) ||
                !deviceId.StartsWith(DeviceIdPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return Guid.TryParseExact(deviceId[DeviceIdPrefix.Length..], "D", out _);
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
                { "client_id", "1" },
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
                { "client_id", "1" }
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
            string responseString = await responseMessage.Content.ReadAsStringAsync();
            if (responseMessage.IsSuccessStatusCode)
            {
                VenmoAuthResponse response = JsonConvert.DeserializeObject<VenmoAuthResponse>(responseString)!;
                AccessToken = response.AccessToken;
                // User id will not be null here, it's returned by the Venmo API
                UserId = response.User?.Id;
                return response;
            }
            else if (responseMessage.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                throw CreateVenmoError(responseString);
            }
            else if (responseMessage.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                VenmoException venmoException = CreateVenmoError(responseString);
                if (venmoException.Error != null && venmoException.Error.Code.HasValue)
                {
                    if (venmoException.Error.Code != 81109)
                    {
                        logger.LogWarning($"Unexpected 2FA error code. Expected: 81109. Actual: {venmoException.Error.Code.Value}");
                    }
                }
                if (!responseMessage.Headers.TryGetValues("venmo-otp-secret", out IEnumerable<string>? secretValues))
                {
                    logger.LogError("venmo-otp-secret doesn't exist in error headers");
                    throw venmoException;
                }
                List<string> secretList = secretValues.ToList();
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
                logger.LogError($"Unexpected response while authenticating to Venmo. " +
                    $"Status code: {(int)responseMessage.StatusCode}. Message: {responseString}");
                throw CreateVenmoError(responseString);
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

        /// <summary>
        /// Gets the authenticated user's account, including their profile and balance.
        /// </summary>
        /// <remarks>
        /// Venmo retired /v1/me, /v1/account returns the same data.
        /// </remarks>
        /// <returns>The authenticated user's account</returns>
        public async Task<MeResponse> GetMe()
        {
            Url url = new Url(BaseUrl).AppendPathSegment("account");
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
                UserId = me.Data!.User!.Id;
            }
            Url url = new Url(BaseUrl).AppendPathSegments("users", UserId, "friends")
                .SetQueryParams(new
                {
                    limit = limit,
                    offset = offset,
                    access_token = AccessToken
                });
            HttpResponseMessage responseMessage = await Get(url);
            FriendsResponse response = GetObject<FriendsResponse>(await responseMessage.Content.ReadAsStringAsync());
            return response;
        }

        public async virtual Task<List<VenmoUser>> GetAllFriends()
        {
            MeResponse me = await GetMe();
            int limit = me.Data!.User!.FriendsCount!.Value;
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
                    // Venmo may now require a payment eligibility token obtained from
                    // /v1/protection/eligibility before a payment can be created. Log the error code
                    // so we can tell that apart from an ordinary payment failure.
                    logger.LogError($"Venmo payment failed. Status code: {(int)responseMessage.StatusCode}. " +
                        $"Error code: {ex.Error?.Code?.ToString() ?? "none"}. " +
                        $"Action: {action}. Recipient type: {splitRecipient[0]}. Audience: {venmoAudience}. " +
                        $"Message: {ex.Message}");
                    response = new VenmoPaymentWithBalanceResponse()
                    {
                        Error = ex.Message
                    };
                }
                responses.Add(response);
            }
            return responses;
        }

        public async Task<VenmoPaymentPendingResponse> GetPending(int? limit = null, string? before = null)
        {
            Url url = new Url(BaseUrl).AppendPathSegment("payments")
                .SetQueryParams(new
                {
                    status = "pending",
                    access_token = AccessToken
                });

            if (limit != null)
            {
                url.SetQueryParam("limit", limit.Value);
            }

            if (!string.IsNullOrWhiteSpace(before))
            {
                url.SetQueryParam("before", before);
            }

            HttpResponseMessage responseMessage = await Get(url);
            VenmoPaymentPendingResponse response = GetObject<VenmoPaymentPendingResponse>(await responseMessage.Content.ReadAsStringAsync());
            return response;
        }

        public async Task<List<VenmoPaymentPending>> GetAllPayments()
        {
            int limit = 100;
            string? before = null;
            List<VenmoPaymentPending> pendingPayments = new List<VenmoPaymentPending>();
            VenmoPaymentPendingResponse? response = null;
            do
            {
                try
                {
                    response = await GetPending(limit, before);
                }
                catch (VenmoException)
                {
                    throw;
                }
                pendingPayments.AddRange(response.Data!);
                if (response.Pagination != null && response.Pagination.Next != null)
                {
                    var query = HttpUtility.ParseQueryString(new Uri(response.Pagination.Next).Query);
                    before = query.Get("before");
                }
                limit = response.Data!.Count;
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
                UserId = me.Data!.User!.Id;
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

        /// <summary>
        /// Find the Venmo id of the given user from the given list of users.
        /// Intended to be used to find the Venmo id of a friend from the list of friends retrieved from <see cref="GetAllFriends()"/>
        /// </summary>
        /// <param name="recipient">User to find</param>
        /// <param name="friends">List of users</param>
        /// <returns>The given user's Venmo id</returns>
        public static string? FindFriendId(string recipient, List<VenmoUser> friends)
        {
            foreach (var friend in friends)
            {
                if (string.Equals(friend.Username, recipient, StringComparison.OrdinalIgnoreCase))
                {
                    return friend.Id;
                }
            }
            return null;
        }

        private async Task<HttpResponseMessage> Get(Url url)
        {
            url.SetQueryParam("access_token", AccessToken);
            HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Get, url.ToString());
            HttpResponseMessage responseMessage = await Send(requestMessage, retryOnServerError: false);
            await LogRequest(requestMessage, responseMessage, null);
            return responseMessage;
        }

        private async Task<HttpResponseMessage> Post(Url url, HttpContent httpContent)
        {
            HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Post, url.ToString())
            {
                Content = httpContent
            };
            string requestBody = await httpContent.ReadAsStringAsync();
            HttpResponseMessage responseMessage = await Send(requestMessage);
            await LogRequest(requestMessage, responseMessage, requestBody);
            return responseMessage;
        }

        private async Task<HttpResponseMessage> Put(Url url, HttpContent httpContent)
        {
            HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Put, url.ToString())
            {
                Content = httpContent
            };
            string requestBody = await httpContent.ReadAsStringAsync();
            HttpResponseMessage responseMessage = await Send(requestMessage, retryOnServerError: false);
            await LogRequest(requestMessage, responseMessage, requestBody);
            return responseMessage;
        }

        /// <summary>
        /// Logs a request and its response. Access tokens, refresh tokens, and client secrets are
        /// redacted. Successful requests are logged at information level, failed requests at error
        /// level so Venmo API changes are easy to spot.
        /// </summary>
        /// <param name="request">The request that was sent</param>
        /// <param name="response">The response that was received</param>
        /// <param name="requestBody">The body that was sent, if any</param>
        private async Task LogRequest(HttpRequestMessage request, HttpResponseMessage response, string? requestBody)
        {
            string responseBody = await response.Content.ReadAsStringAsync();
            string requestLine = $"{request.Method} {Redact(request.RequestUri?.ToString())}";
            string bodyLine = string.IsNullOrEmpty(requestBody) ? string.Empty : $"\nRequest body: {Redact(requestBody)}";
            string message = $"Venmo API {requestLine} responded {(int)response.StatusCode} " +
                $"({response.Content.Headers.ContentType?.MediaType}){bodyLine}\nResponse body: {Redact(responseBody)}";
            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation(message);
            }
            else
            {
                logger.LogError(message);
            }
        }

        /// <summary>
        /// Redacts secrets so they don't end up in logs.
        /// </summary>
        /// <param name="value">The value to redact</param>
        /// <returns>The value with any secrets replaced</returns>
        private static string Redact(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }
            return SecretRegex.Replace(value, match => $"{match.Groups[1].Value}{match.Groups[2].Value}<redacted>");
        }

        private async Task<HttpResponseMessage> Send(HttpRequestMessage request)
        {
            return await Send(request, retryOnServerError: true);
        }

        private async Task<HttpResponseMessage> Send(HttpRequestMessage request, bool retryOnServerError)
        {
            AddDefaultHeaders(request);
            if (!retryOnServerError)
            {
                // Don't log response as this could contain an access token that doesn't expire
                return await httpClient.SendAsync(request);
            }

            HttpResponseMessage responseMessage = await Polly.Policy
                .HandleResult<HttpResponseMessage>(response => response.StatusCode == System.Net.HttpStatusCode.InternalServerError)
                .WaitAndRetryAsync(Backoff.DecorrelatedJitterBackoffV2(TimeSpan.FromSeconds(5), 30))
                .ExecuteAsync(async () => await httpClient.SendAsync(request));
            // Don't log response as this could contain an access token that doesn't expire
            return responseMessage;
        }

        /// <summary>
        /// Adds the headers the Venmo apps send on every request. Venmo blocks requests that don't
        /// look like they're coming from one of their own clients.
        /// </summary>
        /// <param name="request">The request to add headers to</param>
        private void AddDefaultHeaders(HttpRequestMessage request)
        {
            if (!request.Headers.Contains("User-Agent"))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", VenmoUserAgent);
            }
            if (!request.Headers.Contains("Accept"))
            {
                request.Headers.TryAddWithoutValidation("Accept", VenmoAccept);
            }
            if (!request.Headers.Contains("Accept-Language"))
            {
                request.Headers.TryAddWithoutValidation("Accept-Language", VenmoAcceptLanguage);
            }
            if (!request.Headers.Contains("X-Session-ID"))
            {
                request.Headers.TryAddWithoutValidation("X-Session-ID", sessionId);
            }
        }

        private T GetObject<T>(string responseString)
        {
            JObject o;
            try
            {
                o = JObject.Parse(responseString);
            }
            catch (JsonException ex)
            {
                logger.LogError(ex, "Unable to parse Venmo response");
                throw new VenmoException("Unexpected response from Venmo");
            }

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
            // Deserialize the whole error so the error code is preserved, callers use it to tell
            // Venmo API changes apart from ordinary failures.
            VenmoErrorObject? error = o["error"]!.ToObject<VenmoErrorObject>();
            if (error == null || string.IsNullOrEmpty(error.Message))
            {
                return new VenmoException("Unexpected response from Venmo");
            }
            return new VenmoException(error);
        }

        private VenmoException CreateVenmoError(string responseString)
        {
            VenmoErrorWrapper<VenmoErrorObject>? error = null;
            try
            {
                error = JsonConvert.DeserializeObject<VenmoErrorWrapper<VenmoErrorObject>>(responseString);
            }
            catch (JsonException ex)
            {
                logger.LogError(ex, "Unable to parse Venmo error response");
            }

            if (error?.Error == null)
            {
                return new VenmoException("Unexpected response from Venmo");
            }

            if (string.IsNullOrEmpty(error.Error.Message))
            {
                error.Error.Message = "Unexpected response from Venmo";
            }
            return new VenmoException(error.Error);
        }
    }
}
