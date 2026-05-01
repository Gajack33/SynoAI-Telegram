using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using SkiaSharp;
using SynoAI.App;
using SynoAI.Controllers;
using SynoAI.Models;
using SynoAI.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SynoAI.Tests
{
    public class CameraControllerTests
    {
        private IHttpClient _previousHttpClient;
        private string _previousCurrentDirectory;
        private string _workspace;

        [SetUp]
        public void Setup()
        {
            _previousHttpClient = Shared.HttpClient;
            _previousCurrentDirectory = Environment.CurrentDirectory;
            _workspace = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_workspace);
            Environment.CurrentDirectory = _workspace;
        }

        [TearDown]
        public void TearDown()
        {
            Shared.HttpClient = _previousHttpClient;
            Environment.CurrentDirectory = _previousCurrentDirectory;

            if (Directory.Exists(_workspace))
            {
                Directory.Delete(_workspace, recursive: true);
            }
        }

        [Test]
        public void Get_QueuesCameraTriggerAndReturnsAccepted()
        {
            Configure();

            FakeCameraQueue queue = new(new CameraEnqueueResult(CameraEnqueueStatus.Queued));
            CameraController controller = CreateController(queue);

            IActionResult result = controller.Get("Entree");

            ObjectResult accepted = result as ObjectResult;
            Assert.That(accepted, Is.Not.Null);
            Assert.That(accepted.StatusCode, Is.EqualTo(202));
            Assert.That(accepted.Value, Is.EqualTo("Camera trigger queued."));
            Assert.That(queue.EnqueuedCameraNames, Is.EqualTo(new[] { "Entree" }));
        }

        [Test]
        public async Task Processor_SendsTelegramPhotoEvenWhenRecordingClipDownloadFails()
        {
            Configure();

            FakeHttpClient httpClient = new();
            Shared.HttpClient = httpClient;

            FakeSynologyService synologyService = new(CreateJpeg(640, 360))
            {
                ThrowOnClipDownload = true
            };

            CameraTriggerProcessor processor = new(
                new FakeAIService(new[]
                {
                    new AIPrediction
                    {
                        Label = "person",
                        Confidence = 90,
                        MinX = 10,
                        MinY = 20,
                        MaxX = 80,
                        MaxY = 160
                    }
                }),
                synologyService,
                new FakeCameraQueue(new CameraEnqueueResult(CameraEnqueueStatus.Queued)),
                NullLogger<CameraTriggerProcessor>.Instance);

            CameraProcessingStatus status = await processor.ProcessAsync("Entree", CancellationToken.None);

            Assert.That(status, Is.EqualTo(CameraProcessingStatus.ValidObjectDetected));
            Assert.That(synologyService.ClipDownloadCalls, Is.EqualTo(1));
            Assert.That(httpClient.Requests.Select(x => x.AbsolutePath), Is.EqualTo(new[] { "/bottoken/sendPhoto" }));
        }

        private static CameraController CreateController(ICameraProcessingQueue queue)
        {
            DefaultHttpContext httpContext = new();
            httpContext.Request.QueryString = new QueryString("?token=secret");

            return new CameraController(queue, NullLogger<CameraController>.Instance)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = httpContext
                }
            };
        }

        private static void Configure()
        {
            Dictionary<string, string> values = new()
            {
                ["AccessToken"] = "secret",
                ["AI:Url"] = "http://codeproject-ai:32168",
                ["OutputJpegQuality"] = "100",
                ["Cameras:0:Name"] = "Entree",
                ["Cameras:0:Types:0"] = "person",
                ["Cameras:0:Threshold"] = "50",
                ["Cameras:0:MinSizeX"] = "1",
                ["Cameras:0:MinSizeY"] = "1",
                ["Notifiers:0:Type"] = "Telegram",
                ["Notifiers:0:ChatID"] = "1",
                ["Notifiers:0:Token"] = "token",
                ["Notifiers:0:SendRecordingClip"] = "true",
                ["Notifiers:0:RecordingClipDownloadDelayMs"] = "0",
                ["Notifiers:0:RecordingClipDurationMs"] = "10000"
            };

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .Build();

            Config.Generate(NullLogger.Instance, configuration);
        }

        private static byte[] CreateJpeg(int width, int height)
        {
            using SKBitmap bitmap = new(width, height);
            using SKCanvas canvas = new(bitmap);
            canvas.Clear(SKColors.White);

            using SKImage image = SKImage.FromBitmap(bitmap);
            using SKData data = image.Encode(SKEncodedImageFormat.Jpeg, 100);
            return data.ToArray();
        }

        private sealed class FakeAIService : IAIService
        {
            private readonly IEnumerable<AIPrediction> _predictions;

            public FakeAIService(IEnumerable<AIPrediction> predictions)
            {
                _predictions = predictions;
            }

            public Task<IEnumerable<AIPrediction>> ProcessAsync(Camera camera, byte[] image)
            {
                return Task.FromResult(_predictions);
            }

            public Task<bool> WarmupAsync()
            {
                return Task.FromResult(true);
            }
        }

        private sealed class FakeSynologyService : ISynologyService
        {
            private readonly byte[] _snapshot;

            public FakeSynologyService(byte[] snapshot)
            {
                _snapshot = snapshot;
            }

            public bool ThrowOnClipDownload { get; set; }
            public int ClipDownloadCalls { get; private set; }

            public Task InitialiseAsync()
            {
                return Task.CompletedTask;
            }

            public Task<Cookie> LoginAsync()
            {
                return Task.FromResult<Cookie>(null);
            }

            public Task<IEnumerable<SynologyCamera>> GetCamerasAsync()
            {
                return Task.FromResult<IEnumerable<SynologyCamera>>(Array.Empty<SynologyCamera>());
            }

            public Task<byte[]> TakeSnapshotAsync(string cameraName)
            {
                return Task.FromResult(_snapshot);
            }

            public Task<ProcessedFile> DownloadLatestRecordingClipAsync(string cameraName, int offsetTimeMs, int playTimeMs)
            {
                ClipDownloadCalls++;

                if (ThrowOnClipDownload)
                {
                    throw new InvalidOperationException("clip unavailable");
                }

                return Task.FromResult<ProcessedFile>(null);
            }
        }

        private sealed class FakeCameraQueue : ICameraProcessingQueue
        {
            private readonly CameraEnqueueResult _enqueueResult;

            public FakeCameraQueue(CameraEnqueueResult enqueueResult)
            {
                _enqueueResult = enqueueResult;
            }

            public List<string> EnqueuedCameraNames { get; } = new();
            public List<int> Delays { get; } = new();

            public CameraEnqueueResult TryEnqueue(string cameraName)
            {
                EnqueuedCameraNames.Add(cameraName);
                return _enqueueResult;
            }

            public void SetCameraEnabled(string cameraName, bool enabled)
            {
            }

            public void AddCameraDelay(string cameraName, int delayMs)
            {
                Delays.Add(delayMs);
            }

            public void Complete(string cameraName)
            {
            }

            public ValueTask<CameraTriggerWorkItem> ReadAsync(CancellationToken cancellationToken)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class FakeHttpClient : IHttpClient
        {
            public TimeSpan Timeout { get; set; }
            public List<Uri> Requests { get; } = new();

            public Task<HttpResponseMessage> PostAsync(string requestUri, HttpContent content)
            {
                return PostAsync(new Uri(requestUri), content);
            }

            public Task<HttpResponseMessage> PostAsync(Uri requestUri, HttpContent content)
            {
                return PostAsync(requestUri, content, CancellationToken.None);
            }

            public Task<HttpResponseMessage> PostAsync(Uri requestUri, HttpContent content, CancellationToken cancellationToken)
            {
                Requests.Add(requestUri);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"{""ok"":true}")
                });
            }
        }
    }
}
