using Microsoft.Extensions.Diagnostics.HealthChecks;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SynoAI.Services
{
    public sealed class PipelineHealthCheck : IHealthCheck
    {
        private readonly PipelineDiagnostics _diagnostics;
        public PipelineHealthCheck(PipelineDiagnostics diagnostics) => _diagnostics = diagnostics;

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Check the actual capture mount without creating a notification or keeping a file.
                Directory.CreateDirectory(Constants.DIRECTORY_CAPTURES);
                string path = Path.Combine(Constants.DIRECTORY_CAPTURES, $".health-{Guid.NewGuid():N}");
                using FileStream probe = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    1, FileOptions.DeleteOnClose);
                probe.WriteByte(0);
                probe.Flush();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy("Capture storage is not writable."));
            }

            return Task.FromResult(_diagnostics.GetSnapshot().IsUnhealthy
                ? HealthCheckResult.Unhealthy("A pipeline stage failed or is stalled. See the authorized diagnostics endpoint.")
                : HealthCheckResult.Healthy("Capture storage is writable; no observed pipeline failure."));
        }
    }
}
