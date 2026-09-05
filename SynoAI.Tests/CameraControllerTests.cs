using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using SkiaSharp;
using SynoAI.App;
using SynoAI.Controllers;
using SynoAI.Models;
using SynoAI.Notifiers;
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
        private string _previousCurrentDirectory;
        private string _workspace;

        [SetUp]
        public void Setup()
        {
            _previousCurrentDirectory = Environment.CurrentDirectory;
            _workspace = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_workspace);
            Environment.CurrentDirectory = _workspace;
        }

        [TearDown]
        public void TearDown()
        {
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
        public async Task Processor_SendsTelegramPhotoAndQueuesRecordingClipWithoutWaitingForDownload()
        {
            FakeHttpClient httpClient = new();
            Configure(httpClient: httpClient);

            FakeSynologyService synologyService = new(CreateJpeg(640, 360));
            FakeRecordingClipQueue recordingClipQueue = new();

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
                recordingClipQueue,
                new DetectionMemory(),
                NullLogger<CameraTriggerProcessor>.Instance);

            CameraProcessingStatus status = await processor.ProcessAsync("Entree", CancellationToken.None);

            Assert.That(status, Is.EqualTo(CameraProcessingStatus.ValidObjectDetected));
            Assert.That(synologyService.ClipDownloadCalls, Is.Zero);
            Assert.That(recordingClipQueue.WorkItems, Has.Count.EqualTo(1));
            Assert.That(httpClient.Requests.Select(x => x.AbsolutePath), Is.EqualTo(new[] { "/bottoken/sendPhoto" }));
        }

        [Test]
        public async Task Processor_ReturnsNotificationFailedWhenTelegramPhotoFails()
        {
            FakeHttpClient httpClient = new()
            {
                StatusCode = HttpStatusCode.BadRequest,
                ResponseBody = @"{""ok"":false,""description"":""bad request""}"
            };
            Configure(new Dictionary<string, string>
            {
                ["HttpRetryCount"] = "0",
                ["Notifiers:0:SendRecordingClip"] = "false"
            }, httpClient);

            FakeCameraQueue queue = new(new CameraEnqueueResult(CameraEnqueueStatus.Queued));
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
                new FakeSynologyService(CreateJpeg(640, 360)),
                queue,
                new FakeRecordingClipQueue(),
                new DetectionMemory(),
                NullLogger<CameraTriggerProcessor>.Instance);

            CameraProcessingStatus status = await processor.ProcessAsync("Entree", CancellationToken.None);

            Assert.That(status, Is.EqualTo(CameraProcessingStatus.NotificationFailed));
            Assert.That(httpClient.Requests.Select(x => x.AbsolutePath), Is.EqualTo(new[] { "/bottoken/sendPhoto" }));
            Assert.That(queue.Delays, Is.EqualTo(new[] { Config.AIFailureDelayMs }));
        }

        [Test]
        public async Task Processor_FiltersPredictionsAfterClampingToImageBounds()
        {
            FakeHttpClient httpClient = new();
            Configure(new Dictionary<string, string>
            {
                ["Cameras:0:MinSizeX"] = "50",
                ["Cameras:0:MinSizeY"] = "50",
                ["Notifiers:0:SendRecordingClip"] = "false"
            }, httpClient);

            CameraTriggerProcessor processor = new(
                new FakeAIService(new[]
                {
                    new AIPrediction
                    {
                        Label = "person",
                        Confidence = 90,
                        MinX = -100,
                        MinY = -100,
                        MaxX = 30,
                        MaxY = 30
                    }
                }),
                new FakeSynologyService(CreateJpeg(64, 64)),
                new FakeCameraQueue(new CameraEnqueueResult(CameraEnqueueStatus.Queued)),
                new FakeRecordingClipQueue(),
                new DetectionMemory(),
                NullLogger<CameraTriggerProcessor>.Instance);

            CameraProcessingStatus status = await processor.ProcessAsync("Entree", CancellationToken.None);

            Assert.That(status, Is.EqualTo(CameraProcessingStatus.NoValidObjectDetected));
            Assert.That(httpClient.Requests, Is.Empty);
        }

        [Test]
        public async Task RecordingClipProcessor_DownloadsAndSendsQueuedClip()
        {
            FakeHttpClient httpClient = new();
            Configure(httpClient: httpClient);

            string clipPath = Path.Combine(_workspace, "clip.mp4");
            File.WriteAllBytes(clipPath, new byte[] { 1, 2, 3 });
            FakeSynologyService synologyService = new(CreateJpeg(640, 360))
            {
                ClipFilePath = clipPath
            };

            IRecordingClipNotifier notifier = Config.Notifiers.OfType<IRecordingClipNotifier>().Single();
            RecordingClipWorkItem workItem = new(
                Config.Cameras.Single(),
                DateTimeOffset.Now,
                new[] { notifier });
            RecordingClipProcessor processor = new(
                synologyService,
                NullLogger<RecordingClipProcessor>.Instance);

            await processor.ProcessAsync(workItem, CancellationToken.None);

            Assert.That(synologyService.ClipDownloadCalls, Is.EqualTo(1));
            Assert.That(httpClient.Requests.Select(x => x.AbsolutePath), Is.EqualTo(new[] { "/bottoken/sendVideo" }));
        }

        [Test]
        public async Task Processor_PerfectShot_SelectsHighestConfidenceSnapshot()
        {
            FakeHttpClient httpClient = new();
            Configure(new Dictionary<string, string>
            {
                ["PerfectShotEnabled"] = "true",
                ["MaxSnapshots"] = "3",
                ["DrawMode"] = "Off",
                ["Notifiers:0:SendRecordingClip"] = "false"
            }, httpClient);

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
                new FakeRecordingClipQueue(),
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
        public async Task Processor_PerfectShot_PreservesCandidateAfterLaterAiFailure()
        {
            FakeHttpClient httpClient = new();
            Configure(new Dictionary<string, string>
            {
                ["PerfectShotEnabled"] = "true",
                ["MaxSnapshots"] = "3",
                ["DrawMode"] = "Off",
                ["Notifiers:0:SendRecordingClip"] = "false"
            }, httpClient);

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
                null
            });

            CameraTriggerProcessor processor = new(
                aiService,
                synologyService,
                new FakeCameraQueue(new CameraEnqueueResult(CameraEnqueueStatus.Queued)),
                new FakeRecordingClipQueue(),
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
        public async Task Processor_MinimumSizeRejectsSmallPersonFalsePositive()
        {
            FakeHttpClient httpClient = new();
            Configure(new Dictionary<string, string>
            {
                ["Cameras:0:MinSizeX"] = "100",
                ["Cameras:0:MinSizeY"] = "200",
                ["Notifiers:0:SendRecordingClip"] = "false"
            }, httpClient);

            CameraTriggerProcessor processor = new(
                new FakeAIService(new[]
                {
                    new AIPrediction
                    {
                        Label = "person",
                        Confidence = 83,
                        MinX = 597,
                        MinY = 676,
                        MaxX = 667,
                        MaxY = 836
                    }
                }),
                new FakeSynologyService(CreateJpeg(960, 1080)),
                new FakeCameraQueue(new CameraEnqueueResult(CameraEnqueueStatus.Queued)),
                new FakeRecordingClipQueue(),
                new DetectionMemory(),
                NullLogger<CameraTriggerProcessor>.Instance);

            CameraProcessingStatus status = await processor.ProcessAsync("Entree", CancellationToken.None);

            Assert.That(status, Is.EqualTo(CameraProcessingStatus.NoValidObjectDetected));
            Assert.That(httpClient.Requests, Is.Empty);
        }

        [Test]
        public async Task Processor_StationaryObjectFilter_SuppressesRepeatedObject()
        {
            FakeHttpClient httpClient = new();
            Configure(new Dictionary<string, string>
            {
                ["StationaryObjectIgnoreSeconds"] = "300",
                ["StationaryObjectMovementThresholdPixels"] = "20",
                ["Notifiers:0:SendRecordingClip"] = "false"
            }, httpClient);

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
                new FakeRecordingClipQueue(),
                detectionMemory,
                NullLogger<CameraTriggerProcessor>.Instance);

            CameraProcessingStatus firstStatus = await processor.ProcessAsync("Entree", CancellationToken.None);
            CameraProcessingStatus secondStatus = await processor.ProcessAsync("Entree", CancellationToken.None);

            Assert.That(firstStatus, Is.EqualTo(CameraProcessingStatus.ValidObjectDetected));
            Assert.That(secondStatus, Is.EqualTo(CameraProcessingStatus.NoValidObjectDetected));
            Assert.That(httpClient.Requests.Select(x => x.AbsolutePath), Is.EqualTo(new[] { "/bottoken/sendPhoto" }));
        }

        [Test]
        public async Task Processor_PerfectShot_PreservesCandidateAfterLaterSnapshotException()
        {
            FakeHttpClient client = new();
            Configure(new Dictionary<string, string>
            {
                ["PerfectShotEnabled"] = "true", ["MaxSnapshots"] = "2", ["DrawMode"] = "Off"
            }, client);
            FakeSynologyService synology = new(CreateJpeg(64, 64)) { ThrowOnSnapshotCall = 2 };
            CameraTriggerProcessor processor = new(new FakeAIService(new[] { ValidPerson() }), synology,
                new FakeCameraQueue(new CameraEnqueueResult(CameraEnqueueStatus.Queued)), new FakeRecordingClipQueue(),
                new DetectionMemory(), NullLogger<CameraTriggerProcessor>.Instance);
            Assert.That(await processor.ProcessAsync("Entree", CancellationToken.None), Is.EqualTo(CameraProcessingStatus.ValidObjectDetected));
            Assert.That(client.Requests, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task Processor_AnotherCameraCanFinishWhileFirstNotificationWaits()
        {
            FakeHttpClient client = new() { BlockFirstRequest = true };
            Configure(new Dictionary<string, string>
            {
                ["Cameras:1:Name"] = "Garage", ["Cameras:1:MinSizeX"] = "1", ["Cameras:1:MinSizeY"] = "1", ["DrawMode"] = "Off"
            }, client);
            using CameraAnalysisGate gate = new();
            CameraTriggerProcessor processor = new(new FakeAIService(new[] { ValidPerson() }), new FakeSynologyService(CreateJpeg(64, 64)),
                new FakeCameraQueue(new CameraEnqueueResult(CameraEnqueueStatus.Queued)), new FakeRecordingClipQueue(),
                new DetectionMemory(), NullLogger<CameraTriggerProcessor>.Instance, gate);
            using CancellationTokenSource stop = new();
            Task<CameraProcessingStatus> first = processor.ProcessAsync("Entree", stop.Token);
            try
            {
                await client.FirstRequestArrived.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var second = await processor.ProcessAsync("Garage", stop.Token).WaitAsync(TimeSpan.FromSeconds(3));
                Assert.That(second, Is.EqualTo(CameraProcessingStatus.ValidObjectDetected));
                Assert.That(first.IsCompleted, Is.False);
            }
            finally
            {
                stop.Cancel();
                client.ReleaseFirstRequest.TrySetResult();
                await first;
            }
        }

        private static AIPrediction ValidPerson() => new() { Label = "person", Confidence = 95, MinX = 1, MinY = 1, MaxX = 25, MaxY = 25 };

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

        private static void Configure(Dictionary<string, string> overrides = null, IHttpClient httpClient = null)
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

            Config.Generate(NullLogger.Instance, configuration, httpClient);
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

            public Task<IEnumerable<AIPrediction>> ProcessAsync(Camera camera, byte[] image, CancellationToken cancellationToken = default)
            {
                ProcessCalls++;
                return Task.FromResult(_predictions.Count > 1 ? _predictions.Dequeue() : _predictions.Peek());
            }

            public Task<bool> WarmupAsync(CancellationToken cancellationToken = default)
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
            public int? ThrowOnSnapshotCall { get; set; }
            public string ClipFilePath { get; set; }
            public int ClipDownloadCalls { get; private set; }
            public int SnapshotCalls { get; private set; }

            public Task InitialiseAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task<Cookie> LoginAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult<Cookie>(null);
            }

            public Task<IEnumerable<SynologyCamera>> GetCamerasAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IEnumerable<SynologyCamera>>(Array.Empty<SynologyCamera>());
            }

            public Task<byte[]> TakeSnapshotAsync(string cameraName, CancellationToken cancellationToken = default)
            {
                SnapshotCalls++;
                if (SnapshotCalls == ThrowOnSnapshotCall) throw new HttpRequestException("Simulated snapshot failure");
                return Task.FromResult(_snapshots.Count > 1 ? _snapshots.Dequeue() : _snapshots.Peek());
            }

            public Task<ProcessedFile> DownloadLatestRecordingClipAsync(string cameraName, DateTimeOffset detectedAt, int offsetTimeMs, int playTimeMs, CancellationToken cancellationToken = default)
            {
                ClipDownloadCalls++;

                if (ThrowOnClipDownload)
                {
                    throw new InvalidOperationException("clip unavailable");
                }

                if (!string.IsNullOrWhiteSpace(ClipFilePath))
                {
                    return Task.FromResult(new ProcessedFile(ClipFilePath));
                }

                return Task.FromResult<ProcessedFile>(null);
            }
        }

        private sealed class FakeRecordingClipQueue : IRecordingClipQueue
        {
            public int PendingCount => WorkItems.Count;
            public List<RecordingClipWorkItem> WorkItems { get; } = new();

            public bool TryEnqueue(RecordingClipWorkItem workItem)
            {
                WorkItems.Add(workItem);
                return true;
            }

            public ValueTask<RecordingClipWorkItem> ReadAsync(CancellationToken cancellationToken)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class FakeCameraQueue : ICameraProcessingQueue
        {
            public int PendingCount => EnqueuedCameraNames.Count;
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
            public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
            public string ResponseBody { get; set; } = @"{""ok"":true}";
            public bool BlockFirstRequest { get; set; }
            public TaskCompletionSource FirstRequestArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource ReleaseFirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<HttpResponseMessage> PostAsync(string requestUri, HttpContent content)
            {
                return PostAsync(new Uri(requestUri), content);
            }

            public Task<HttpResponseMessage> PostAsync(Uri requestUri, HttpContent content)
            {
                return PostAsync(requestUri, content, CancellationToken.None);
            }

            public async Task<HttpResponseMessage> PostAsync(Uri requestUri, HttpContent content, CancellationToken cancellationToken)
            {
                Requests.Add(requestUri);
                if (BlockFirstRequest && Requests.Count == 1)
                {
                    FirstRequestArrived.TrySetResult();
                    await ReleaseFirstRequest.Task.WaitAsync(cancellationToken);
                }
                return new HttpResponseMessage(StatusCode) { Content = new StringContent(ResponseBody) };
            }
        }
    }
}
