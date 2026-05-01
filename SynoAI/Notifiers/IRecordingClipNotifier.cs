using Microsoft.Extensions.Logging;
using SynoAI.Models;
using System.Threading.Tasks;

namespace SynoAI.Notifiers
{
    public interface IRecordingClipNotifier
    {
        bool SendRecordingClip { get; }
        int RecordingClipDownloadDelayMs { get; }
        int RecordingClipOffsetMs { get; }
        int RecordingClipDurationMs { get; }
        Task SendRecordingClipAsync(Camera camera, Notification notification, ILogger logger);
    }
}
