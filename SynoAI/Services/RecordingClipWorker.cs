using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
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

        public RecordingClipWorker(
            IRecordingClipQueue queue,
            IServiceScopeFactory scopeFactory,
            ILogger<RecordingClipWorker> logger)
        {
            _queue = queue;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Recording Clip Worker is starting.");
            HashSet<Task> activeTasks = new();

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    RecordingClipWorkItem workItem = await _queue.ReadAsync(stoppingToken);
                    activeTasks.RemoveWhere(x => x.IsCompleted);
                    activeTasks.Add(ProcessWorkItemAsync(workItem, stoppingToken));
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            finally
            {
                await Task.WhenAll(activeTasks.ToArray());
            }
        }

        private async Task ProcessWorkItemAsync(
            RecordingClipWorkItem workItem,
            CancellationToken cancellationToken)
        {
            try
            {
                using IServiceScope scope = _scopeFactory.CreateScope();
                IRecordingClipProcessor processor = scope.ServiceProvider.GetRequiredService<IRecordingClipProcessor>();
                await processor.ProcessAsync(workItem, cancellationToken);
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
