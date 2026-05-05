using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using SkiaSharp;
using SynoAI.Models;
using SynoAI.Services;
using System;
using System.Collections.Generic;
using System.IO;

namespace SynoAI.Tests
{
    public class SnapshotManagerTests
    {
        private string _previousDirectory;
        private string _workspace;

        [SetUp]
        public void Setup()
        {
            _previousDirectory = Environment.CurrentDirectory;
            _workspace = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_workspace);
            Environment.CurrentDirectory = _workspace;

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["AI:Url"] = "http://codeproject-ai:32168",
                    ["OutputJpegQuality"] = "90"
                })
                .Build();

            Config.Generate(NullLogger.Instance, configuration);
        }

        [TearDown]
        public void TearDown()
        {
            Environment.CurrentDirectory = _previousDirectory;
            if (Directory.Exists(_workspace))
            {
                Directory.Delete(_workspace, recursive: true);
            }
        }

        [Test]
        public void DressImage_ReturnsNullForInvalidImageBytes()
        {
            ProcessedImage processedImage = SnapshotManager.DressImage(
                new Camera { Name = "Entree" },
                new byte[] { 1, 2, 3 },
                Array.Empty<AIPrediction>(),
                Array.Empty<AIPrediction>(),
                NullLogger.Instance);

            Assert.That(processedImage, Is.Null);
        }

        [Test]
        public void DressImage_ReturnsNullForOversizedSnapshot()
        {
            Configure(new Dictionary<string, string>
            {
                ["MaxSnapshotBytes"] = "2"
            });

            ProcessedImage processedImage = SnapshotManager.DressImage(
                new Camera { Name = "Entree" },
                new byte[] { 1, 2, 3 },
                Array.Empty<AIPrediction>(),
                Array.Empty<AIPrediction>(),
                NullLogger.Instance);

            Assert.That(processedImage, Is.Null);
        }

        [Test]
        public void SaveOriginalImage_DoesNotWriteOversizedSnapshot()
        {
            Configure(new Dictionary<string, string>
            {
                ["MaxSnapshotBytes"] = "2"
            });

            string filePath = SnapshotManager.SaveOriginalImage(
                NullLogger.Instance,
                new Camera { Name = "Entree" },
                new byte[] { 1, 2, 3 });

            Assert.That(filePath, Is.Null);
            Assert.That(Directory.Exists(Constants.DIRECTORY_CAPTURES), Is.False);
        }

        [Test]
        public void DressImage_SavesCaptureInSafeCameraDirectory()
        {
            ProcessedImage processedImage = SnapshotManager.DressImage(
                new Camera { Name = "Door:1" },
                CreateJpeg(320, 240),
                new[]
                {
                    new AIPrediction
                    {
                        Label = "person",
                        Confidence = 90,
                        MinX = 10,
                        MinY = 10,
                        MaxX = 80,
                        MaxY = 160
                    }
                },
                new[]
                {
                    new AIPrediction
                    {
                        Label = "person",
                        Confidence = 90,
                        MinX = 10,
                        MinY = 10,
                        MaxX = 80,
                        MaxY = 160
                    }
                },
                NullLogger.Instance);

            Assert.That(processedImage, Is.Not.Null);
            Assert.That(processedImage.FilePath, Does.Contain(Path.Combine("Captures", "Door_1")));
            Assert.That(File.Exists(processedImage.FilePath), Is.True);
        }

        [Test]
        public void DressImage_ClampsPredictionCoordinatesToImageBounds()
        {
            ProcessedImage processedImage = SnapshotManager.DressImage(
                new Camera { Name = "Entree" },
                CreateJpeg(100, 80),
                new[]
                {
                    new AIPrediction { Label = "person", Confidence = 90, MinX = -20, MinY = -10, MaxX = 130, MaxY = 90 }
                },
                new[]
                {
                    new AIPrediction { Label = "person", Confidence = 90, MinX = -20, MinY = -10, MaxX = 130, MaxY = 90 }
                },
                NullLogger.Instance);

            Assert.That(processedImage, Is.Not.Null);
            Assert.That(File.Exists(processedImage.FilePath), Is.True);
        }

        [Test]
        public void DressImage_ReturnsNullWhenValidPredictionCoordinatesAreInvalid()
        {
            ProcessedImage processedImage = SnapshotManager.DressImage(
                new Camera { Name = "Entree" },
                CreateJpeg(100, 80),
                new[]
                {
                    new AIPrediction { Label = "person", Confidence = 90, MinX = 90, MinY = 10, MaxX = 10, MaxY = 40 }
                },
                new[]
                {
                    new AIPrediction { Label = "person", Confidence = 90, MinX = 90, MinY = 10, MaxX = 10, MaxY = 40 }
                },
                NullLogger.Instance);

            Assert.That(processedImage, Is.Null);
        }

        [Test]
        public void DressImage_UsesConfiguredCapturePathPattern()
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["AI:Url"] = "http://codeproject-ai:32168",
                    ["CapturePathPattern"] = "{camera}/{yyyy}/{MM}/{dd}"
                })
                .Build();
            Config.Generate(NullLogger.Instance, configuration);

            ProcessedImage processedImage = SnapshotManager.DressImage(
                new Camera { Name = "Door:1" },
                CreateJpeg(320, 240),
                new[]
                {
                    new AIPrediction { Label = "person", Confidence = 90, MinX = 10, MinY = 10, MaxX = 80, MaxY = 160 }
                },
                new[]
                {
                    new AIPrediction { Label = "person", Confidence = 90, MinX = 10, MinY = 10, MaxX = 80, MaxY = 160 }
                },
                NullLogger.Instance);

            string expectedDatePath = Path.Combine(DateTime.Now.ToString("yyyy"), DateTime.Now.ToString("MM"), DateTime.Now.ToString("dd"));
            Assert.That(processedImage.FilePath, Does.Contain(Path.Combine("Captures", "Door_1", expectedDatePath)));
            Assert.That(processedImage.RelativePath, Does.Contain(expectedDatePath));
            Assert.That(File.Exists(processedImage.FilePath), Is.True);
        }

        private static void Configure(Dictionary<string, string> overrides)
        {
            Dictionary<string, string> values = new()
            {
                ["AI:Url"] = "http://codeproject-ai:32168",
                ["OutputJpegQuality"] = "90"
            };

            foreach (KeyValuePair<string, string> pair in overrides)
            {
                values[pair.Key] = pair.Value;
            }

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
            using SKData data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
            return data.ToArray();
        }
    }
}
