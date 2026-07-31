using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Contrib.HttpClient;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VenmoForSlack.Venmo;
using VenmoForSlack.Venmo.Models;
using VenmoForSlack.Venmo.Models.Responses;
using Xunit;

namespace VenmoForSlack.Tests
{
    public class VenmoApiTests
    {
        private VenmoApi venmoApi;
        private Mock<HttpMessageHandler> httpMessageHandler;

        public VenmoApiTests()
        {
            httpMessageHandler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
            venmoApi = new VenmoApi(NullLogger<VenmoApi>.Instance, httpMessageHandler.CreateClient());
        }

        [Fact]
        public async Task AuthorizeWithUsernameAndPassword_SucceedsWith401()
        {
            VenmoErrorWrapper<VenmoErrorObject> unauthorizedError = new VenmoErrorWrapper<VenmoErrorObject>()
            {
                Error = new VenmoErrorObject()
                {
                    Url = "https://venmo.com/two-factor",
                    Message = "Additional authentication is required",
                    Code = 81109,
                    Title = "Error"
                }
            };
            httpMessageHandler
                .SetupRequest(HttpMethod.Post, "https://api.venmo.com/v1/oauth/access_token")
                .ReturnsResponse(System.Net.HttpStatusCode.Unauthorized,
                    new StringContent(JsonConvert.SerializeObject(unauthorizedError)),
                    message =>
                    {
                        message.Headers.Add("venmo-otp-secret", "test");
                    });
            httpMessageHandler
                .SetupRequest(HttpMethod.Post, "https://api.venmo.com/v1/account/two-factor/token", request =>
                {
                    string requestDeviceId = request.Headers.GetValues("device-id").ToList()[0];
                    string requestVenmoOtpSecret = request.Headers.GetValues("venmo-otp-secret").ToList()[0];
                    Assert.Equal("device", requestDeviceId);
                    Assert.Equal("test", requestVenmoOtpSecret);
                    return requestDeviceId == "device" && requestVenmoOtpSecret == "test";
                })
                .ReturnsResponse(System.Net.HttpStatusCode.OK);
            try
            {
                await venmoApi.AuthorizeWithUsernameAndPassword("test", "password", "device");
            }
            catch (VenmoException ex)
            {
                Assert.NotNull(ex.Error);
                Assert.Equal(unauthorizedError.Error.Message, ex.Error!.Message);
                Assert.Equal("test", ex.VenmoOtpSecret);
            }
        }

        [Fact]
        public async Task AuthorizeWithUsernameAndPassword_SendsVenmoAppHeaders()
        {
            VenmoAuthResponse expectedResponse = new VenmoAuthResponse()
            {
                AccessToken = "0_0",
                User = new VenmoUser()
                {
                    Id = "auser"
                }
            };
            httpMessageHandler
                .SetupRequest(HttpMethod.Post, "https://api.venmo.com/v1/oauth/access_token", async request =>
                {
                    Assert.Equal("Venmo/26.1.0 (iPhone; iOS 18.6.2; Scale/3.0)",
                        string.Join(' ', request.Headers.GetValues("User-Agent")));
                    Assert.Equal("application/json; charset=utf-8",
                        string.Join(' ', request.Headers.GetValues("Accept")));
                    // HttpClient normalizes the Accept-Language separator when re-serializing
                    Assert.Equal("en-US;q=1.0",
                        string.Join(' ', request.Headers.GetValues("Accept-Language")).Replace("; ", ";"));
                    Assert.NotEmpty(request.Headers.GetValues("X-Session-ID").ToList()[0]);
                    Assert.Equal("fp01-device", request.Headers.GetValues("device-id").ToList()[0]);

                    JObject body = JObject.Parse(await request.Content!.ReadAsStringAsync());
                    // Venmo rejects the client id when it's sent as a number
                    Assert.Equal(JTokenType.String, body["client_id"]!.Type);
                    Assert.Equal("1", (string?)body["client_id"]);
                    Assert.Equal("test", (string?)body["phone_email_or_username"]);
                    Assert.Equal("password", (string?)body["password"]);
                    return true;
                })
                .ReturnsResponse(System.Net.HttpStatusCode.OK, message =>
                {
                    message.Content = new StringContent(JsonConvert.SerializeObject(expectedResponse), Encoding.UTF8, "application/json");
                });

            VenmoAuthResponse response = await venmoApi.AuthorizeWithUsernameAndPassword("test", "password", "fp01-device");
            Assert.Equal(expectedResponse.AccessToken, response.AccessToken);
        }

