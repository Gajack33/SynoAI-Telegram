using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;
using SynoAI.App;
using SynoAI.Services;
using System.Net.Http;

namespace SynoAI
{
    public class Startup
    {
        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddLogging(logging =>
            {
                logging.AddFilter("System.Net.Http.HttpClient.synoai-outbound", LogLevel.Warning);
                logging.AddFilter("System.Net.Http.HttpClient.synoai-outbound.LogicalHandler", LogLevel.Warning);
                logging.AddFilter("System.Net.Http.HttpClient.synoai-outbound.ClientHandler", LogLevel.Warning);
            });
            services.AddHttpClient(HttpClientWrapper.OutboundClientName);
            services.AddSingleton<IHttpClient, HttpClientWrapper>();
            services.AddSingleton<SynologyCookieStore>();
            services.AddHttpClient(SynologyService.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(serviceProvider =>
                {
                    SynologyCookieStore cookieStore = serviceProvider.GetRequiredService<SynologyCookieStore>();
                    HttpClientHandler handler = new()
                    {
                        CookieContainer = cookieStore.CookieContainer
                    };

                    if (Config.AllowInsecureUrl)
                    {
                        handler.ServerCertificateCustomValidationCallback = (message, cert, chain, sslPolicyErrors) => true;
                    }

                    return handler;
                });

            services.AddSingleton<ICameraProcessingQueue, CameraProcessingQueue>();
            services.AddSingleton<IDetectionMemory, DetectionMemory>();
            services.AddScoped<IAIService, AIService>();
            services.AddSingleton<ISynologyService, SynologyService>();
            services.AddScoped<ICameraTriggerProcessor, CameraTriggerProcessor>();

            services.AddHostedService<CaptureCleanupService>();
            services.AddHostedService<AppLifecycleService>();
            services.AddHostedService<CameraProcessingWorker>();

            services.AddControllers();
            services.AddHealthChecks()
                .AddCheck<AIHealthCheck>("codeproject-ai");
            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo { Title = "SynoAI", Version = "v1" });
            });

        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, IConfiguration configuration, ILogger<Startup> logger)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
                app.UseSwagger();
                app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "SynoAI v1"));
            }
            app.UseRouting();

            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
                endpoints.MapHealthChecks("/health");
            });
        }
    }
}
