using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using VenmoForSlack.Controllers;
using VenmoForSlack.Database;
using VenmoForSlack.Venmo;
using Xunit;

namespace VenmoForSlack.Tests
{
    public class VenmoControllerTests
    {
        private VenmoController venmoController;
        private HttpClient httpClient;
        private VenmoApi venmoApi;
        private FakeClock fakeClock;
        private HelperMethods helperMethods;

        public VenmoControllerTests()
        {
            httpClient = new HttpClient();
            venmoApi = new VenmoApi(NullLogger<VenmoApi>.Instance);
            fakeClock = new FakeClock(SystemClock.Instance.GetCurrentInstant());
            helperMethods = new HelperMethods(NullLogger<HelperMethods>.Instance);
            venmoController = new VenmoController(NullLogger<VenmoController>.Instance,
                httpClient,
                venmoApi,
                NullLogger<YNABHandler>.Instance,
                fakeClock,
                helperMethods,
                NullLogger<MongoDatabase>.Instance);    
        }

        [Fact]
        public void ConvertScheduleMessageIntoPaymentMessage()
        {
            string message1 = "/venmo schedule every Wednesday charge 1 for lunch to someone1 someone2";
            string[] newSplitMessage1 = helperMethods.ConvertScheduleMessageIntoPaymentMessage(message1.Split(' '));
            string[] expectedSplitMessage = new string[] { "/venmo", "charge", "1", "for", "lunch", "to", "someone1", "someone2" };
            Assert.Equal(expectedSplitMessage, newSplitMessage1);

            string message2 = "/venmo schedule every Wednesday public charge 1 for lunch to someone1 someone2";
            string[] newSplitMessage2 = helperMethods.ConvertScheduleMessageIntoPaymentMessage(message2.Split(' '));
            string[] expectedSplitMessage2 = new string[] { "/venmo", "public", "charge", "1", "for", "lunch", "to", "someone1", "someone2" };
            Assert.Equal(expectedSplitMessage2, newSplitMessage2);
        }
        
