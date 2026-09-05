using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SynoAI.Services
{
    public sealed class RecordingClipWorker : BackgroundService
    {
        private readonly IRecordingClipQueue _queue;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<RecordingClipWorker> _logger;
        private readonly PipelineDiagnostics _diagnostics;

        public RecordingClipWorker(
            IRecordingClipQueue queue,
            IServiceScopeFactory scopeFactory,
            ILogger<RecordingClipWorker> logger,
            PipelineDiagnostics diagnostics = null)
        {
            _queue = queue;
            _scopeFactory = scopeFactory;
            _logger = logger;
            _diagnostics = diagnostics;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Recording Clip Worker is starting.");
            await Task.WhenAll(Enumerable.Range(0, Math.Max(1, Config.MaxConcurrentRecordingClips))
                .Select(_ => ConsumeAsync(stoppingToken)));
        }

        private async Task ConsumeAsync(CancellationToken stoppingToken)
        {
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    RecordingClipWorkItem workItem = await _queue.ReadAsync(stoppingToken);
                    await ProcessWorkItemAsync(workItem, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
        }

        private async Task ProcessWorkItemAsync(
            RecordingClipWorkItem workItem,
            CancellationToken cancellationToken)
        {
            try
            {
                using var operation = _diagnostics?.Begin("video-job", workItem.Camera.Name, cancellationToken);
                using IServiceScope scope = _scopeFactory.CreateScope();
                IRecordingClipProcessor processor = scope.ServiceProvider.GetRequiredService<IRecordingClipProcessor>();
                await processor.ProcessAsync(workItem, cancellationToken);
                operation?.Complete(true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation(
                    "{cameraName}: Background recording clip processing was cancelled during shutdown.",
                    workItem.Camera.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "{cameraName}: Recording clip processing failed after the photo notification was sent.",
                    workItem.Camera.Name);
            }
        }
    }
}
