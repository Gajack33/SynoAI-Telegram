using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using SynoAI.App;
using SynoAI.Models;
using SynoAI.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SynoAI.Tests
{
    public class CameraStatusMonitorServiceTests
    {
        [Test]
        public async Task CheckCameraStatusesAsync_NotifiesOnlyConfirmedOfflineAndOnlineTransitions()
        {
            RecordingHttpClient httpClient = new();
            Configure(httpClient);
            SequenceSynologyService synologyService = new(new[]
            {
                Cameras(SynologyCameraStatus.Normal),
                Cameras(SynologyCameraStatus.Disconnected),
                Cameras(SynologyCameraStatus.Normal),
                Cameras(SynologyCameraStatus.Disconnected),
                Cameras(SynologyCameraStatus.Disconnected),
                Cameras(SynologyCameraStatus.Normal),
                Cameras(SynologyCameraStatus.Normal)
            });
            CameraStatusMonitorService monitor = new(
                synologyService,
                NullLogger<CameraStatusMonitorService>.Instance);

            for (int index = 0; index < 7; index++)
            {
                await monitor.CheckCameraStatusesAsync(CancellationToken.None);
            }

            Assert.That(httpClient.Requests, Has.Count.EqualTo(2));
            Assert.That(httpClient.Requests.All(x => x.Uri.AbsolutePath == "/bottoken/sendMessage"), Is.True);
            Assert.That(httpClient.Requests[0].Body, Does.Contain("Caméra hors ligne - Entree"));
            Assert.That(httpClient.Requests[1].Body, Does.Contain("Caméra en ligne - Entree"));
            Assert.That(httpClient.Requests.All(x => x.Body.Contains("321")), Is.True);
        }

        [Test]
        public async Task CheckCameraStatusesAsync_NotifiesWhenCameraIsAlreadyOfflineAtStartup()
        {
            RecordingHttpClient httpClient = new();
            Configure(httpClient);
            SequenceSynologyService synologyService = new(new[]
            {
                Cameras(SynologyCameraStatus.NoVideo),
                Cameras(SynologyCameraStatus.NoVideo)
            });
            CameraStatusMonitorService monitor = new(
                synologyService,
                NullLogger<CameraStatusMonitorService>.Instance);

            await monitor.CheckCameraStatusesAsync(CancellationToken.None);
            await monitor.CheckCameraStatusesAsync(CancellationToken.None);

            Assert.That(httpClient.Requests, Has.Count.EqualTo(1));
            Assert.That(httpClient.Requests[0].Body, Does.Contain("Caméra hors ligne - Entree"));
        }

        [Test]
        public void Tracker_DoesNotNotifyForHealthyStartupOrSingleFlap()
        {
            CameraStatusTransitionTracker tracker = new(2);

            Assert.That(tracker.Observe(true), Is.Null);
            Assert.That(tracker.Observe(false), Is.Null);
            Assert.That(tracker.Observe(true), Is.Null);
            Assert.That(tracker.Observe(false), Is.Null);
            Assert.That(tracker.Observe(false), Is.False);
            Assert.That(tracker.Observe(true), Is.Null);
            Assert.That(tracker.Observe(true), Is.True);
        }

        private static IEnumerable<SynologyCamera> Cameras(SynologyCameraStatus status)
        {
            return new[]
            {
                new SynologyCamera
                {
                    Id = 1,
                    NameNew = "Entree",
                    Status = status
                }
            };
        }

        private static void Configure(IHttpClient httpClient)
        {
            Dictionary<string, string> values = new()
            {
                ["AI:Url"] = "http://codeproject-ai:32168",
                ["CameraStatusMonitoring:Enabled"] = "true",
                ["CameraStatusMonitoring:ConfirmationCount"] = "2",
                ["Cameras:0:Name"] = "Entree",
                ["Notifiers:0:Type"] = "Telegram",
                ["Notifiers:0:ChatID"] = "1",
                ["Notifiers:0:Token"] = "token",
                ["Notifiers:0:Language"] = "fr",
                ["Notifiers:0:CameraMessageThreadIDs:Entree"] = "321"
            };
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .Build();

            Config.Generate(NullLogger.Instance, configuration, httpClient);
        }

        private sealed class SequenceSynologyService : ISynologyService
        {
            private readonly Queue<IEnumerable<SynologyCamera>> _responses;
            private IEnumerable<SynologyCamera> _lastResponse = Array.Empty<SynologyCamera>();

            public SequenceSynologyService(IEnumerable<IEnumerable<SynologyCamera>> responses)
            {
                _responses = new Queue<IEnumerable<SynologyCamera>>(responses);
            }

            public Task<IEnumerable<SynologyCamera>> GetCamerasAsync()
            {
                if (_responses.Count > 0)
                {
                    _lastResponse = _responses.Dequeue();
                }

                return Task.FromResult(_lastResponse);
            }

            public Task InitialiseAsync() => Task.CompletedTask;
            public Task<Cookie> LoginAsync() => Task.FromResult<Cookie>(null);
            public Task<byte[]> TakeSnapshotAsync(string cameraName) => Task.FromResult<byte[]>(null);
            public Task<ProcessedFile> DownloadLatestRecordingClipAsync(
                string cameraName,
                DateTimeOffset detectedAt,
                int offsetTimeMs,
                int playTimeMs) => Task.FromResult<ProcessedFile>(null);
        }

        private sealed class RecordingHttpClient : IHttpClient
        {
            public TimeSpan Timeout { get; set; }
            public List<(Uri Uri, string Body)> Requests { get; } = new();

            public Task<HttpResponseMessage> PostAsync(string requestUri, HttpContent content)
            {
                return PostAsync(new Uri(requestUri), content, CancellationToken.None);
            }

            public Task<HttpResponseMessage> PostAsync(Uri requestUri, HttpContent content)
            {
                return PostAsync(requestUri, content, CancellationToken.None);
            }

            public async Task<HttpResponseMessage> PostAsync(
                Uri requestUri,
                HttpContent content,
                CancellationToken cancellationToken)
            {
                Requests.Add((requestUri, await content.ReadAsStringAsync(cancellationToken)));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"{""ok"":true}")
                };
            }
        }
    }
}
