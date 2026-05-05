using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using SynoAI.App;
using SynoAI.Models;
using SynoAI.Notifiers.Telegram;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SynoAI.Tests
{
    public class TelegramTests
    {
        private string _workspace;

        [SetUp]
        public void Setup()
        {
            _workspace = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_workspace);
            Configure();
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_workspace))
            {
                Directory.Delete(_workspace, recursive: true);
            }
        }

        [Test]
        public async Task SendAsync_UsesEnglishCaptionByDefault()
        {
            string imagePath = Path.Combine(_workspace, "capture.jpeg");
            File.WriteAllBytes(imagePath, new byte[] { 1, 2, 3 });

            FakeHttpClient httpClient = new();

            Telegram telegram = new(httpClient)
            {
                ChatID = "1",
                Token = "token"
            };

            Camera camera = new()
            {
                Name = "Entree",
                Threshold = 50,
                MinSizeX = 40,
                MinSizeY = 80
            };

            Notification notification = new()
            {
                CreatedAt = new DateTime(2026, 4, 23, 7, 44, 12),
                ProcessedImage = new ProcessedImage(imagePath),
                ValidPredictions = new List<AIPrediction>
                {
                    new()
                    {
                        Label = "person",
                        Confidence = 87,
                        MinX = 199,
                        MinY = 103,
                        MaxX = 225,
                        MaxY = 181
                    }
                }
            };

            await telegram.SendAsync(camera, notification, NullLogger.Instance);

            Assert.That(httpClient.RequestBody, Does.Contain("Camera alert - Entree"));
            Assert.That(httpClient.RequestBody, Does.Contain("Time: 4/23/2026 7:44"));
            Assert.That(httpClient.RequestBody, Does.Contain("AM"));
            Assert.That(httpClient.RequestBody, Does.Contain("Detection: Person"));
            Assert.That(httpClient.RequestBody, Does.Not.Contain("Action: check the image."));
            Assert.That(httpClient.RequestBody, Does.Not.Contain("Video:"));
            Assert.That(httpClient.RequestBody, Does.Not.Contain("26x78px"));
            Assert.That(httpClient.RequestBody, Does.Not.Contain("Camera threshold"));
            Assert.That(httpClient.RequestBody, Does.Not.Contain("Min. size"));
        }

        [Test]
        public async Task SendAsync_UsesConfiguredFrenchCaption()
        {
            string imagePath = Path.Combine(_workspace, "capture.jpeg");
            File.WriteAllBytes(imagePath, new byte[] { 1, 2, 3 });

            FakeHttpClient httpClient = new();

            Telegram telegram = new(httpClient)
            {
                ChatID = "1",
                Token = "token",
                Language = "fr"
            };

            Camera camera = new()
            {
                Name = "Entree",
                Threshold = 50,
                MinSizeX = 40,
                MinSizeY = 80
            };

            Notification notification = new()
            {
                CreatedAt = new DateTime(2026, 4, 23, 7, 44, 12),
                ProcessedImage = new ProcessedImage(imagePath),
                ValidPredictions = new List<AIPrediction>
                {
                    new()
                    {
                        Label = "person",
                        Confidence = 87,
                        MinX = 199,
                        MinY = 103,
                        MaxX = 225,
                        MaxY = 181
                    }
                }
            };

            await telegram.SendAsync(camera, notification, NullLogger.Instance);

            Assert.That(httpClient.RequestBody, Does.Contain("Alerte caméra - Entree"));
            Assert.That(httpClient.RequestBody, Does.Contain("Heure: 23/04/2026 07:44"));
            Assert.That(httpClient.RequestBody, Does.Contain("Détection: Personne"));
        }

        [Test]
        public async Task SendAsync_UsesImageEndpointWhenPhotoBaseUrlIsConfigured()
        {
            Configure("action-token", imageAccessToken: "image-token");

            FakeHttpClient httpClient = new();

            Telegram telegram = new(httpClient)
            {
                ChatID = "1",
                Token = "token",
                PhotoBaseURL = "https://synoai.local"
            };

            Notification notification = new()
            {
                CreatedAt = new DateTime(2026, 4, 23, 7, 44, 12),
                ProcessedImage = new ProcessedImage(Path.Combine(_workspace, "capture.jpeg")),
                ValidPredictions = new List<AIPrediction>
                {
                    new()
                    {
                        Label = "person",
                        Confidence = 87
                    }
                }
            };

            await telegram.SendAsync(new Camera { Name = "Entree" }, notification, NullLogger.Instance);

            Assert.That(httpClient.RequestBody, Does.Contain("https://synoai.local/Image/Entree/capture.jpeg?token=image-token"));
        }

        [Test]
        public async Task SendAsync_RetriesTransientTelegramStatus()
        {
            Configure(httpRetryCount: 1, httpRetryDelayMs: 0);

            string imagePath = Path.Combine(_workspace, "capture.jpeg");
            File.WriteAllBytes(imagePath, new byte[] { 1, 2, 3 });

            FakeHttpClient httpClient = new(
                new HttpResponseMessage((HttpStatusCode)429)
                {
                    Content = new StringContent(@"{""ok"":false,""description"":""Too Many Requests: retry after 1""}")
                },
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"{""ok"":true}")
                });

            Telegram telegram = new(httpClient)
            {
                ChatID = "1",
                Token = "token"
            };

            await telegram.SendAsync(
                new Camera { Name = "Entree" },
                new Notification
                {
                    ProcessedImage = new ProcessedImage(imagePath),
                    ValidPredictions = new[] { new AIPrediction { Label = "person", Confidence = 90 } }
                },
                NullLogger.Instance);

            Assert.That(httpClient.RequestCount, Is.EqualTo(2));
        }

        [Test]
        public void SendAsync_ThrowsWhenPhotoCannotBeSent()
        {
            string imagePath = Path.Combine(_workspace, "capture.jpeg");
            File.WriteAllBytes(imagePath, new byte[] { 1, 2, 3 });

            FakeHttpClient httpClient = new(
                new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(@"{""ok"":false,""description"":""bad request""}")
                });

            Telegram telegram = new(httpClient)
            {
                ChatID = "1",
                Token = "token"
            };

            Assert.That(async () => await telegram.SendAsync(
                new Camera { Name = "Entree" },
                new Notification
                {
                    ProcessedImage = new ProcessedImage(imagePath),
                    ValidPredictions = new[] { new AIPrediction { Label = "person", Confidence = 90 } }
                },
                NullLogger.Instance), Throws.InstanceOf<HttpRequestException>());
        }

        [Test]
        public async Task SendRecordingClipAsync_PostsVideoToTelegram()
        {
            string clipPath = Path.Combine(_workspace, "clip.mp4");
            File.WriteAllBytes(clipPath, new byte[] { 1, 2, 3, 4 });

            FakeHttpClient httpClient = new();

            Telegram telegram = new(httpClient)
            {
                ChatID = "1",
                Token = "token",
                SendRecordingClip = true
            };

            await telegram.SendRecordingClipAsync(
                new Camera { Name = "Entree" },
                new Notification { RecordingClip = new ProcessedFile(clipPath) },
                NullLogger.Instance);

            Assert.That(httpClient.RequestUri.AbsolutePath, Is.EqualTo("/bottoken/sendVideo"));
            Assert.That(httpClient.RequestBody, Does.Contain("clip.mp4"));
            Assert.That(httpClient.RequestBody, Does.Contain("video"));
        }

        [Test]
        public void Factory_DefaultsRecordingClipDurationToSixtySeconds()
        {
            Telegram telegram = CreateTelegramFromFactory(new Dictionary<string, string>
            {
                ["Notifiers:0:ChatID"] = "1",
                ["Notifiers:0:Token"] = "token"
            });

            Assert.That(telegram.RecordingClipDurationMs, Is.EqualTo(60000));
        }

        [Test]
        public void Factory_DefaultsRecordingClipDownloadDelayToThirtySeconds()
        {
            Telegram telegram = CreateTelegramFromFactory(new Dictionary<string, string>
            {
                ["Notifiers:0:ChatID"] = "1",
                ["Notifiers:0:Token"] = "token"
            });

            Assert.That(telegram.RecordingClipDownloadDelayMs, Is.EqualTo(30000));
        }

        [Test]
        public void Factory_AllowsRecordingClipDownloadDelayToBeDisabled()
        {
            Telegram telegram = CreateTelegramFromFactory(new Dictionary<string, string>
            {
                ["Notifiers:0:ChatID"] = "1",
                ["Notifiers:0:Token"] = "token",
                ["Notifiers:0:RecordingClipDownloadDelayMs"] = "0"
            });

            Assert.That(telegram.RecordingClipDownloadDelayMs, Is.EqualTo(0));
        }

        [Test]
        public void Factory_AllowsRecordingClipDurationUpToTwoMinutes()
        {
            Telegram telegram = CreateTelegramFromFactory(new Dictionary<string, string>
            {
                ["Notifiers:0:ChatID"] = "1",
                ["Notifiers:0:Token"] = "token",
                ["Notifiers:0:RecordingClipDurationMs"] = "120000"
            });

            Assert.That(telegram.RecordingClipDurationMs, Is.EqualTo(120000));
        }

        [Test]
        public void Factory_CapsRecordingClipDurationAtTwoMinutes()
        {
            Telegram telegram = CreateTelegramFromFactory(new Dictionary<string, string>
            {
                ["Notifiers:0:ChatID"] = "1",
                ["Notifiers:0:Token"] = "token",
                ["Notifiers:0:RecordingClipDurationMs"] = "180000"
            });

            Assert.That(telegram.RecordingClipDurationMs, Is.EqualTo(120000));
        }

        private static void Configure(string accessToken = null, string imageAccessToken = null, int? httpRetryCount = null, int? httpRetryDelayMs = null)
        {
            Dictionary<string, string> values = new()
            {
                ["AI:Url"] = "http://codeproject-ai:32168",
                ["AccessToken"] = accessToken,
                ["ImageAccessToken"] = imageAccessToken
            };

            if (httpRetryCount.HasValue)
            {
                values["HttpRetryCount"] = httpRetryCount.Value.ToString();
            }

            if (httpRetryDelayMs.HasValue)
            {
                values["HttpRetryDelayMs"] = httpRetryDelayMs.Value.ToString();
            }

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .Build();

            Config.Generate(NullLogger.Instance, configuration);
        }

        private static Telegram CreateTelegramFromFactory(Dictionary<string, string> values)
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .Build();

            return (Telegram)new TelegramFactory().Create(NullLogger.Instance, configuration.GetSection("Notifiers:0"));
        }

        private sealed class FakeHttpClient : IHttpClient
        {
            private readonly Queue<HttpResponseMessage> _responses;

            public FakeHttpClient(params HttpResponseMessage[] responses)
            {
                _responses = new Queue<HttpResponseMessage>(responses);
            }

            public TimeSpan Timeout { get; set; }
            public Uri RequestUri { get; private set; }
            public string RequestBody { get; private set; }
            public int RequestCount { get; private set; }

            public Task<HttpResponseMessage> PostAsync(string requestUri, HttpContent content)
            {
                return PostAsync(new Uri(requestUri), content);
            }

            public async Task<HttpResponseMessage> PostAsync(Uri requestUri, HttpContent content)
            {
                return await PostAsync(requestUri, content, CancellationToken.None);
            }

            public async Task<HttpResponseMessage> PostAsync(Uri requestUri, HttpContent content, CancellationToken cancellationToken)
            {
                RequestCount++;
                RequestUri = requestUri;
                RequestBody = await content.ReadAsStringAsync();

                return _responses.Count > 0
                    ? _responses.Dequeue()
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(@"{""ok"":true}")
                    };
            }
        }
    }
}
