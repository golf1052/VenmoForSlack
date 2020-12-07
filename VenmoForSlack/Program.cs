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
using Microsoft.Extensions.Logging.Console;
using MongoDB.Driver;
using NodaTime;
using VenmoForSlack.Database;
using VenmoForSlack.Venmo;

namespace VenmoForSlack
{
    public class Program
    {
        public static MongoClient Mongo = new MongoClient(Secrets.MongoConnectionString);
        
        public static void Main(string[] args)
        {
            var host = CreateHostBuilder(args).Build();
            using (var serviceScope = host.Services.CreateScope())
            {
                var services = serviceScope.ServiceProvider;
                ScheduleProcessor scheduleProcessor = new ScheduleProcessor(
                services.GetRequiredService<ILogger<ScheduleProcessor>>(),
                services.GetRequiredService<ILogger<VenmoApi>>(),
                services.GetRequiredService<ILogger<MongoDatabase>>(),
                services.GetRequiredService<HttpClient>(),
                services.GetRequiredService<IClock>(),
                services.GetRequiredService<HelperMethods>());
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
