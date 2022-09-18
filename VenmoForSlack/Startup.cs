using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using VenmoForSlack.Providers;
using VenmoForSlack.Venmo;

namespace VenmoForSlack
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddControllers()
                .AddNewtonsoftJson();
            services.AddMemoryCache();

            services.AddSingleton<Settings>();
            services.AddSingleton<HttpClient>();
            services.AddScoped(container =>
            {
                var logger = container.GetRequiredService<ILogger<VenmoApi>>();
                return new VenmoApi(logger);
            });
            services.AddScoped(container =>
            {
                var logger = container.GetRequiredService<ILogger<HelperMethods>>();
                return new HelperMethods(logger);
            });
            services.AddSingleton<IClock>(SystemClock.Instance);
            // For Slack API rate limiting
            services.AddSingleton<Dictionary<string, SemaphoreSlim>>();
            services.AddSingleton<ICacheItemLifetimeProvider>(c => new CacheItemLifetimeProvider());
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseStaticFiles();

            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
            });
        }
    }
}
