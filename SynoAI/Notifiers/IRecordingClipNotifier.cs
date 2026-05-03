using Microsoft.Extensions.Logging;
using SynoAI.Models;
using System.Threading.Tasks;

namespace SynoAI.Notifiers
{
    public interface IRecordingClipNotifier
    {
        bool SendRecordingClip { get; }
        int RecordingClipDownloadDelayMs { get; }
        /// <summary>
        /// Millisecond adjustment relative to the detected snapshot when recording timestamps are available.
        /// </summary>
        int RecordingClipOffsetMs { get; }
        int RecordingClipDurationMs { get; }
        Task SendRecordingClipAsync(Camera camera, Notification notification, ILogger logger);
    }
}
