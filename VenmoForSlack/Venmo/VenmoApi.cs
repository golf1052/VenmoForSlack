using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Net.Http;
using Flurl;
using Newtonsoft.Json;
using VenmoForSlack.Venmo.Models;
using Microsoft.Extensions.Logging;

namespace VenmoForSlack.Venmo
{
    public class VenmoApi
    {
        private const string BaseUrl = "https://api.venmo.com/v1/";
        private readonly ILogger logger;
        private HttpClient httpClient;
        private string? AccessToken { get; set; }
        private string? UserId { get; set; }

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

        public async Task<MeResponse> GetMe()
        {
            Url url = new Url(BaseUrl).AppendPathSegment("me");
            HttpResponseMessage responseMessage = await Get(url);
            MeResponse response = JsonConvert.DeserializeObject<MeResponse>(await responseMessage.Content.ReadAsStringAsync());
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
            FriendsResponse response = JsonConvert.DeserializeObject<FriendsResponse>(await responseMessage.Content.ReadAsStringAsync());
            return response;
        }

        public async Task<List<VenmoUser>> GetAllFriends()
        {
            MeResponse me = await GetMe();
            int limit = me.Data.User.FriendsCount.Value;
            int offset = 0;
            List<VenmoUser> friends = new List<VenmoUser>();
            FriendsResponse friendsResponse = null;
            do
            {
                friendsResponse = await GetFriends(limit, offset);
                friends.AddRange(friendsResponse.Data);
                limit = friendsResponse.Data.Count;
                offset += friendsResponse.Data.Count;
            }
            while (friendsResponse.Pagination.Next != null);
            return friends;
        }

        public async Task PostPayment(float amount,
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

            List<VenmoUser> friendsList = null;

            foreach (var recipient in recipients)
            {
                Dictionary<string, string> data = new Dictionary<string, string>()
                {
                    { "access_token", AccessToken }
                };

                if (recipient.StartsWith("phone:"))
                {
                    data.Add("phone", recipient.Substring(6));
                }
                else if (recipient.StartsWith("email:"))
                {
                    data.Add("email", recipient.Substring(6));
                }
                else
                {
                    // TODO: Check alias
                    // TODO: Check cache
                    if (friendsList == null)
                    {
                        friendsList = await GetAllFriends();
                    }
                    string id = FindFriend(recipient, friendsList);
                    if (id == null)
                    {
                        // Exception: You are not friends with {recipient}
                        continue;
                    }
                    data.Add("user_id", id);
                }
                data.Add("note", note);
                data.Add("amount", amountString);
                data.Add("audience", venmoAudience.ToString());
                HttpResponseMessage responseMessage = await Post(url, new FormUrlEncodedContent(data));
            }
        }

        public async Task GetPending(VenmoAction action)
        {
            Url url = new Url(BaseUrl).AppendPathSegment("payments")
                .SetQueryParams(new {
                    status = "pending",
                    access_token = AccessToken
                });
            HttpResponseMessage responseMessage = await Get(url);
        }

        private string FindFriend(string recipient, List<VenmoUser> friends)
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
    }
}
