using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace SynoAI.Services
{
    public sealed class PipelineDiagnostics
    {
        private readonly object _sync = new();
        private readonly TimeProvider _clock;
        private readonly Dictionary<(string Stage, string Target), StageStatus> _stages = new();
        private readonly Dictionary<long, RunningOperation> _running = new();
        private long _nextId;

        public PipelineDiagnostics(TimeProvider clock = null) => _clock = clock ?? TimeProvider.System;

        public Operation Begin(string stage, string target, CancellationToken cancellationToken, TimeSpan? stallAfter = null)
        {
            lock (_sync)
            {
                long id = ++_nextId;
                _running[id] = new RunningOperation(stage, target, _clock.GetUtcNow(), stallAfter);
                return new Operation(this, id, cancellationToken);
            }
        }

        public void Forget(string stage, string target)
        {
            lock (_sync) _stages.Remove((stage, target));
        }

        public PipelineStatus GetSnapshot()
        {
            lock (_sync)
            {
                DateTimeOffset now = _clock.GetUtcNow();
                return new PipelineStatus(
                    _stages.Values.OrderBy(x => x.Stage).ThenBy(x => x.Target).ToArray(),
                    _running.Values.Select(x => new ActiveOperation(x.Stage, x.Target, x.StartedAt,
                        x.StallAfter.HasValue && now - x.StartedAt > x.StallAfter.Value)).ToArray());
            }
        }

        private void Finish(long id, bool? success)
        {
            lock (_sync)
            {
                if (!_running.Remove(id, out RunningOperation operation) || !success.HasValue) return;
                var key = (operation.Stage, operation.Target);
                _stages.TryGetValue(key, out StageStatus previous);
                DateTimeOffset now = _clock.GetUtcNow();
                _stages[key] = new StageStatus(operation.Stage, operation.Target,
                    success.Value ? now : previous?.LastSuccessAt,
                    success.Value ? previous?.LastFailureAt : now, !success.Value);
            }
        }

        public sealed class Operation : IDisposable
        {
            private PipelineDiagnostics _owner;
            private readonly long _id;
            private readonly CancellationToken _cancellationToken;
            internal Operation(PipelineDiagnostics owner, long id, CancellationToken cancellationToken)
            {
                _owner = owner;
                _id = id;
                _cancellationToken = cancellationToken;
            }
            public void Complete(bool success) => Interlocked.Exchange(ref _owner, null)?.Finish(_id, _cancellationToken.IsCancellationRequested ? null : success);
            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Finish(_id,
                _cancellationToken.IsCancellationRequested ? null : false);
        }

        private sealed record RunningOperation(string Stage, string Target, DateTimeOffset StartedAt, TimeSpan? StallAfter);
        public sealed record StageStatus(string Stage, string Target, DateTimeOffset? LastSuccessAt, DateTimeOffset? LastFailureAt, bool IsFailing);
        public sealed record ActiveOperation(string Stage, string Target, DateTimeOffset StartedAt, bool IsStalled);
        public sealed record PipelineStatus(IReadOnlyList<StageStatus> Stages, IReadOnlyList<ActiveOperation> ActiveOperations)
        {
            public bool IsUnhealthy => Stages.Any(x => x.IsFailing) || ActiveOperations.Any(x => x.IsStalled);
        }
    }
}
