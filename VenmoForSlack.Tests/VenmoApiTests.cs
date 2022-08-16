using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Contrib.HttpClient;
using Newtonsoft.Json;
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
