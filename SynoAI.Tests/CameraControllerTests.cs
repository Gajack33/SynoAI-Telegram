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
                new DetectionMemory(),
                NullLogger<CameraTriggerProcessor>.Instance);

            CameraProcessingStatus status = await processor.ProcessAsync("Entree", CancellationToken.None);

            Assert.That(status, Is.EqualTo(CameraProcessingStatus.ValidObjectDetected));
            Assert.That(synologyService.ClipDownloadCalls, Is.EqualTo(1));
            Assert.That(httpClient.Requests.Select(x => x.AbsolutePath), Is.EqualTo(new[] { "/bottoken/sendPhoto" }));
        }

        [Test]
        public async Task Processor_PerfectShot_SelectsHighestConfidenceSnapshot()
        {
            Configure(new Dictionary<string, string>
            {
                ["PerfectShotEnabled"] = "true",
                ["MaxSnapshots"] = "3",
                ["DrawMode"] = "Off",
                ["Notifiers:0:SendRecordingClip"] = "false"
            });

            FakeHttpClient httpClient = new();
            Shared.HttpClient = httpClient;

            FakeSynologyService synologyService = new(new[]
            {
                CreateJpeg(64, 64, SKColors.Red),
                CreateJpeg(64, 64, SKColors.Lime),
                CreateJpeg(64, 64, SKColors.Blue)
            });

            FakeAIService aiService = new(new[]
            {
                new[]
                {
                    new AIPrediction { Label = "person", Confidence = 60, MinX = 1, MinY = 1, MaxX = 20, MaxY = 20 }
                },
                new[]
                {
                    new AIPrediction { Label = "person", Confidence = 95, MinX = 1, MinY = 1, MaxX = 20, MaxY = 20 }
                },
                Array.Empty<AIPrediction>()
            });

            CameraTriggerProcessor processor = new(
                aiService,
                synologyService,
                new FakeCameraQueue(new CameraEnqueueResult(CameraEnqueueStatus.Queued)),
                new DetectionMemory(),
                NullLogger<CameraTriggerProcessor>.Instance);

            CameraProcessingStatus status = await processor.ProcessAsync("Entree", CancellationToken.None);

            Assert.That(status, Is.EqualTo(CameraProcessingStatus.ValidObjectDetected));
            Assert.That(synologyService.SnapshotCalls, Is.EqualTo(3));
            Assert.That(aiService.ProcessCalls, Is.EqualTo(3));
            Assert.That(httpClient.Requests.Select(x => x.AbsolutePath), Is.EqualTo(new[] { "/bottoken/sendPhoto" }));

            string savedCapture = Directory.GetFiles(Path.Combine("Captures", "Entree"), "*.jpeg").Single();
            using SKBitmap bitmap = SKBitmap.Decode(savedCapture);
            SKColor pixel = bitmap.GetPixel(5, 5);
            Assert.That(pixel.Green, Is.GreaterThan(pixel.Red));
            Assert.That(pixel.Green, Is.GreaterThan(pixel.Blue));
        }

        [Test]
        public async Task Processor_StationaryObjectFilter_SuppressesRepeatedObject()
        {
            Configure(new Dictionary<string, string>
            {
                ["StationaryObjectIgnoreSeconds"] = "300",
                ["StationaryObjectMovementThresholdPixels"] = "20",
                ["Notifiers:0:SendRecordingClip"] = "false"
            });

            FakeHttpClient httpClient = new();
            Shared.HttpClient = httpClient;

            DetectionMemory detectionMemory = new();
            FakeAIService aiService = new(new[]
            {
                new[]
                {
                    new AIPrediction { Label = "person", Confidence = 90, MinX = 10, MinY = 20, MaxX = 80, MaxY = 160 }
                }
            });

            CameraTriggerProcessor processor = new(
                aiService,
                new FakeSynologyService(new[]
                {
                    CreateJpeg(640, 360, SKColors.White),
                    CreateJpeg(640, 360, SKColors.LightGray)
                }),
                new FakeCameraQueue(new CameraEnqueueResult(CameraEnqueueStatus.Queued)),
                detectionMemory,
                NullLogger<CameraTriggerProcessor>.Instance);

            CameraProcessingStatus firstStatus = await processor.ProcessAsync("Entree", CancellationToken.None);
            CameraProcessingStatus secondStatus = await processor.ProcessAsync("Entree", CancellationToken.None);

            Assert.That(firstStatus, Is.EqualTo(CameraProcessingStatus.ValidObjectDetected));
            Assert.That(secondStatus, Is.EqualTo(CameraProcessingStatus.NoValidObjectDetected));
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

        private static void Configure(Dictionary<string, string> overrides = null)
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

            if (overrides != null)
            {
                foreach (KeyValuePair<string, string> pair in overrides)
                {
                    values[pair.Key] = pair.Value;
                }
            }

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .Build();

            Config.Generate(NullLogger.Instance, configuration);
        }

        private static byte[] CreateJpeg(int width, int height)
        {
            return CreateJpeg(width, height, SKColors.White);
        }

        private static byte[] CreateJpeg(int width, int height, SKColor color)
        {
            using SKBitmap bitmap = new(width, height);
            using SKCanvas canvas = new(bitmap);
            canvas.Clear(color);

            using SKImage image = SKImage.FromBitmap(bitmap);
            using SKData data = image.Encode(SKEncodedImageFormat.Jpeg, 100);
            return data.ToArray();
        }

        private sealed class FakeAIService : IAIService
        {
            private readonly Queue<IEnumerable<AIPrediction>> _predictions;

            public FakeAIService(IEnumerable<AIPrediction> predictions)
                : this(new[] { predictions })
            {
            }

            public FakeAIService(IEnumerable<IEnumerable<AIPrediction>> predictions)
            {
                _predictions = new Queue<IEnumerable<AIPrediction>>(predictions);
            }

            public int ProcessCalls { get; private set; }

            public Task<IEnumerable<AIPrediction>> ProcessAsync(Camera camera, byte[] image)
            {
                ProcessCalls++;
                return Task.FromResult(_predictions.Count > 1 ? _predictions.Dequeue() : _predictions.Peek());
            }

            public Task<bool> WarmupAsync()
            {
                return Task.FromResult(true);
            }
        }

        private sealed class FakeSynologyService : ISynologyService
        {
            private readonly Queue<byte[]> _snapshots;

            public FakeSynologyService(byte[] snapshot)
                : this(new[] { snapshot })
            {
            }

            public FakeSynologyService(IEnumerable<byte[]> snapshots)
            {
                _snapshots = new Queue<byte[]>(snapshots);
            }

            public bool ThrowOnClipDownload { get; set; }
            public int ClipDownloadCalls { get; private set; }
            public int SnapshotCalls { get; private set; }

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
                SnapshotCalls++;
                return Task.FromResult(_snapshots.Count > 1 ? _snapshots.Dequeue() : _snapshots.Peek());
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
