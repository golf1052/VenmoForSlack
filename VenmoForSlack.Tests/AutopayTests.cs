using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using golf1052.SlackAPI.BlockKit.Blocks;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Moq;
using VenmoForSlack.Database;
using VenmoForSlack.Database.Models;
using VenmoForSlack.Venmo;
using Xunit;

namespace VenmoForSlack.Tests
{
    public class AutopayTests
    {
        private readonly Autopay autopay;
        private readonly Mock<VenmoApi> mockVenmoApi;
        private readonly Mock<MongoDatabase> mockDatabase;
        
        public AutopayTests()
        {
            mockVenmoApi = new Mock<VenmoApi>(NullLogger<VenmoApi>.Instance);
            Mock<IMongoClient> mockMongoClient = new Mock<IMongoClient>();
            Mock<IMongoDatabase> mockMongoDatabase = new Mock<IMongoDatabase>();
            mockMongoClient.Setup(c => c.GetDatabase(It.IsAny<string>(), null))
                .Returns(mockMongoDatabase.Object);
            Mock<IMongoCollection<VenmoUser>> mockVenmoUserCollection = new Mock<IMongoCollection<VenmoUser>>();
            mockMongoDatabase.Setup(db => db.GetCollection<VenmoUser>(It.IsAny<string>(), null))
                .Returns(mockVenmoUserCollection.Object);
            mockDatabase = new Mock<MongoDatabase>(string.Empty, mockMongoClient.Object, NullLogger<MongoDatabase>.Instance);
            autopay = new Autopay(mockVenmoApi.Object, mockDatabase.Object);
        }

        [Theory]
        [InlineData("/venmo autopay add user is test_user and amount is 4.20 and note is test note")]
        [InlineData("/venmo autopay add user is test_user and amount = $4.20 and note == test note")]
        public async Task Parse_Add_Amount_And_Note(string message)
        {
            string[] splitMessage = message.Split(' ');
            Action<string, List<IBlock>?> respondAction = (m, b) =>
            {
                Assert.Equal("Saved autopayment where user is test_user and amount = $4.20 and note is test note", m);
            };

            List<Venmo.Models.VenmoUser> friends = new List<Venmo.Models.VenmoUser>()
            {
                new Venmo.Models.VenmoUser()
                {
                    Username = "test_user",
                    Id = "69"
                }
            };
            mockVenmoApi.Setup(api => api.GetAllFriends().Result)
                .Returns(friends);
            
            VenmoUser venmoUser = new VenmoUser(string.Empty);
            await autopay.Parse(splitMessage, venmoUser, respondAction);

            Assert.NotNull(venmoUser.Autopay);
            Assert.Single(venmoUser.Autopay);
            VenmoAutopay saved = venmoUser.Autopay[0];
            Assert.Equal("test_user", saved.Username);
            Assert.Equal(friends[0].Id, saved.UserId);
            Assert.Equal("=", saved.Comparison);
            Assert.NotNull(saved.Amount);
            Assert.Equal(4.2, saved.Amount.Value, 0.0);
            Assert.Equal("test note", saved.Note);
        }

        [Fact]
        public async Task Parse_Add_Only_User()
        {
            string message = "/venmo autopay add user is test_user";
            string[] splitMessage = message.Split(' ');
            Action<string, List<IBlock>?> respondAction = (m, b) =>
            {
            };

            List<Venmo.Models.VenmoUser> friends = new List<Venmo.Models.VenmoUser>()
            {
                new Venmo.Models.VenmoUser()
                {
                    Username = "test_user",
                    Id = "69"
                }
            };
            mockVenmoApi.Setup(api => api.GetAllFriends().Result)
                .Returns(friends);

            VenmoUser venmoUser = new VenmoUser(string.Empty);
            await autopay.Parse(splitMessage, venmoUser, respondAction);

            Assert.NotNull(venmoUser.Autopay);
            Assert.Single(venmoUser.Autopay);
            VenmoAutopay saved = venmoUser.Autopay[0];
            Assert.Equal("test_user", saved.Username);
            Assert.Equal(friends[0].Id, saved.UserId);
        }

        [Fact]
        public async Task Parse_List_With_Amount_And_Note()
        {
            string message = "/venmo autopay list";
            VenmoUser venmoUser = new VenmoUser(string.Empty)
            {
                Autopay = new List<VenmoAutopay>()
                {
                    new VenmoAutopay("test_user", "1")
                    {
                        Comparison = "=",
                        Amount = 4.2,
                        Note = "test note"
                    }
                }
            };
            
            string[] splitMessage = message.Split(' ');
            Action<string, List<IBlock>?> respondAction = (m, b) =>
            {
                Assert.Equal("1: Automatically accept charges from test_user where amount = $4.20 and note is test note", m);
            };

            await autopay.Parse(splitMessage, venmoUser, respondAction);
        }

