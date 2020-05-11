using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using NodaTime;
using VenmoForSlack.Venmo;

namespace VenmoForSlack
{
    public class Program
    {
        public static MongoClient Mongo = new MongoClient(Secrets.MongoConnectionString);
        
        public static void Main(string[] args)
        {
            var host = CreateHostBuilder(args).Build();
            ScheduleProcessor scheduleProcessor = new ScheduleProcessor(
                host.Services.GetRequiredService<ILogger<ScheduleProcessor>>(),
                host.Services.GetRequiredService<ILogger<VenmoApi>>(),
                host.Services.GetRequiredService<HttpClient>(),
                host.Services.GetRequiredService<IClock>());
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>()
                    .UseUrls("http://127.0.0.1:8900");
                });
    }
}