        [Fact]
        public async Task AuthorizeWithUsernameAndPassword_ThrowsHelpfulErrorOn403()
        {
            VenmoErrorWrapper<VenmoErrorObject> forbiddenError = new VenmoErrorWrapper<VenmoErrorObject>()
            {
                Error = new VenmoErrorObject()
                {
                    Message = "OAuth2 Exception: Unable to complete your request. Please try again later.",
                    Code = 240,
                    Title = "Error"
                }
            };
            httpMessageHandler
                .SetupRequest(HttpMethod.Post, "https://api.venmo.com/v1/oauth/access_token")
                .ReturnsResponse(System.Net.HttpStatusCode.Forbidden,
                    new StringContent(JsonConvert.SerializeObject(forbiddenError)));

            VenmoException ex = await Assert.ThrowsAsync<VenmoException>(async () =>
                await venmoApi.AuthorizeWithUsernameAndPassword("test", "password", "fp01-device"));
            Assert.Contains(forbiddenError.Error.Message, ex.Message);
            Assert.Null(ex.VenmoOtpSecret);
        }

        [Fact]
        public async Task AuthorizeWithUsernameAndPassword_ThrowsWhenOtpSecretIsMissing()
        {
            VenmoErrorWrapper<VenmoErrorObject> unauthorizedError = new VenmoErrorWrapper<VenmoErrorObject>()
            {
                Error = new VenmoErrorObject()
                {
                    Message = "Additional authentication is required",
                    Code = 81109,
                    Title = "Error"
                }
            };
            httpMessageHandler
                .SetupRequest(HttpMethod.Post, "https://api.venmo.com/v1/oauth/access_token")
                .ReturnsResponse(System.Net.HttpStatusCode.Unauthorized,
                    new StringContent(JsonConvert.SerializeObject(unauthorizedError)));

            VenmoException ex = await Assert.ThrowsAsync<VenmoException>(async () =>
                await venmoApi.AuthorizeWithUsernameAndPassword("test", "password", "fp01-device"));
            Assert.Equal(unauthorizedError.Error.Message, ex.Message);
            Assert.Null(ex.VenmoOtpSecret);
        }

        [Fact]
        public async Task GetMe_UsesAccountEndpoint()
        {
            // Venmo retired /v1/me
            httpMessageHandler
                .SetupRequest(HttpMethod.Get, request => request.RequestUri!.ToString().StartsWith("https://api.venmo.com/v1/account"))
                .ReturnsResponse(System.Net.HttpStatusCode.OK,
                    new StringContent("{\"data\":{\"user\":{\"id\":\"auser\",\"friends_count\":3},\"balance\":\"6.42\"}}"));

            MeResponse response = await venmoApi.GetMe();
            Assert.Equal("6.42", response.Data!.Balance);
            Assert.Equal("auser", response.Data!.User!.Id);
        }

        [Fact]
        public async Task GetMe_ThrowsVenmoExceptionOnNonJsonResponse()
        {
            httpMessageHandler
                .SetupRequest(HttpMethod.Get, request => request.RequestUri!.ToString().StartsWith("https://api.venmo.com/v1/account"))
                .ReturnsResponse(System.Net.HttpStatusCode.NotFound,
                    new StringContent("<html><body>Sorry, the page you requested does not exist!</body></html>"));

            await Assert.ThrowsAsync<VenmoException>(async () => await venmoApi.GetMe());
        }

