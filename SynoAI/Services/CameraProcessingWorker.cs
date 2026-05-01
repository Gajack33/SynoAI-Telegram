using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SynoAI.Services
{
    public class CameraProcessingWorker : BackgroundService
    {
        private readonly ICameraProcessingQueue _queue;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<CameraProcessingWorker> _logger;

        public CameraProcessingWorker(
            ICameraProcessingQueue queue,
            IServiceScopeFactory scopeFactory,
            ILogger<CameraProcessingWorker> logger)
        {
            _queue = queue;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Camera Processing Worker is starting.");

            while (!stoppingToken.IsCancellationRequested)
            {
                CameraTriggerWorkItem workItem;
                try
                {
                    workItem = await _queue.ReadAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    using IServiceScope scope = _scopeFactory.CreateScope();
                    ICameraTriggerProcessor processor = scope.ServiceProvider.GetRequiredService<ICameraTriggerProcessor>();
                    CameraProcessingStatus status = await processor.ProcessAsync(workItem.CameraName, stoppingToken);
                    _logger.LogInformation(
                        "{cameraName}: Queued camera trigger finished with status {status}. QueuedFor={queuedForMs}ms.",
                        workItem.CameraName,
                        status,
                        (DateTime.UtcNow - workItem.QueuedAtUtc).TotalMilliseconds);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("{cameraName}: Queued camera trigger cancelled during shutdown.", workItem.CameraName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "{cameraName}: Queued camera trigger failed unexpectedly.", workItem.CameraName);
                }
                finally
                {
                    _queue.Complete(workItem.CameraName);
                }
            }
        }
    }
}
