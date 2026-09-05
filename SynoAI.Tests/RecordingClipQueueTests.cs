using NUnit.Framework;
using SynoAI.Models;
using SynoAI.Notifiers;
using SynoAI.Notifiers.Telegram;
using SynoAI.Services;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace SynoAI.Tests
{
    public class RecordingClipQueueTests
    {
        [Test]
        public async Task ReadAsync_ImmediateClipOvertakesRecordingThatIsStillGrowing()
        {
            RecordingClipQueue queue = new();
            var delayed = Clip(60000);
            var immediate = Clip(0);
            using CancellationTokenSource stop = new(TimeSpan.FromSeconds(3));
            Assert.That(queue.TryEnqueue(delayed), Is.True);
            Task<RecordingClipWorkItem> read = queue.ReadAsync(stop.Token).AsTask();
            Assert.That(read.IsCompleted, Is.False);
            Assert.That(queue.TryEnqueue(immediate), Is.True);
            Assert.That(await read, Is.SameAs(immediate));
            Assert.That(queue.PendingCount, Is.EqualTo(1));
        }

        [Test]
        public async Task ReadAsync_WaitsUntilClipIsReady()
        {
            RecordingClipQueue queue = new();
            var item = Clip(150);
            Stopwatch elapsed = Stopwatch.StartNew();
            queue.TryEnqueue(item);
            using CancellationTokenSource stop = new(TimeSpan.FromSeconds(3));
            Assert.That(await queue.ReadAsync(stop.Token), Is.SameAs(item));
            Assert.That(elapsed.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(140));
        }

        [Test]
        public async Task ReadAsync_CancelsWhileOnlyDelayedWorkExists()
        {
            RecordingClipQueue queue = new();
            queue.TryEnqueue(Clip(60000));
            using CancellationTokenSource stop = new();
            Task<RecordingClipWorkItem> read = queue.ReadAsync(stop.Token).AsTask();
            stop.Cancel();
            Assert.That(async () => await read, Throws.InstanceOf<OperationCanceledException>());
            Assert.That(queue.PendingCount, Is.EqualTo(1));
            await Task.CompletedTask;
        }

        private static RecordingClipWorkItem Clip(int delayMs) => new(new Camera { Name = "Test" }, DateTimeOffset.UtcNow,
            new IRecordingClipNotifier[] { new Telegram { SendRecordingClip = true, RecordingClipDownloadDelayMs = delayMs } });
    }
}
