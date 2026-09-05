using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SynoAI.Services
{
    public class CameraProcessingWorker : BackgroundService
    {
        private readonly ICameraProcessingQueue _queue;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<CameraProcessingWorker> _logger;
        private readonly PipelineDiagnostics _diagnostics;

        public CameraProcessingWorker(
            ICameraProcessingQueue queue,
            IServiceScopeFactory scopeFactory,
            ILogger<CameraProcessingWorker> logger,
            PipelineDiagnostics diagnostics = null)
        {
            _queue = queue;
            _scopeFactory = scopeFactory;
            _logger = logger;
            _diagnostics = diagnostics;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Camera Processing Worker is starting.");

            // The queue reserves at most one job per configured camera until Complete is called.
            // Image/AI stages share CameraAnalysisGate; notification waits can overlap.
            HashSet<Task> activeTasks = new();
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    CameraTriggerWorkItem workItem = await _queue.ReadAsync(stoppingToken);
                    activeTasks.RemoveWhere(task => task.IsCompleted);
                    activeTasks.Add(ProcessWorkItemAsync(workItem, stoppingToken));
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            finally
            {
                await Task.WhenAll(activeTasks);
            }
        }

        private async Task ProcessWorkItemAsync(CameraTriggerWorkItem workItem, CancellationToken stoppingToken)
        {
            try
            {
                using var operation = _diagnostics?.Begin("camera", workItem.CameraName, stoppingToken);
                using IServiceScope scope = _scopeFactory.CreateScope();
                ICameraTriggerProcessor processor = scope.ServiceProvider.GetRequiredService<ICameraTriggerProcessor>();
                CameraProcessingStatus status = await processor.ProcessAsync(workItem.CameraName, stoppingToken);
                operation?.Complete(status == CameraProcessingStatus.ValidObjectDetected || status == CameraProcessingStatus.NoValidObjectDetected);
                _logger.LogInformation(
                    "{cameraName}: Queued camera trigger finished with status {status}. TotalTime={totalMs}ms.",
                    workItem.CameraName, status, (DateTime.UtcNow - workItem.QueuedAtUtc).TotalMilliseconds);
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