        [Fact]
        public void ConvertDateStringIntoDateTime()
        {
            string timeZone = "America/Los_Angeles";

            fakeClock.Reset(GetLocalInstant(new LocalDateTime(2020, 1, 15, 11, 30), timeZone));

            ZonedDateTime nextDayDateTime = helperMethods.ConvertDateStringIntoDateTime("day", timeZone, fakeClock);
            Assert.Equal(CreateZonedDateTime(new LocalDateTime(2020, 1, 16, 12, 0), timeZone),
                nextDayDateTime);

            ZonedDateTime sundayDateTime = helperMethods.ConvertDateStringIntoDateTime("sunday", timeZone, fakeClock);
            Assert.Equal(CreateZonedDateTime(new LocalDateTime(2020, 1, 19, 12, 0), timeZone),
                sundayDateTime);
            
            ZonedDateTime mondayDateTime = helperMethods.ConvertDateStringIntoDateTime("monday", timeZone, fakeClock);
            Assert.Equal(CreateZonedDateTime(new LocalDateTime(2020, 1, 20, 12, 0), timeZone),
                mondayDateTime);

            ZonedDateTime tuesdayDateTime = helperMethods.ConvertDateStringIntoDateTime("tuesday", timeZone, fakeClock);
            Assert.Equal(CreateZonedDateTime(new LocalDateTime(2020, 1, 21, 12, 0), timeZone),
                tuesdayDateTime);

            ZonedDateTime wednesdayDateTime = helperMethods.ConvertDateStringIntoDateTime("wednesday", timeZone, fakeClock);
            Assert.Equal(CreateZonedDateTime(new LocalDateTime(2020, 1, 22, 12, 0), timeZone),
                wednesdayDateTime);

            ZonedDateTime thursdayDateTime = helperMethods.ConvertDateStringIntoDateTime("thursday", timeZone, fakeClock);
            Assert.Equal(CreateZonedDateTime(new LocalDateTime(2020, 1, 16, 12, 0), timeZone),
                thursdayDateTime);

            ZonedDateTime fridayDateTime = helperMethods.ConvertDateStringIntoDateTime("friday", timeZone, fakeClock);
            Assert.Equal(CreateZonedDateTime(new LocalDateTime(2020, 1, 17, 12, 0), timeZone),
                fridayDateTime);

            ZonedDateTime saturdayDateTime = helperMethods.ConvertDateStringIntoDateTime("saturday", timeZone, fakeClock);
            Assert.Equal(CreateZonedDateTime(new LocalDateTime(2020, 1, 18, 12, 0), timeZone),
                saturdayDateTime);

            ZonedDateTime beginningOfTheMonthDateTime = helperMethods.ConvertDateStringIntoDateTime("beginning of the month", timeZone, fakeClock);
            Assert.Equal(CreateZonedDateTime(new LocalDateTime(2020, 2, 1, 12, 0), timeZone),
                beginningOfTheMonthDateTime);

            ZonedDateTime endOfTheMonthDateTime = helperMethods.ConvertDateStringIntoDateTime("end of the month", timeZone, fakeClock);
            Assert.Equal(CreateZonedDateTime(new LocalDateTime(2020, 1, 31, 12, 0), timeZone),
                endOfTheMonthDateTime);

            ZonedDateTime dateAfterNowDateTime = helperMethods.ConvertDateStringIntoDateTime("18", timeZone, fakeClock);
            Assert.Equal(CreateZonedDateTime(new LocalDateTime(2020, 1, 18, 12, 0), timeZone),
                dateAfterNowDateTime);

            ZonedDateTime dateBeforeNowDateTime = helperMethods.ConvertDateStringIntoDateTime("14", timeZone, fakeClock);
            Assert.Equal(CreateZonedDateTime(new LocalDateTime(2020, 2, 14, 12, 0), timeZone),
                dateBeforeNowDateTime);

            ZonedDateTime parsedLocalDateDateTime = helperMethods.ConvertDateStringIntoDateTime("2020-01-20", timeZone, fakeClock);
            Assert.Equal(CreateZonedDateTime(new LocalDateTime(2020, 1, 20, 12, 0), timeZone),
                parsedLocalDateDateTime);

            ZonedDateTime parsedLocalDateTimeDateTime = helperMethods.ConvertDateStringIntoDateTime("2020-01-20T10:30", timeZone, fakeClock);
            Assert.Equal(CreateZonedDateTime(new LocalDateTime(2020, 1, 20, 10, 30), timeZone),
                parsedLocalDateTimeDateTime);
            
            ZonedDateTime parsedLocalDateTimeWithSecondsDateTime = helperMethods.ConvertDateStringIntoDateTime("2020-01-20T10:30:01", timeZone, fakeClock);
            Assert.Equal(CreateZonedDateTime(new LocalDateTime(2020, 1, 20, 10, 30, 1), timeZone),
                parsedLocalDateTimeWithSecondsDateTime);

            // "edge cases" aka February
            // on Feb 29th, requesting 30th of every month will get set to March 30th because we don't schedule
            // on the day we're on
            fakeClock.Reset(GetLocalInstant(new LocalDateTime(2020, 2, 29, 11, 30), timeZone));
            ZonedDateTime march30 = helperMethods.ConvertDateStringIntoDateTime("30", timeZone, fakeClock);
            Assert.Equal(CreateZonedDateTime(new LocalDateTime(2020, 3, 30, 12, 0), timeZone),
                march30);

            // on Feb 15th, requesting 30th of every month will get set to Feb 29th because Feb only has 29 days
            fakeClock.Reset(GetLocalInstant(new LocalDateTime(2020, 2, 15, 11, 30), timeZone));
            ZonedDateTime feb29 = helperMethods.ConvertDateStringIntoDateTime("30", timeZone, fakeClock);
            Assert.Equal(CreateZonedDateTime(new LocalDateTime(2020, 2, 29, 12, 0), timeZone),
                feb29);

            // on March 31st, requesting 31st of every month will get set to April 30th because we don't schedule
            // on the day we're on and April only has 30 days
            fakeClock.Reset(GetLocalInstant(new LocalDateTime(2020, 3, 31, 11, 30), timeZone));
            ZonedDateTime april30 = helperMethods.ConvertDateStringIntoDateTime("31", timeZone, fakeClock);
            Assert.Equal(CreateZonedDateTime(new LocalDateTime(2020, 4, 30, 12, 0), timeZone),
                april30);

            // on Jan 31st, requesting the 31st of every month will get set to Feb 29th because we don't schedule
            // on the day we're on and Feb only has 29 days (in a leap year)
            fakeClock.Reset(GetLocalInstant(new LocalDateTime(2020, 1, 31, 11, 30), timeZone));
            ZonedDateTime feb292 = helperMethods.ConvertDateStringIntoDateTime("31", timeZone, fakeClock);
            Assert.Equal(CreateZonedDateTime(new LocalDateTime(2020, 2, 29, 12, 0), timeZone),
                feb292);
        }

        private ZonedDateTime CreateZonedDateTime(LocalDateTime localDateTime, string timeZone)
        {
            return localDateTime.InZoneLeniently(DateTimeZoneProviders.Tzdb[timeZone]);
        }

        private Instant GetLocalInstant(LocalDateTime localDateTime, string timeZone)
        {
            return CreateZonedDateTime(localDateTime, timeZone).ToInstant();
        }
    }
}
