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

        public RecordingClipProcessor(
            ISynologyService synologyService,
            ILogger<RecordingClipProcessor> logger)
        {
            _synologyService = synologyService;
            _logger = logger;
        }

        public async Task ProcessAsync(RecordingClipWorkItem workItem, CancellationToken cancellationToken)
        {
            IRecordingClipNotifier recordingClipNotifier = workItem.Notifiers.FirstOrDefault(x => x.SendRecordingClip);
            if (recordingClipNotifier == null)
            {
                return;
            }

            int downloadDelayMs = Math.Max(0, recordingClipNotifier.RecordingClipDownloadDelayMs);
            if (downloadDelayMs > 0)
            {
                _logger.LogInformation(
                    "{cameraName}: Waiting {delayMs}ms before downloading the recording clip in the background.",
                    workItem.Camera.Name,
                    downloadDelayMs);
                await Task.Delay(downloadDelayMs, cancellationToken);
            }

            ProcessedFile recordingClip = await _synologyService.DownloadLatestRecordingClipAsync(
                workItem.Camera.Name,
                workItem.DetectedAt,
                recordingClipNotifier.RecordingClipOffsetMs,
                recordingClipNotifier.RecordingClipDurationMs);
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
                .Select(x => x.SendRecordingClipAsync(workItem.Camera, notification, _logger)));
        }
    }
}
