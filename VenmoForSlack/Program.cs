using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using NodaTime;
using VenmoForSlack.Database;
using VenmoForSlack.Venmo;

namespace VenmoForSlack
{
    public class Program
    {
        public static readonly MongoClient Mongo = new MongoClient(Secrets.MongoConnectionString);
        
        public static void Main(string[] args)
        {
            var host = CreateHostBuilder(args).Build();
            using (var serviceScope = host.Services.CreateScope())
            {
                var services = serviceScope.ServiceProvider;
                ScheduleProcessor scheduleProcessor = new ScheduleProcessor(
                    services.GetRequiredService<Settings>(),
                    Duration.FromMinutes(15),
                    services.GetRequiredService<ILogger<ScheduleProcessor>>(),
                    services.GetRequiredService<ILogger<VenmoApi>>(),
                    services.GetRequiredService<ILogger<MongoDatabase>>(),
                    services.GetRequiredService<HttpClient>(),
                    services.GetRequiredService<IClock>(),
                    services.GetRequiredService<HelperMethods>(),
                    services.GetRequiredService<IMemoryCache>(),
                    TimeSpan.FromDays(1),
                    services.GetRequiredService<Dictionary<string, SemaphoreSlim>>());

                YNABProcessor ynabProcessor = new YNABProcessor(
                    services.GetRequiredService<Settings>(),
                    Duration.FromHours(1),
                    services.GetRequiredService<ILogger<YNABProcessor>>(),
                    services.GetRequiredService<ILogger<VenmoApi>>(),
                    services.GetRequiredService<ILogger<MongoDatabase>>(),
                    services.GetRequiredService<HttpClient>(),
                    services.GetRequiredService<HelperMethods>(),
                    services.GetRequiredService<Dictionary<string, SemaphoreSlim>>());
            }
            
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureLogging(logging =>
                {
                    logging.AddConsole()
                    .AddSimpleConsole(configure =>
                    {
                        configure.UseUtcTimestamp = true;
                        configure.TimestampFormat = "[yyyy-MM-ddTHH:mm:ss] ";
                    });
                })
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>()
                    .UseUrls("http://127.0.0.1:8900");
                });
    }
}