        [Fact]
        public async Task PostPayment_PreservesVenmoErrorCode()
        {
            httpMessageHandler
                .SetupRequest(HttpMethod.Post, request => request.RequestUri!.ToString().StartsWith("https://api.venmo.com/v1/payments"))
                .ReturnsResponse(System.Net.HttpStatusCode.BadRequest,
                    new StringContent("{\"error\":{\"message\":\"Payment eligibility required\",\"code\":1384,\"title\":\"Error\"}}"));

            List<VenmoPaymentWithBalanceResponse> responses = await venmoApi.PostPayment(1.00,
                "test",
                new List<string>() { "user_id:auser" },
                VenmoAction.Pay);

            Assert.Single(responses);
            Assert.Equal("Payment eligibility required", responses[0].Error);
        }

        [Fact]
        public void NormalizeDeviceId_StripsPastedJunk()
        {
            const string expected = "fp01-211e6584-6663-4e91-843e-7a1429a065c1";
            Assert.Equal(expected, VenmoApi.NormalizeDeviceId(expected));
            Assert.Equal(expected, VenmoApi.NormalizeDeviceId($"  {expected}  "));
            Assert.Equal(expected, VenmoApi.NormalizeDeviceId($"\"{expected}\""));
            Assert.Equal(expected, VenmoApi.NormalizeDeviceId($"'{expected}',"));
            Assert.Equal(string.Empty, VenmoApi.NormalizeDeviceId(null));
            Assert.Equal(string.Empty, VenmoApi.NormalizeDeviceId("   "));
        }

        [Fact]
        public void GenerateDeviceId_IsInVenmoFormat()
        {
            string deviceId = VenmoApi.GenerateDeviceId();
            Assert.StartsWith("fp01-", deviceId);
            Assert.True(Guid.TryParse(deviceId["fp01-".Length..], out _));
            Assert.True(VenmoApi.IsSupportedDeviceId(deviceId));
            Assert.True(VenmoApi.IsSupportedDeviceId("fp01-211e6584-6663-4e91-843e-7a1429a065c1"));
            Assert.False(VenmoApi.IsSupportedDeviceId(Guid.NewGuid().ToString()));
            Assert.False(VenmoApi.IsSupportedDeviceId(null));
            Assert.False(VenmoApi.IsSupportedDeviceId(string.Empty));
            // Partially copied out of a browser
            Assert.False(VenmoApi.IsSupportedDeviceId("fp01-211e6584-6663-4e91-843e"));
            Assert.False(VenmoApi.IsSupportedDeviceId("fp01-"));
        }

        [Fact]
        public async Task AuthorizeWith2FA_Succeeds()
        {
            const string deviceId = "device";
            const string venmoOtpSecret = "test";
            const string otp = "123456";
            VenmoAuthResponse expectedResponse = new VenmoAuthResponse()
            {
                AccessToken = "0_0",
                User = new VenmoUser()
                {
                    Id = "auser"
                }
            };
            httpMessageHandler
                .SetupRequest(HttpMethod.Post, "https://api.venmo.com/v1/oauth/access_token", request =>
                {
                    string requestDeviceId = request.Headers.GetValues("device-id").ToList()[0];
                    string requestVenmoOtpSecret = request.Headers.GetValues("venmo-otp-secret").ToList()[0];
                    string requestOtp = request.Headers.GetValues("venmo-otp").ToList()[0];
                    Assert.Equal(deviceId, requestDeviceId);
                    Assert.Equal(venmoOtpSecret, requestVenmoOtpSecret);
                    Assert.Equal(otp, requestOtp);
                    return requestDeviceId == deviceId && requestVenmoOtpSecret == venmoOtpSecret && requestOtp == otp;
                })
                .ReturnsResponse(System.Net.HttpStatusCode.OK, message =>
                {
                    message.Content = new StringContent(JsonConvert.SerializeObject(expectedResponse), Encoding.UTF8, "application/json");
                });

            VenmoAuthResponse response = await venmoApi.AuthorizeWith2FA(otp, venmoOtpSecret, deviceId);
            Assert.Equal(expectedResponse.AccessToken, response.AccessToken);
            Assert.NotNull(response.User);
            Assert.Equal(expectedResponse.User.Id, response.User!.Id);
        }
    }
}
