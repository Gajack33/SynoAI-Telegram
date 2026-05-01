using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SynoAI.App;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace SynoAI.Services
{
    public class AppLifecycleService : IHostedService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<AppLifecycleService> _logger;
        private readonly IConfiguration _configuration;

        public AppLifecycleService(IServiceScopeFactory scopeFactory, ILogger<AppLifecycleService> logger, IConfiguration configuration)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _configuration = configuration;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString()
                ?? "unknown";
            _logger.LogInformation("Starting SynoAI version {version}.", version);

            Config.Generate(_logger, _configuration);
            IReadOnlyList<string> configurationErrors = Config.ValidateStartupConfiguration();
            if (configurationErrors.Count > 0)
            {
                foreach (string error in configurationErrors)
                {
                    _logger.LogError("Configuration error: {error}", error);
                }

                _logger.LogError("SynoAI configuration is invalid. Stopping application.");
                using (var scope = _scopeFactory.CreateScope())
                {
                    var lifetime = scope.ServiceProvider.GetRequiredService<IHostApplicationLifetime>();
                    lifetime.StopApplication();
                }
                return;
            }

            int sharedTimeoutSeconds = Math.Max(Config.HttpTimeoutSeconds, Math.Max(Config.AITimeoutSeconds, Config.TelegramTimeoutSeconds));
            Shared.HttpClient.Timeout = TimeSpan.FromSeconds(sharedTimeoutSeconds);

            using (var scope = _scopeFactory.CreateScope())
            {
                var synologyService = scope.ServiceProvider.GetRequiredService<ISynologyService>();
                var aiService = scope.ServiceProvider.GetRequiredService<IAIService>();

                List<Task> initializationTasks = new List<Task>();
                if (string.IsNullOrWhiteSpace(Config.AccessToken))
                {
                    _logger.LogWarning("AccessToken is not configured. SynoAI endpoints will accept unauthenticated LAN requests.");
                }
                initializationTasks.Add(aiService.WarmupAsync());
                initializationTasks.Add(synologyService.InitialiseAsync());

                await Task.WhenAll(initializationTasks);
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
