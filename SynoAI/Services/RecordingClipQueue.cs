using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SynoAI.Services
{
    public sealed class RecordingClipQueue : IRecordingClipQueue
    {
        private const int Capacity = 32;

        private readonly Channel<RecordingClipWorkItem> _queue = Channel.CreateBounded<RecordingClipWorkItem>(
            new BoundedChannelOptions(Capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });

        public bool TryEnqueue(RecordingClipWorkItem workItem)
        {
            return workItem != null && _queue.Writer.TryWrite(workItem);
        }

        public ValueTask<RecordingClipWorkItem> ReadAsync(CancellationToken cancellationToken)
        {
            return _queue.Reader.ReadAsync(cancellationToken);
        }
    }
}
