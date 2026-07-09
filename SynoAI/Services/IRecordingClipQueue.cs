using System.Threading;
using System.Threading.Tasks;

namespace SynoAI.Services
{
    public interface IRecordingClipQueue
    {
        bool TryEnqueue(RecordingClipWorkItem workItem);
        ValueTask<RecordingClipWorkItem> ReadAsync(CancellationToken cancellationToken);
    }
}
