using Microsoft.Extensions.Diagnostics.HealthChecks;
using SynoAI.AIs;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SynoAI.Services
{
    public class AIHealthCheck : IHealthCheck
    {
        private static readonly HttpClient HttpClient = new();

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            if (Config.AI != AIType.CodeProjectAIServer)
            {
                return HealthCheckResult.Healthy("AI health check is only enabled for CodeProject.AI.");
            }

            if (string.IsNullOrWhiteSpace(Config.AIUrl))
            {
                return HealthCheckResult.Unhealthy("CodeProject.AI URL is not configured.");
            }

            try
            {
                Uri uri = new(new Uri(Config.AIUrl), "v1/status/ping");
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(Config.AITimeoutSeconds, 5)));

                using HttpResponseMessage response = await HttpClient.GetAsync(uri, timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    return HealthCheckResult.Healthy("CodeProject.AI responded to ping.");
                }

                return HealthCheckResult.Unhealthy($"CodeProject.AI ping returned HTTP {(int)response.StatusCode}.");
            }
            catch (TaskCanceledException ex)
            {
                return HealthCheckResult.Unhealthy("CodeProject.AI ping timed out.", ex);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is UriFormatException || ex is ArgumentException)
            {
                return HealthCheckResult.Unhealthy("CodeProject.AI ping failed.", ex);
            }
        }
    }
}
