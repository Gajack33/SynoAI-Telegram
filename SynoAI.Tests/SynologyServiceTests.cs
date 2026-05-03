using NUnit.Framework;
using SynoAI.Models;
using SynoAI.Services;
using System;

namespace SynoAI.Tests
{
    public class SynologyServiceTests
    {
        [Test]
        public void BuildSnapshotResource_UsesUnquotedApiNameAndConfiguredQuality()
        {
            string resource = SynologyService.BuildSnapshotResource("entry.cgi", 9, 42, CameraQuality.Balanced);

            Assert.That(resource, Is.EqualTo("webapi/entry.cgi?version=9&id=42&api=SYNO.SurveillanceStation.Camera&method=GetSnapshot&profileType=1"));
            Assert.That(resource, Does.Not.Contain("\"SYNO.SurveillanceStation.Camera\""));
        }

        [Test]
        public void BuildRecordingListResource_SearchesAroundDetectionTime()
        {
            DateTimeOffset detectedAt = DateTimeOffset.FromUnixTimeSeconds(1_714_821_600);

            string resource = SynologyService.BuildRecordingListResource("entry.cgi", 42, detectedAt);

            Assert.That(resource, Is.EqualTo("webapi/entry.cgi?api=SYNO.SurveillanceStation.Recording&method=List&version=6&cameraIds=42&offset=0&limit=20&fromTime=1714820700&toTime=0"));
        }

        [Test]
        public void SelectRecordingForDetection_PrefersRecordingContainingDetection()
        {
            DateTimeOffset detectedAt = DateTimeOffset.FromUnixTimeSeconds(1_714_821_600);
            SynologyRecording selected = SynologyService.SelectRecordingForDetection(new[]
            {
                new SynologyRecording { Id = 1, StartTimeUnixSeconds = 1_714_821_900, EndTimeUnixSeconds = 1_714_822_200 },
                new SynologyRecording { Id = 2, StartTimeUnixSeconds = 1_714_821_500, EndTimeUnixSeconds = 1_714_821_800 },
                new SynologyRecording { Id = 3, StartTimeUnixSeconds = 1_714_821_000, EndTimeUnixSeconds = 1_714_821_300 }
            }, detectedAt);

            Assert.That(selected.Id, Is.EqualTo(2));
        }

        [Test]
        public void CalculateRecordingOffsetMs_AlignsOffsetToDetectionTime()
        {
            DateTimeOffset detectedAt = DateTimeOffset.FromUnixTimeSeconds(1_714_821_600);
            SynologyRecording recording = new()
            {
                StartTimeUnixSeconds = 1_714_821_540
            };

            int offsetMs = SynologyService.CalculateRecordingOffsetMs(recording, detectedAt, -5000);

            Assert.That(offsetMs, Is.EqualTo(55000));
        }

        [Test]
        public void GetRecordingStartTime_CanInferUnixTimeFromFilePath()
        {
            SynologyRecording recording = new()
            {
                FilePath = "20141030PM/TVIP2155220141030-224911-1414680551.avi"
            };

            DateTimeOffset? start = SynologyService.GetRecordingStartTime(recording);

            Assert.That(start, Is.EqualTo(DateTimeOffset.FromUnixTimeSeconds(1414680551)));
        }

        [Test]
        public void TryNormalizeApiPath_AllowsRelativeSynologyApiPath()
        {
            bool result = SynologyService.TryNormalizeApiPath("entry.cgi", out string normalizedPath);

            Assert.That(result, Is.True);
            Assert.That(normalizedPath, Is.EqualTo("entry.cgi"));
        }

        [TestCase("http://attacker.local/auth.cgi")]
        [TestCase("//attacker.local/auth.cgi")]
        [TestCase("../auth.cgi")]
        [TestCase("auth.cgi?api=evil")]
        public void TryNormalizeApiPath_RejectsUnsafeSynologyApiPath(string path)
        {
            bool result = SynologyService.TryNormalizeApiPath(path, out string normalizedPath);

            Assert.That(result, Is.False);
            Assert.That(normalizedPath, Is.Null);
        }
    }
}
