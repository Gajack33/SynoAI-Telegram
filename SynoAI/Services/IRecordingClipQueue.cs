using System.Threading;
using System.Threading.Tasks;

namespace SynoAI.Services
{
    public interface IRecordingClipQueue
    {
        int PendingCount { get; }
        bool TryEnqueue(RecordingClipWorkItem workItem);
        ValueTask<RecordingClipWorkItem> ReadAsync(CancellationToken cancellationToken);
    }
}
