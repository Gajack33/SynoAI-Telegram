using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using SkiaSharp;
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
    public class AIServiceTests
    {
        private IHttpClient _previousHttpClient;

        [SetUp]
        public void Setup()
        {
            _previousHttpClient = Shared.HttpClient;
        }

        [TearDown]
        public void TearDown()
        {
            Shared.HttpClient = _previousHttpClient;
        }

        [Test]
        public async Task ProcessAsync_ScalesResizedDetectionsBackToOriginalCoordinates()
        {
            Configure(new Dictionary<string, string>
            {
                ["AI:Type"] = "CodeProjectAIServer",
                ["AI:Url"] = "http://codeproject-ai:32168",
                ["AI:MaxImageWidth"] = "1000",
                ["AI:JpegQuality"] = "90"
            });

            Shared.HttpClient = new FakeHttpClient(
                @"{""success"":true,""predictions"":[{""label"":""person"",""confidence"":90,""x_min"":10,""y_min"":15,""x_max"":110,""y_max"":215}]}");

            byte[] image = CreateJpeg(2000, 1000);
            AIService service = new(NullLogger<AIService>.Instance);

            AIPrediction prediction = (await service.ProcessAsync(
                new Camera { Name = "Entree", Threshold = 50 },
                image)).Single();

            Assert.That(prediction.MinX, Is.EqualTo(20));
            Assert.That(prediction.MinY, Is.EqualTo(30));
            Assert.That(prediction.MaxX, Is.EqualTo(220));
            Assert.That(prediction.MaxY, Is.EqualTo(430));
        }

        private static void Configure(Dictionary<string, string> values)
        {
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

        private sealed class FakeHttpClient : IHttpClient
        {
            private readonly string _responseContent;

            public FakeHttpClient(string responseContent)
            {
                _responseContent = responseContent;
            }

            public TimeSpan Timeout { get; set; }

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
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_responseContent)
                });
            }
        }
    }
}
