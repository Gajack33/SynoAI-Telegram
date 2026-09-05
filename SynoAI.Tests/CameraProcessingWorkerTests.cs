using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using SynoAI.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SynoAI.Tests
{
    public class CameraProcessingWorkerTests
    {
        [Test]
        public async Task Worker_ProcessesOtherCameraWhileFirstIsWaitingAndKeepsReservation()
        {
            Config.Generate(NullLogger.Instance, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Cameras:0:Name"] = "Entree", ["Cameras:1:Name"] = "Garage"
            }).Build());
            CameraProcessingQueue queue = new(NullLogger<CameraProcessingQueue>.Instance);
            WaitingProcessor processor = new();
            using ServiceProvider services = new ServiceCollection()
                .AddSingleton<ICameraTriggerProcessor>(processor).BuildServiceProvider();
            using CameraProcessingWorker worker = new(queue, services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<CameraProcessingWorker>.Instance);
            queue.TryEnqueue("Entree");
            await worker.StartAsync(CancellationToken.None);
            try
            {
                await processor.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.That(queue.TryEnqueue("Entree").Status, Is.EqualTo(CameraEnqueueStatus.CameraAlreadyProcessing));
                queue.TryEnqueue("Garage");
                await processor.SecondFinished.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.That(queue.TryEnqueue("Entree").Status, Is.EqualTo(CameraEnqueueStatus.CameraAlreadyProcessing));
            }
            finally { await worker.StopAsync(CancellationToken.None); }
            Assert.That(queue.TryEnqueue("Entree").Status, Is.EqualTo(CameraEnqueueStatus.Queued));
        }

        [Test]
        public async Task AnalysisGate_SerializesAnalysesAndLeaseCanBeReleasedEarlyOnce()
        {
            using CameraAnalysisGate gate = new();
            using CancellationTokenSource stop = new(TimeSpan.FromSeconds(3));
            using IDisposable first = await gate.EnterAsync(stop.Token);
            Task<IDisposable> second = gate.EnterAsync(stop.Token);
            Assert.That(second.IsCompleted, Is.False);
            first.Dispose();
            first.Dispose();
            using IDisposable acquiredSecond = await second;
            Task<IDisposable> third = gate.EnterAsync(stop.Token);
            Assert.That(third.IsCompleted, Is.False);
            acquiredSecond.Dispose();
            using IDisposable acquiredThird = await third;
        }

        private sealed class WaitingProcessor : ICameraTriggerProcessor
        {
            public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource SecondFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public async Task<CameraProcessingStatus> ProcessAsync(string cameraName, CancellationToken cancellationToken)
            {
                if (cameraName == "Entree")
                {
                    FirstStarted.TrySetResult();
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                SecondFinished.TrySetResult();
                return CameraProcessingStatus.NoValidObjectDetected;
            }
        }
    }
}
