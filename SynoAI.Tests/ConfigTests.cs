using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System.Collections.Generic;

namespace SynoAI.Tests
{
    public class ConfigTests
    {
        [Test]
        public void Generate_ClampsOutputJpegQuality()
        {
            GenerateConfig(new Dictionary<string, string>
            {
                ["OutputJpegQuality"] = "150",
                ["AI:Url"] = "http://codeproject-ai:32168"
            });

            Assert.That(Config.OutputJpegQuality, Is.EqualTo(100));
        }

        [Test]
        public void Generate_DefaultsOutputJpegQualityToBalancedQuality()
        {
            GenerateConfig(new Dictionary<string, string>
            {
                ["AI:Url"] = "http://codeproject-ai:32168"
            });

            Assert.That(Config.OutputJpegQuality, Is.EqualTo(90));
        }

        [Test]
        public void Generate_EnablesWarmupByDefaultForCodeProjectAI()
        {
            GenerateConfig(new Dictionary<string, string>
            {
                ["AI:Type"] = "CodeProjectAIServer",
                ["AI:Url"] = "http://codeproject-ai:32168"
            });

            Assert.That(Config.AIWarmupEnabled, Is.True);
            Assert.That(Config.AIWarmupRetries, Is.EqualTo(5));
            Assert.That(Config.AIWarmupDelayMs, Is.EqualTo(5000));
        }

        [Test]
        public void Generate_ReadsCodeProjectPerformanceOptions()
        {
            GenerateConfig(new Dictionary<string, string>
            {
                ["HttpTimeoutSeconds"] = "30",
                ["HttpRetryCount"] = "3",
                ["HttpRetryDelayMs"] = "250",
                ["SynologyTimeoutSeconds"] = "20",
                ["TelegramTimeoutSeconds"] = "120",
                ["MaxAIResponseBytes"] = "2048",
                ["MaxRecordingClipBytes"] = "4096",
                ["AI:Type"] = "CodeProjectAIServer",
                ["AI:Url"] = "http://codeproject-ai:32168",
                ["AI:Path"] = "v1/vision/custom/ipcam-general",
                ["AI:DetectionMode"] = "FaceRecognition",
                ["AI:FaceRecognitionPath"] = "v1/vision/face/recognize",
                ["AI:FaceLabels:pierre-id"] = "Pierre",
                ["AI:TimeoutSeconds"] = "15",
                ["AI:FailureDelayMs"] = "45000",
                ["AI:MaxImageWidth"] = "1280",
                ["AI:JpegQuality"] = "82",
                ["PerfectShotEnabled"] = "true",
                ["CapturePathPattern"] = "{camera}/{yyyy}/{MM}/{dd}",
                ["DuplicateSnapshotIgnoreSeconds"] = "60",
                ["StationaryObjectIgnoreSeconds"] = "300",
                ["StationaryObjectMovementThresholdPixels"] = "12",
                ["MaxSnapshotBytes"] = "1024",
                ["MaxAIResponseBytes"] = "2048",
                ["MaxRecordingClipBytes"] = "4096"
            });

            Assert.That(Config.AIPath, Is.EqualTo("v1/vision/custom/ipcam-general"));
            Assert.That(Config.AIDetectionMode, Is.EqualTo(SynoAI.AIs.AIDetectionMode.FaceRecognition));
            Assert.That(Config.AIFaceRecognitionPath, Is.EqualTo("v1/vision/face/recognize"));
            Assert.That(Config.MapFaceLabel("pierre-id"), Is.EqualTo("Pierre"));
            Assert.That(Config.AITimeoutSeconds, Is.EqualTo(15));
            Assert.That(Config.AIFailureDelayMs, Is.EqualTo(45000));
            Assert.That(Config.AIMaxImageWidth, Is.EqualTo(1280));
            Assert.That(Config.AIJpegQuality, Is.EqualTo(82));
            Assert.That(Config.HttpRetryCount, Is.EqualTo(3));
            Assert.That(Config.HttpRetryDelayMs, Is.EqualTo(250));
            Assert.That(Config.SynologyTimeoutSeconds, Is.EqualTo(20));
            Assert.That(Config.TelegramTimeoutSeconds, Is.EqualTo(120));
            Assert.That(Config.MaxAIResponseBytes, Is.EqualTo(2048));
            Assert.That(Config.MaxRecordingClipBytes, Is.EqualTo(4096));
            Assert.That(Config.PerfectShotEnabled, Is.True);
            Assert.That(Config.CapturePathPattern, Is.EqualTo("{camera}/{yyyy}/{MM}/{dd}"));
            Assert.That(Config.DuplicateSnapshotIgnoreSeconds, Is.EqualTo(60));
            Assert.That(Config.StationaryObjectIgnoreSeconds, Is.EqualTo(300));
            Assert.That(Config.StationaryObjectMovementThresholdPixels, Is.EqualTo(12));
            Assert.That(Config.MaxSnapshotBytes, Is.EqualTo(1024));
            Assert.That(Config.MaxAIResponseBytes, Is.EqualTo(2048));
            Assert.That(Config.MaxRecordingClipBytes, Is.EqualTo(4096));
        }

