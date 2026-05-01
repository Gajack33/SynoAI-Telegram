using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using SynoAI.Services;
using System.Collections.Generic;

namespace SynoAI.Tests
{
    public class CameraProcessingQueueTests
    {
        [SetUp]
        public void Setup()
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["AI:Url"] = "http://codeproject-ai:32168",
                    ["Cameras:0:Name"] = "Entree",
                    ["Cameras:0:Threshold"] = "50"
                })
                .Build();

            Config.Generate(NullLogger.Instance, configuration);
        }

        [Test]
        public void TryEnqueue_RejectsDuplicateCameraWhileQueued()
        {
            CameraProcessingQueue queue = new(NullLogger<CameraProcessingQueue>.Instance);

            CameraEnqueueResult first = queue.TryEnqueue("Entree");
            CameraEnqueueResult second = queue.TryEnqueue("Entree");

            Assert.That(first.Status, Is.EqualTo(CameraEnqueueStatus.Queued));
            Assert.That(second.Status, Is.EqualTo(CameraEnqueueStatus.CameraAlreadyProcessing));
        }

        [Test]
        public void TryEnqueue_AllowsCameraAgainAfterComplete()
        {
            CameraProcessingQueue queue = new(NullLogger<CameraProcessingQueue>.Instance);

            queue.TryEnqueue("Entree");
            queue.Complete("Entree");
            CameraEnqueueResult result = queue.TryEnqueue("Entree");

            Assert.That(result.Status, Is.EqualTo(CameraEnqueueStatus.Queued));
        }

        [Test]
        public void TryEnqueue_RejectsCameraUnderDelay()
        {
            CameraProcessingQueue queue = new(NullLogger<CameraProcessingQueue>.Instance);

            queue.AddCameraDelay("Entree", 60000);
            CameraEnqueueResult result = queue.TryEnqueue("Entree");

            Assert.That(result.Status, Is.EqualTo(CameraEnqueueStatus.CameraDelayed));
            Assert.That(result.IgnoreUntil, Is.Not.Null);
        }
    }
}
