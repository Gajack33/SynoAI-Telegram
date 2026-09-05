using Microsoft.Extensions.Logging;
using SynoAI.Models;
using SynoAI.Notifiers;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SynoAI.Services
{
    public sealed class RecordingClipProcessor : IRecordingClipProcessor
    {
        private readonly ISynologyService _synologyService;
        private readonly ILogger<RecordingClipProcessor> _logger;
        private readonly PipelineDiagnostics _diagnostics;

        public RecordingClipProcessor(
            ISynologyService synologyService,
            ILogger<RecordingClipProcessor> logger,
            PipelineDiagnostics diagnostics = null)
        {
            _synologyService = synologyService;
            _logger = logger;
            _diagnostics = diagnostics;
        }

        public async Task ProcessAsync(RecordingClipWorkItem workItem, CancellationToken cancellationToken)
        {
            IRecordingClipNotifier recordingClipNotifier = workItem.Notifiers.FirstOrDefault(x => x.SendRecordingClip);
            if (recordingClipNotifier == null)
            {
                return;
            }

            using var download = _diagnostics?.Begin("video-download", workItem.Camera.Name, cancellationToken,
                TimeSpan.FromSeconds(Config.SynologyTimeoutSeconds + 5d));
            ProcessedFile recordingClip = await _synologyService.DownloadLatestRecordingClipAsync(
                workItem.Camera.Name,
                workItem.DetectedAt,
                recordingClipNotifier.RecordingClipOffsetMs,
                recordingClipNotifier.RecordingClipDurationMs, cancellationToken);
            download?.Complete(recordingClip != null);
            if (recordingClip == null)
            {
                _logger.LogWarning(
                    "{cameraName}: The photo was sent, but no recording clip was available.",
                    workItem.Camera.Name);
                return;
            }

            Notification notification = new()
            {
                CreatedAt = workItem.DetectedAt.LocalDateTime,
                RecordingClip = recordingClip
            };

            await Task.WhenAll(workItem.Notifiers
                .Where(x => x.SendRecordingClip)
                .Select(async notifier =>
                {
                    using var operation = _diagnostics?.Begin("telegram-video", $"{workItem.Camera.Name}/{Config.Notifiers.ToList().IndexOf((INotifier)notifier)}", cancellationToken);
                    await notifier.SendRecordingClipAsync(workItem.Camera, notification, _logger, cancellationToken);
                    operation?.Complete(true);
                }));
        }
    }
}