        [Test]
        public void ValidateStartupConfiguration_ReturnsNoErrorsForCompleteConfig()
        {
            GenerateConfig(CreateCompleteConfig());

            Assert.That(Config.ValidateStartupConfiguration(), Is.Empty);
        }

        [Test]
        public void ValidateStartupConfiguration_ReturnsActionableErrorsForMissingRequiredValues()
        {
            GenerateConfig(new Dictionary<string, string>
            {
                ["AI:Type"] = "CodeProjectAIServer",
                ["Cameras:0:Name"] = "Entree",
                ["Cameras:0:Threshold"] = "50",
                ["Notifiers:0:Type"] = "Telegram"
            });

            IReadOnlyList<string> errors = Config.ValidateStartupConfiguration();

            Assert.That(errors, Does.Contain("Url must be an absolute http or https URL."));
            Assert.That(errors, Does.Contain("User is required."));
            Assert.That(errors, Does.Contain("Password is required."));
            Assert.That(errors, Does.Contain("AccessToken is required."));
            Assert.That(errors, Does.Contain("AI:Url must be an absolute http or https URL."));
            Assert.That(errors, Does.Contain("Telegram ChatID is required."));
            Assert.That(errors, Does.Contain("Telegram Token is required."));
        }

        [Test]
        public void ValidateStartupConfiguration_RejectsAbsoluteAIPaths()
        {
            Dictionary<string, string> values = CreateCompleteConfig();
            values["AI:Path"] = "http://attacker.local/collect";
            GenerateConfig(values);

            IReadOnlyList<string> errors = Config.ValidateStartupConfiguration();

            Assert.That(errors, Does.Contain("AI:Path must be a relative path."));
        }

        private static void GenerateConfig(Dictionary<string, string> values)
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .Build();

            using ILoggerFactory loggerFactory = LoggerFactory.Create(builder => { });
            Config.Generate(loggerFactory.CreateLogger("test"), configuration);
        }

        private static Dictionary<string, string> CreateCompleteConfig()
        {
            return new Dictionary<string, string>
            {
                ["Url"] = "http://nas.local:5000",
                ["User"] = "synoai",
                ["Password"] = "password",
                ["AccessToken"] = "secret-token",
                ["AI:Type"] = "CodeProjectAIServer",
                ["AI:Url"] = "http://codeproject-ai:32168",
                ["AI:Path"] = "v1/vision/custom/ipcam-general",
                ["Cameras:0:Name"] = "Entree",
                ["Cameras:0:Types:0"] = "Person",
                ["Cameras:0:Threshold"] = "50",
                ["Cameras:0:Wait"] = "1000",
                ["Notifiers:0:Type"] = "Telegram",
                ["Notifiers:0:ChatID"] = "1",
                ["Notifiers:0:Token"] = "token"
            };
        }
    }
}
