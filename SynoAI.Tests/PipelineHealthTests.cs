using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using SynoAI.Controllers;
using SynoAI.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SynoAI.Tests
{
    public class PipelineHealthTests
    {
        private string _previousDirectory;
        private string _workspace;
        [SetUp]
        public void SetUp()
        {
            _previousDirectory = Environment.CurrentDirectory;
            _workspace = Path.Combine(Path.GetTempPath(), "synoai-health-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_workspace);
            Environment.CurrentDirectory = _workspace;
        }
        [TearDown]
        public void TearDown()
        {
            Environment.CurrentDirectory = _previousDirectory;
            Directory.Delete(_workspace, true);
        }

        [Test]
        public async Task HealthCheck_ReportsStallFailureAndRecovery()
        {
            MutableClock clock = new();
            PipelineDiagnostics diagnostics = new(clock);
            PipelineHealthCheck health = new(diagnostics);
            using var snapshot = diagnostics.Begin("snapshot", "Entree", CancellationToken.None, TimeSpan.FromSeconds(10));
            clock.Advance(TimeSpan.FromSeconds(11));
            Assert.That(diagnostics.GetSnapshot().ActiveOperations.Single().IsStalled, Is.True);
            Assert.That((await health.CheckHealthAsync(new())).Status, Is.EqualTo(HealthStatus.Unhealthy));
            snapshot.Complete(false);
            Assert.That(diagnostics.GetSnapshot().Stages.Single().LastFailureAt, Is.Not.Null);
            Assert.That((await health.CheckHealthAsync(new())).Status, Is.EqualTo(HealthStatus.Unhealthy));
            using var recovered = diagnostics.Begin("snapshot", "Entree", CancellationToken.None);
            recovered.Complete(true);
            Assert.That((await health.CheckHealthAsync(new())).Status, Is.EqualTo(HealthStatus.Healthy));
            Assert.That(diagnostics.GetSnapshot().Stages.Single().LastSuccessAt, Is.Not.Null);
            Assert.That(Directory.GetFiles(Constants.DIRECTORY_CAPTURES), Is.Empty);
        }

        [Test]
        public async Task HealthCheck_ReportsUnavailableCaptureDirectory()
        {
            File.WriteAllText(Constants.DIRECTORY_CAPTURES, "This is a file, not a writable directory");
            PipelineHealthCheck health = new(new PipelineDiagnostics());
            Assert.That((await health.CheckHealthAsync(new())).Status, Is.EqualTo(HealthStatus.Unhealthy));
        }

        [Test]
        public void Diagnostics_ShutdownDoesNotRecordFailure()
        {
            PipelineDiagnostics diagnostics = new();
            using CancellationTokenSource stop = new();
            using (diagnostics.Begin("snapshot", "Entree", stop.Token)) stop.Cancel();
            Assert.That(diagnostics.GetSnapshot().IsUnhealthy, Is.False);
            Assert.That(diagnostics.GetSnapshot().ActiveOperations, Is.Empty);
            Assert.That(diagnostics.GetSnapshot().Stages, Is.Empty);
        }

        [Test]
        public void DiagnosticsEndpoint_RequiresActionTokenAndExposesQueueDepth()
        {
            Config.Generate(NullLogger.Instance, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
            {
                ["AccessToken"] = "action-test-token", ["ImageAccessToken"] = "image-test-token", ["Cameras:0:Name"] = "Entree"
            }).Build());
            CameraProcessingQueue queue = new(NullLogger<CameraProcessingQueue>.Instance);
            queue.TryEnqueue("Entree");
            DiagnosticsController controller = new(new PipelineDiagnostics(), queue, new RecordingClipQueue())
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
            Assert.That(controller.Get(), Is.TypeOf<UnauthorizedResult>());
            controller.Request.Headers["X-SynoAI-Token"] = "image-test-token";
            Assert.That(controller.Get(), Is.TypeOf<UnauthorizedResult>());
            controller.Request.Headers["X-SynoAI-Token"] = "action-test-token";
            var result = (OkObjectResult)controller.Get();
            string json = System.Text.Json.JsonSerializer.Serialize(result.Value);
            Assert.That(json, Does.Contain("\"CameraQueueDepth\":1"));
            Assert.That(json, Does.Not.Contain("action-test-token"));
        }

        private sealed class MutableClock : TimeProvider
        {
            private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            public override DateTimeOffset GetUtcNow() => _now;
            public void Advance(TimeSpan delta) => _now += delta;
        }
    }
}
