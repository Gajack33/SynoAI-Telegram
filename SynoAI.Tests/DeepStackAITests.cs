using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using SynoAI.AIs.DeepStack;
using SynoAI.App;
using SynoAI.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SynoAI.Tests
{
    public class DeepStackAITests
    {
        private CultureInfo _previousCulture;

        [SetUp]
        public void Setup()
        {
            _previousCulture = Thread.CurrentThread.CurrentCulture;
            ConfigureCodeProjectAI();
        }

        [TearDown]
        public void TearDown()
        {
            Thread.CurrentThread.CurrentCulture = _previousCulture;
        }

        [Test]
        public async Task Process_NormalisesCodeProjectPercentConfidence()
        {
            FakeHttpClient httpClient = new(
                @"{""success"":true,""predictions"":[{""label"":""person"",""confidence"":95,""x_min"":10,""y_min"":20,""x_max"":110,""y_max"":220}],""inferenceMs"":42,""processMs"":50}");

            IEnumerable<AIPrediction> predictions = await new DeepStackAI(httpClient).Process(CreateLogger(), CreateCamera(50), new byte[] { 1, 2, 3 });

            AIPrediction prediction = predictions.Single();
            Assert.That(prediction.Label, Is.EqualTo("person"));
            Assert.That(prediction.Confidence, Is.EqualTo(95));
            Assert.That(prediction.SizeX, Is.EqualTo(100));
            Assert.That(prediction.SizeY, Is.EqualTo(200));
        }

        [Test]
        public async Task Process_FormatsMinConfidenceWithInvariantCulture()
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("fr-FR");
            FakeHttpClient httpClient = new(@"{""success"":true,""predictions"":[]}");

            await new DeepStackAI(httpClient).Process(CreateLogger(), CreateCamera(50), new byte[] { 1, 2, 3 });

            Assert.That(httpClient.RequestBody, Does.Contain("0.5"));
            Assert.That(httpClient.RequestBody, Does.Not.Contain("0,5"));
        }

        [Test]
        public async Task Process_RetriesTransientHttpStatus()
        {
            ConfigureCodeProjectAI(new Dictionary<string, string>
            {
                ["HttpRetryCount"] = "1",
                ["HttpRetryDelayMs"] = "0"
            });

            FakeHttpClient httpClient = new(
                new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("warming")
                },
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(@"{""success"":true,""predictions"":[{""label"":""person"",""confidence"":95,""x_min"":10,""y_min"":20,""x_max"":110,""y_max"":220}]}")
                });

            AIPrediction prediction = (await new DeepStackAI(httpClient).Process(CreateLogger(), CreateCamera(50), new byte[] { 1, 2, 3 })).Single();

            Assert.That(prediction.Label, Is.EqualTo("person"));
            Assert.That(httpClient.RequestCount, Is.EqualTo(2));
        }

        [Test]
        public async Task Process_ReturnsNullWhenAIResponseExceedsLimit()
        {
            ConfigureCodeProjectAI(new Dictionary<string, string>
            {
                ["MaxAIResponseBytes"] = "4"
            });
            FakeHttpClient httpClient = new(@"{""success"":true}");

            IEnumerable<AIPrediction> predictions = await new DeepStackAI(httpClient).Process(CreateLogger(), CreateCamera(50), new byte[] { 1, 2, 3 });

            Assert.That(predictions, Is.Null);
        }

        [Test]
        public async Task Process_UsesFaceRecognitionPathAndMapsUserId()
        {
            ConfigureCodeProjectAI(new Dictionary<string, string>
            {
                ["AI:DetectionMode"] = "FaceRecognition",
                ["AI:FaceRecognitionPath"] = "v1/vision/face/recognize",
                ["AI:FaceLabels:pierre-id"] = "Pierre"
            });
            FakeHttpClient httpClient = new(
                @"{""success"":true,""predictions"":[{""userid"":""pierre-id"",""confidence"":0.96,""x_min"":10,""y_min"":20,""x_max"":110,""y_max"":220}]}");

            AIPrediction prediction = (await new DeepStackAI(httpClient).Process(CreateLogger(), CreateCamera(50), new byte[] { 1, 2, 3 })).Single();

            Assert.That(httpClient.RequestUri.AbsolutePath, Is.EqualTo("/v1/vision/face/recognize"));
            Assert.That(prediction.Label, Is.EqualTo("Pierre"));
            Assert.That(prediction.Confidence, Is.EqualTo(96));
        }

        [Test]
        public async Task Process_StopsReadingOversizedStreamingResponseBeforeBufferingItAll()
        {
            ConfigureCodeProjectAI(new Dictionary<string, string> { ["MaxAIResponseBytes"] = "1024" });
            using ControlledReadStream body = new(1024 * 1024);
            using TestHttpHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(body)
            });
            HttpClientWrapper client = new(new TestHttpClientFactory(handler));

            var result = await new DeepStackAI(client).Process(CreateLogger(), CreateCamera(50), new byte[] { 1 });

            Assert.That(result, Is.Null);
            Assert.That(body.BytesRead, Is.LessThanOrEqualTo(81920));
            Assert.That(body.BytesRead, Is.GreaterThan(1024));
        }

        [Test]
        public async Task Process_TimesOutWhileReadingResponseBody()
        {
            ConfigureCodeProjectAI(new Dictionary<string, string>
            {
                ["AI:TimeoutSeconds"] = "1", ["HttpRetryCount"] = "0"
            });
            using ControlledReadStream body = new(stallAtEnd: true);
            using TestHttpHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
            HttpClientWrapper client = new(new TestHttpClientFactory(handler));
            Task<IEnumerable<AIPrediction>> request = new DeepStackAI(client).Process(CreateLogger(), CreateCamera(50), new byte[] { 1 });
            try
            {
                Assert.That(await request.WaitAsync(TimeSpan.FromSeconds(3)), Is.Null);
                Assert.That(body.WasCancelled, Is.True);
            }
            finally { body.Release.TrySetResult(0); }
        }

        [Test]
        public async Task Process_CallerCancellationStopsBodyReadWithoutRetrying()
        {
            ConfigureCodeProjectAI(new Dictionary<string, string> { ["HttpRetryCount"] = "3" });
            using ControlledReadStream body = new(stallAtEnd: true);
            int calls = 0;
            using TestHttpHandler handler = new(_ =>
            {
                calls++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
            });
            HttpClientWrapper client = new(new TestHttpClientFactory(handler));
            using CancellationTokenSource stop = new();
            var request = new DeepStackAI(client).Process(CreateLogger(), CreateCamera(50), new byte[] { 1 }, stop.Token);
            try
            {
                await body.Reading.Task.WaitAsync(TimeSpan.FromSeconds(3));
                stop.Cancel();
                Assert.That(async () => await request, Throws.InstanceOf<OperationCanceledException>());
                Assert.That(calls, Is.EqualTo(1));
            }
            finally { body.Release.TrySetResult(0); }
        }

        private static Camera CreateCamera(decimal threshold)
        {
            return new Camera
            {
                Name = "Entree",
                Threshold = threshold
            };
        }

        private static ILogger CreateLogger()
        {
            return NullLogger.Instance;
        }

        private static void ConfigureCodeProjectAI(Dictionary<string, string> overrides = null)
        {
            Dictionary<string, string> values = new()
            {
                ["AI:Type"] = "CodeProjectAIServer",
                ["AI:Url"] = "http://codeproject-ai:32168"
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

            using ILoggerFactory loggerFactory = LoggerFactory.Create(builder => { });
            Config.Generate(loggerFactory.CreateLogger("test"), configuration);
        }

        private sealed class FakeHttpClient : IHttpClient
        {
            private readonly Queue<HttpResponseMessage> _responses;

            public FakeHttpClient(string responseContent)
                : this(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseContent)
                })
            {
            }

            public FakeHttpClient(params HttpResponseMessage[] responses)
            {
                _responses = new Queue<HttpResponseMessage>(responses);
            }

            public TimeSpan Timeout { get; set; }
            public string RequestBody { get; private set; }
            public Uri RequestUri { get; private set; }
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
                        Content = new StringContent(@"{""success"":true,""predictions"":[]}")
                    };
            }
        }
    }
}
