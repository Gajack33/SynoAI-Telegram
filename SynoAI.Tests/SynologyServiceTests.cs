using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using SynoAI;
using SynoAI.Models;
using SynoAI.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

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
        public void BuildRecentRecordingListResource_QueriesLatestRecordingsWithoutTimeFilter()
        {
            string resource = SynologyService.BuildRecentRecordingListResource("entry.cgi", 42);

            Assert.That(resource, Is.EqualTo("webapi/entry.cgi?api=SYNO.SurveillanceStation.Recording&method=List&version=6&cameraIds=42&offset=0&limit=20"));
        }

        [Test]
        public async Task DownloadLatestRecordingClipAsync_RetriesLatestAndNeverDownloadsWithSaturatedOffset()
        {
            Configure();
            RecordingHttpMessageHandler handler = new();
            using HttpClient httpClient = new(handler);
            SynologyService service = CreateInitializedService(httpClient);
            DateTimeOffset detectedAt = DateTimeOffset.FromUnixTimeSeconds(1_714_821_600);
            ProcessedFile clip = null;

            try
            {
                clip = await service.DownloadLatestRecordingClipAsync("Entree", detectedAt, -5000, 10000);

                Assert.That(clip, Is.Not.Null);
                Uri downloadRequest = handler.Requests.Single(x => x.Query.Contains("method=Download"));
                Assert.That(handler.Requests.Count(x => x.Query.Contains("method=List")), Is.EqualTo(2));
                Assert.That(downloadRequest.Query, Does.Contain("id=2"));
                Assert.That(downloadRequest.Query, Does.Contain("offsetTimeMs=55000"));
                Assert.That(downloadRequest.Query, Does.Contain("playTimeMs=10000"));
                Assert.That(downloadRequest.Query, Does.Not.Contain("offsetTimeMs=2147483647"));
            }
            finally
            {
                if (clip != null && File.Exists(clip.FilePath))
                {
                    File.Delete(clip.FilePath);
                }
            }
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
        public void ShouldRetryWithRecentRecordingList_WhenFilteredLookupIsEmpty()
        {
            DateTimeOffset detectedAt = DateTimeOffset.FromUnixTimeSeconds(1_714_821_600);

            bool retry = SynologyService.ShouldRetryWithRecentRecordingList(Array.Empty<SynologyRecording>(), null, detectedAt);

            Assert.That(retry, Is.True);
        }

        [Test]
        public void ShouldRetryWithRecentRecordingList_WhenSelectedRecordingStartsAfterDetection()
        {
            DateTimeOffset detectedAt = DateTimeOffset.FromUnixTimeSeconds(1_714_821_600);
            SynologyRecording selected = new()
            {
                Id = 1,
                StartTimeUnixSeconds = 1_714_821_900,
                EndTimeUnixSeconds = 1_714_822_200
            };

            bool retry = SynologyService.ShouldRetryWithRecentRecordingList(new[] { selected }, selected, detectedAt);

            Assert.That(retry, Is.True);
        }

        [Test]
        public void ShouldRetryWithRecentRecordingList_WhenSelectedRecordingEndedBeforeDetection()
        {
            DateTimeOffset detectedAt = DateTimeOffset.FromUnixTimeSeconds(1_714_821_600);
            SynologyRecording selected = new()
            {
                Id = 1,
                StartTimeUnixSeconds = 1_714_821_000,
                EndTimeUnixSeconds = 1_714_821_300
            };

            bool retry = SynologyService.ShouldRetryWithRecentRecordingList(new[] { selected }, selected, detectedAt);

            Assert.That(retry, Is.True);
        }

        [Test]
        public void ShouldRetryWithRecentRecordingList_DoesNotRetryWhenSelectionContainsDetection()
        {
            DateTimeOffset detectedAt = DateTimeOffset.FromUnixTimeSeconds(1_714_821_600);
            SynologyRecording selected = new()
            {
                Id = 1,
                StartTimeUnixSeconds = 1_714_821_500,
                EndTimeUnixSeconds = 1_714_821_800
            };

            bool retry = SynologyService.ShouldRetryWithRecentRecordingList(new[] { selected }, selected, detectedAt);

            Assert.That(retry, Is.False);
        }

        [Test]
        public void ShouldRetryWithRecentRecordingList_WhenSelectedRecordingHasNoUsableTime()
        {
            DateTimeOffset detectedAt = DateTimeOffset.FromUnixTimeSeconds(1_714_821_600);
            SynologyRecording selected = new()
            {
                Id = 1,
                StartTimeUnixSeconds = 7
            };

            bool retry = SynologyService.ShouldRetryWithRecentRecordingList(new[] { selected }, selected, detectedAt);

            Assert.That(retry, Is.True);
        }

        [Test]
        public void SelectRecordingForDetection_DoesNotSelectEpochSentinelAsLatestBeforeDetection()
        {
            DateTimeOffset detectedAt = DateTimeOffset.FromUnixTimeSeconds(1_714_821_600);

            SynologyRecording selected = SynologyService.SelectRecordingForDetection(new[]
            {
                new SynologyRecording { Id = 1, StartTimeUnixSeconds = 7 },
                new SynologyRecording { Id = 2, StartTimeUnixSeconds = 1_714_821_540 }
            }, detectedAt);

            Assert.That(selected.Id, Is.EqualTo(2));
        }

        [Test]
        public void SelectRecordingForDetection_UsesFilePathTimestampWhenStartTimeIsImplausible()
        {
            DateTimeOffset detectedAt = DateTimeOffset.FromUnixTimeSeconds(1_714_821_600);

            SynologyRecording selected = SynologyService.SelectRecordingForDetection(new[]
            {
                new SynologyRecording { Id = 1, StartTimeUnixSeconds = 1_714_821_500 },
                new SynologyRecording
                {
                    Id = 2,
                    StartTimeUnixSeconds = 7,
                    FilePath = "20240504PM/Entree20240504-103900-1714821540.mp4"
                }
            }, detectedAt);

            Assert.That(selected.Id, Is.EqualTo(2));
        }

        [Test]
        public void SelectRecordingForDetection_UsesLocalFilePathTimestampWhenUnixSuffixIsMissing()
        {
            DateTimeOffset detectedAt = new(2026, 5, 6, 9, 2, 45, TimeSpan.FromHours(2));

            SynologyRecording selected = SynologyService.SelectRecordingForDetection(new[]
            {
                new SynologyRecording
                {
                    Id = 1,
                    FilePath = "20260506AM/Entree20260506-090200.mp4"
                },
                new SynologyRecording
                {
                    Id = 2,
                    FilePath = "20260506AM/Entree20260506-085900.mp4"
                }
            }, detectedAt);

            Assert.That(selected.Id, Is.EqualTo(1));
            Assert.That(SynologyService.ShouldRetryWithRecentRecordingList(new[] { selected }, selected, detectedAt), Is.False);
        }

        [Test]
        public void SelectRecordingForDetection_UsesStopTimeWhenEndTimeIsImplausible()
        {
            DateTimeOffset detectedAt = DateTimeOffset.FromUnixTimeSeconds(1_714_821_600);

            SynologyRecording selected = SynologyService.SelectRecordingForDetection(new[]
            {
                new SynologyRecording
                {
                    Id = 1,
                    StartTimeUnixSeconds = 1_714_821_500,
                    EndTimeUnixSeconds = 7,
                    StopTimeUnixSeconds = 1_714_821_800
                },
                new SynologyRecording { Id = 2, StartTimeUnixSeconds = 1_714_821_590 }
            }, detectedAt);

            Assert.That(selected.Id, Is.EqualTo(1));
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
        public void CalculateRecordingOffsetMs_ClampsNegativeComputedOffsetToZero()
        {
            DateTimeOffset detectedAt = DateTimeOffset.FromUnixTimeSeconds(1_714_821_542);
            SynologyRecording recording = new()
            {
                StartTimeUnixSeconds = 1_714_821_540
            };

            int offsetMs = SynologyService.CalculateRecordingOffsetMs(recording, detectedAt, -5000);

            Assert.That(offsetMs, Is.Zero);
        }

        [Test]
        public void TryCalculateRecordingOffsetMs_RejectsUnknownStart()
        {
            DateTimeOffset detectedAt = DateTimeOffset.FromUnixTimeSeconds(1_714_821_600);
            SynologyRecording recording = new();

            bool calculated = SynologyService.TryCalculateRecordingOffsetMs(recording, detectedAt, 3000, out int offsetMs);

            Assert.That(calculated, Is.False);
            Assert.That(offsetMs, Is.Zero);
            Assert.That(SynologyService.CalculateRecordingOffsetMs(recording, detectedAt, 3000), Is.Zero);
        }

        [Test]
        public void TryCalculateRecordingOffsetMs_RejectsImplausiblyLargeOffset()
        {
            DateTimeOffset detectedAt = DateTimeOffset.FromUnixTimeSeconds(1_714_821_600);
            SynologyRecording recording = new()
            {
                StartTimeUnixSeconds = 946_684_800
            };

            bool calculated = SynologyService.TryCalculateRecordingOffsetMs(recording, detectedAt, 0, out int offsetMs);

            Assert.That(calculated, Is.False);
            Assert.That(offsetMs, Is.Zero);
        }

        [Test]
        public void TryCalculateRecordingOffsetMs_RejectsFutureStart()
        {
            DateTimeOffset detectedAt = DateTimeOffset.FromUnixTimeSeconds(1_714_821_600);
            SynologyRecording recording = new()
            {
                StartTimeUnixSeconds = 1_714_821_601
            };

            bool calculated = SynologyService.TryCalculateRecordingOffsetMs(recording, detectedAt, 0, out int offsetMs);

            Assert.That(calculated, Is.False);
            Assert.That(offsetMs, Is.Zero);
        }

        [Test]
        public void TryCalculateRecordingDownloadWindowMs_CapsPlayTimeAtRecordingEnd()
        {
            DateTimeOffset detectedAt = DateTimeOffset.FromUnixTimeSeconds(1_714_821_600);
            SynologyRecording recording = new()
            {
                StartTimeUnixSeconds = 1_714_821_540,
                EndTimeUnixSeconds = 1_714_821_602
            };

            bool calculated = SynologyService.TryCalculateRecordingDownloadWindowMs(recording, detectedAt, 0, 5000, out int offsetMs, out int playTimeMs);

            Assert.That(calculated, Is.True);
            Assert.That(offsetMs, Is.EqualTo(60000));
            Assert.That(playTimeMs, Is.EqualTo(2000));
        }

        [Test]
        public void CalculateRecordingOffsetMs_IgnoresImplausibleStartInsteadOfSaturating()
        {
            DateTimeOffset detectedAt = DateTimeOffset.FromUnixTimeSeconds(1_778_006_626);
            SynologyRecording recording = new()
            {
                StartTimeUnixSeconds = 7
            };

            int offsetMs = SynologyService.CalculateRecordingOffsetMs(recording, detectedAt, 0);

            Assert.That(offsetMs, Is.Zero);
            Assert.That(offsetMs, Is.Not.EqualTo(int.MaxValue));
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
        public void GetRecordingStartTime_IgnoresImplausibleStartTime()
        {
            SynologyRecording recording = new()
            {
                StartTimeUnixSeconds = 7
            };

            DateTimeOffset? start = SynologyService.GetRecordingStartTime(recording);

            Assert.That(start, Is.Null);
        }

        [Test]
        public void GetRecordingStartTime_FallsBackToFilePathWhenStartTimeIsImplausible()
        {
            SynologyRecording recording = new()
            {
                StartTimeUnixSeconds = 7,
                FilePath = "20240504PM/Entree20240504-103900-1714821540.mp4"
            };

            DateTimeOffset? start = SynologyService.GetRecordingStartTime(recording);

            Assert.That(start, Is.EqualTo(DateTimeOffset.FromUnixTimeSeconds(1_714_821_540)));
        }

        [Test]
        public void GetRecordingStartTime_FallsBackToLocalFilePathTimestampUsingReferenceOffset()
        {
            DateTimeOffset detectedAt = new(2026, 5, 6, 9, 2, 45, TimeSpan.FromHours(2));
            SynologyRecording recording = new()
            {
                FilePath = "20260506AM/Entree20260506-090200.mp4"
            };

            DateTimeOffset? start = SynologyService.GetRecordingStartTime(recording, detectedAt);

            Assert.That(start, Is.EqualTo(DateTimeOffset.FromUnixTimeSeconds(1_778_050_920)));
        }

        [Test]
        public void TryCalculateRecordingDownloadWindowMs_UsesLocalFilePathTimestampWhenUnixSuffixIsMissing()
        {
            DateTimeOffset detectedAt = new(2026, 5, 6, 9, 2, 45, TimeSpan.FromHours(2));
            SynologyRecording recording = new()
            {
                FilePath = "20260506AM/Entree20260506-090200.mp4"
            };

            bool calculated = SynologyService.TryCalculateRecordingDownloadWindowMs(recording, detectedAt, -5000, 10000, out int offsetMs, out int playTimeMs);

            Assert.That(calculated, Is.True);
            Assert.That(offsetMs, Is.EqualTo(40000));
            Assert.That(playTimeMs, Is.EqualTo(10000));
        }

        [Test]
        public void GetRecordingStartTime_PrefersPlausibleStartTimeBeforeFilePath()
        {
            SynologyRecording recording = new()
            {
                StartTimeUnixSeconds = 1_714_821_500,
                FilePath = "20240504PM/Entree20240504-103900-1714821540.mp4"
            };

            DateTimeOffset? start = SynologyService.GetRecordingStartTime(recording);

            Assert.That(start, Is.EqualTo(DateTimeOffset.FromUnixTimeSeconds(1_714_821_500)));
        }

        [Test]
        public void GetRecordingStartTime_DoesNotThrowForOutOfRangeUnixTime()
        {
            SynologyRecording recording = new()
            {
                StartTimeUnixSeconds = long.MaxValue
            };

            Assert.DoesNotThrow(() => SynologyService.GetRecordingStartTime(recording));
            Assert.That(SynologyService.GetRecordingStartTime(recording), Is.Null);
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

        private static void Configure()
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["Url"] = "http://synology.local",
                    ["User"] = "synoai",
                    ["Password"] = "password",
                    ["AccessToken"] = "token",
                    ["HttpRetryCount"] = "0",
                    ["AI:Type"] = "CodeProjectAIServer",
                    ["AI:Url"] = "http://codeproject-ai:32168",
                    ["Cameras:0:Name"] = "Entree",
                    ["Cameras:0:Types:0"] = "Person",
                    ["Cameras:0:Threshold"] = "50",
                    ["Notifiers:0:Type"] = "Telegram",
                    ["Notifiers:0:ChatID"] = "1",
                    ["Notifiers:0:Token"] = "token"
                })
                .Build();

            Config.Generate(NullLogger.Instance, configuration);
        }

        private static SynologyService CreateInitializedService(HttpClient httpClient)
        {
            SynologyService service = new(
                applicationLifetime: null,
                NullLogger<SynologyService>.Instance,
                new FakeHttpClientFactory(httpClient),
                new SynologyCookieStore());

            SetPrivateProperty(service, "Cookie", new Cookie("id", "session"));
            SetPrivateProperty(service, "Cameras", new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Entree"] = 42
            });
            SetPrivateProperty(service, "_recordingPath", "entry.cgi");
            return service;
        }

        private static void SetPrivateProperty(object target, string propertyName, object value)
        {
            target.GetType()
                .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(target, value);
        }

        private sealed class FakeHttpClientFactory : IHttpClientFactory
        {
            private readonly HttpClient _httpClient;

            public FakeHttpClientFactory(HttpClient httpClient)
            {
                _httpClient = httpClient;
            }

            public HttpClient CreateClient(string name)
            {
                return _httpClient;
            }
        }

        private sealed class RecordingHttpMessageHandler : HttpMessageHandler
        {
            public List<Uri> Requests { get; } = new();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(request.RequestUri);

                if (request.RequestUri.Query.Contains("method=List") &&
                    request.RequestUri.Query.Contains("fromTime="))
                {
                    return Task.FromResult(JsonResponse(
                        @"{""success"":true,""data"":{""recordings"":[{""id"":1,""startTime"":7,""filePath"":""19700101AM/Entree19700101-000007-7.mp4""}]}}"));
                }

                if (request.RequestUri.Query.Contains("method=List"))
                {
                    return Task.FromResult(JsonResponse(
                        @"{""success"":true,""data"":{""recordings"":[{""id"":2,""filePath"":""20240504PM/Entree20240504-103900-1714821540.mp4"",""endTime"":1714821660}]}}"));
                }

                if (request.RequestUri.Query.Contains("method=Download"))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            private static HttpResponseMessage JsonResponse(string json)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json)
                };
            }
        }
    }
}
