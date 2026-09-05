using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using SynoAI.Models;
using SynoAI.Notifiers;
using SynoAI.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SynoAI.Tests
{
    public class RecordingClipWorkerTests
    {
        [Test]
        public async Task Worker_BoundsOutstandingWorkAndStopsOnCancellation()
        {
            Config.Generate(NullLogger.Instance, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
            RecordingClipQueue queue = new();
            BlockedProcessor processor = new();
            using ServiceProvider services = new ServiceCollection()
                .AddSingleton<IRecordingClipProcessor>(processor).BuildServiceProvider();
            using RecordingClipWorker worker = new(queue,
                services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<RecordingClipWorker>.Instance);
            RecordingClipWorkItem item = new(new Camera { Name = "Test" },
                DateTimeOffset.UtcNow, Array.Empty<IRecordingClipNotifier>());
            for (int i = 0; i < 32; i++) Assert.That(queue.TryEnqueue(item), Is.True);
            await worker.StartAsync(CancellationToken.None);
            int started;
            try
            {
                await processor.FirstBatch.Task.WaitAsync(TimeSpan.FromSeconds(3));
                for (int i = 0; i < 32; i++) queue.TryEnqueue(item);
                Assert.That(queue.PendingCount, Is.EqualTo(32));
                Assert.That(queue.TryEnqueue(item), Is.False);
                await Task.WhenAny(processor.SecondBatch.Task, Task.Delay(500));
                started = processor.Started;
                processor.Release.Release();
                await processor.SecondBatch.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None);
            }
            Assert.That(started, Is.EqualTo(2),
                "Only the configured two clip transfers may run at the same time.");
        }

        private sealed class BlockedProcessor : IRecordingClipProcessor
        {
            public int Started;
            public SemaphoreSlim Release { get; } = new(0);
            public TaskCompletionSource FirstBatch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource SecondBatch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public async Task ProcessAsync(RecordingClipWorkItem item, CancellationToken cancellationToken)
            {
                int count = Interlocked.Increment(ref Started);
                if (count == 2) FirstBatch.TrySetResult();
                if (count == 3) SecondBatch.TrySetResult();
                await Release.WaitAsync(cancellationToken);
            }
        }
    }
}
