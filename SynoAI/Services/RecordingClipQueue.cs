using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SynoAI.Services
{
    public sealed class RecordingClipQueue : IRecordingClipQueue
    {
        private const int Capacity = 32;
        private readonly object _sync = new();
        private readonly PriorityQueue<RecordingClipWorkItem, DateTimeOffset> _pending = new();
        private TaskCompletionSource<bool> _changed = NewSignal();
        private readonly TimeProvider _clock;

        public RecordingClipQueue(TimeProvider clock = null) => _clock = clock ?? TimeProvider.System;

        public int PendingCount { get { lock (_sync) return _pending.Count; } }

        public bool TryEnqueue(RecordingClipWorkItem workItem)
        {
            if (workItem == null) return false;
            lock (_sync)
            {
                if (_pending.Count >= Capacity) return false;
                int delayMs = workItem.Notifiers.FirstOrDefault(x => x.SendRecordingClip)?.RecordingClipDownloadDelayMs ?? 0;
                _pending.Enqueue(workItem, _clock.GetUtcNow().AddMilliseconds(Math.Max(0, delayMs)));
                TaskCompletionSource<bool> previous = _changed;
                _changed = NewSignal();
                previous.TrySetResult(true);
                return true;
            }
        }

        public async ValueTask<RecordingClipWorkItem> ReadAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Task changed;
                TimeSpan delay;
                lock (_sync)
                {
                    delay = Timeout.InfiniteTimeSpan;
                    if (_pending.TryPeek(out RecordingClipWorkItem item, out DateTimeOffset readyAt))
                    {
                        delay = readyAt - _clock.GetUtcNow();
                        if (delay <= TimeSpan.Zero) return _pending.Dequeue();
                    }
                    changed = _changed.Task;
                }

                // Waiting for recordings to grow does not consume a transfer slot.
                // New, earlier work wakes all readers so it can overtake delayed clips.
                using CancellationTokenSource timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                try
                {
                    await Task.WhenAny(changed, Task.Delay(delay, _clock, timer.Token));
                    cancellationToken.ThrowIfCancellationRequested();
                }
                finally
                {
                    timer.Cancel();
                }
            }
        }

        private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