        [Fact]
        public async Task Parse_List_With_Only_Amount_And_Only_Note()
        {
            string message = "/venmo autopay list";
            VenmoUser venmoUser = new VenmoUser(string.Empty)
            {
                Autopay = new List<VenmoAutopay>()
                {
                    new VenmoAutopay("test_user_1", "1")
                    {
                        Comparison = "<",
                        Amount = 4.2
                    },
                    new VenmoAutopay("test_user_2", "2")
                    {
                        Note = "test note"
                    }
                }
            };

            string[] splitMessage = message.Split(' ');

            int actionCount = 0;
            Action<string, List<IBlock>?> respondAction = (m, b) =>
            {
                if (actionCount == 0)
                {
                    Assert.Equal("1: Automatically accept charges from test_user_1 where amount < $4.20", m);
                }
                else if (actionCount == 1)
                {
                    Assert.Equal("2: Automatically accept charges from test_user_2 where note is test note", m);
                }
                else
                {
                    Assert.Fail("Expected only 2 autopayments.");
                }
                actionCount += 1;
            };

            await autopay.Parse(splitMessage, venmoUser, respondAction);
        }

        [Fact]
        public async Task Parse_List_No_Autopayments()
        {
            string message = "/venmo autopay list";
            VenmoUser venmoUser = new VenmoUser(string.Empty);

            string[] splitMessage = message.Split(' ');
            Action<string, List<IBlock>?> respondAction = (m, b) =>
            {
                Assert.Equal("You have no autopayments defined.", m);
            };

            await autopay.Parse(splitMessage, venmoUser, respondAction);
            venmoUser.Autopay = new List<VenmoAutopay>();
            await autopay.Parse(splitMessage, venmoUser, respondAction);
        }

        [Fact]
        public async Task Parse_Delete_With_Amount_And_Note()
        {
            string message = "/venmo autopay delete 1";
            VenmoUser venmoUser = new VenmoUser(string.Empty)
            {
                Autopay = new List<VenmoAutopay>()
                {
                    new VenmoAutopay("test_user", "1")
                    {
                        Comparison = "=",
                        Amount = 4.2,
                        Note = "test note"
                    }
                }
            };

            string[] splitMessage = message.Split(' ');
            Action<string, List<IBlock>?> respondAction = (m, b) =>
            {
                Assert.Equal("Removed autopayment where user is test_user and amount = $4.20 and note is test note", m);
            };

            await autopay.Parse(splitMessage, venmoUser, respondAction);
        }

        [Fact]
        public async Task Parse_Delete_No_Autopayments()
        {
            string message = "/venmo autopay delete";
            VenmoUser venmoUser = new VenmoUser(string.Empty);

            string[] splitMessage = message.Split(' ');
            Action<string, List<IBlock>?> respondAction = (m, b) =>
            {
                Assert.Equal("You have no autopayments defined.", m);
            };

            await autopay.Parse(splitMessage, venmoUser, respondAction);
            venmoUser.Autopay = new List<VenmoAutopay>();
            await autopay.Parse(splitMessage, venmoUser, respondAction);
        }

        [Fact]
        public async Task Parse_Delete_Incorrect_Length()
        {
            string message = "/venmo autopay delete";
            VenmoUser venmoUser = new VenmoUser(string.Empty)
            {
                Autopay = new List<VenmoAutopay>()
                {
                    new VenmoAutopay("test_user", "1")
                }
            };

            string[] splitMessage = message.Split(' ');
            Action<string, List<IBlock>?> respondAction = (m, b) =>
            {
                Assert.Equal("Incorrect autopayment delete message. Expected /venmo autopay delete ###", m);
            };

            await autopay.Parse(splitMessage, venmoUser, respondAction);
        }

        [Fact]
        public async Task Parse_Delete_Invalid_Number()
        {
            string message = "/venmo autopay delete 0";
            VenmoUser venmoUser = new VenmoUser(string.Empty)
            {
                Autopay = new List<VenmoAutopay>()
                {
                    new VenmoAutopay("test_user", "1")
                }
            };
            string[] splitMessage = message.Split(' ');
            Action<string, List<IBlock>?> respondAction = (m, b) =>
            {
                Assert.Equal("Not a valid autopayment number, you only have 1 item.", m);
            };

            await autopay.Parse(splitMessage, venmoUser, respondAction);
        }

        [Fact]
        public async Task Parse_Unknown()
        {
            string message = "/venmo autopay other";
            string[] splitMessage = message.Split(' ');
            Action<string, List<IBlock>?> respondAction = (m, b) =>
            {
                Assert.Equal("Unknown autopay string. Please specify add, list, or delete.", m);
            };

            await autopay.Parse(splitMessage, new VenmoUser(string.Empty), respondAction);
        }
    }
}
