using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SynoAI.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SynoAI.Services
{
    public sealed class SynologyCookieStore
    {
        public CookieContainer CookieContainer { get; } = new();
    }

    public class SynologyService : ISynologyService
    {
        public const string HttpClientName = "synology";

        /// <summary>
        /// The current cookie with valid authentication.
        /// </summary>
        private Cookie Cookie { get; set; }
        private readonly SemaphoreSlim _loginSemaphore = new(1, 1);
        private readonly CookieContainer _cookieContainer;

        /// <summary>
        /// A list of all cameras mapped from the config friendly name to the Synology Camera ID.
        /// </summary>
        protected Dictionary<string, int> Cameras { get; private set; }

        private const string API_LOGIN = "SYNO.API.Auth";
        private const string API_CAMERA = "SYNO.SurveillanceStation.Camera";
        private const string API_RECORDING = "SYNO.SurveillanceStation.Recording";

        private const string URI_INFO = "webapi/query.cgi?api=SYNO.API.Info&version=1&method=query";
        private const string URI_LOGIN = "webapi/{0}";
        private const string URI_CAMERA_INFO = "webapi/{0}?api=SYNO.SurveillanceStation.Camera&method=List&version={1}";
        private const string URI_CAMERA_SNAPSHOT = "webapi/{0}?version={1}&id={2}&api=SYNO.SurveillanceStation.Camera&method=GetSnapshot&profileType={3}";
        private const string URI_RECORDING_LIST = "webapi/{0}?api=SYNO.SurveillanceStation.Recording&method=List&version=6&cameraIds={1}&offset=0&limit={2}&fromTime={3}&toTime=0";
        private const string URI_RECORDING_LIST_RECENT = "webapi/{0}?api=SYNO.SurveillanceStation.Recording&method=List&version=6&cameraIds={1}&offset=0&limit={2}";
        private const string URI_RECORDING_DOWNLOAD = "webapi/{0}/{1}?api=SYNO.SurveillanceStation.Recording&method=Download&version=6&id={2}&offsetTimeMs={3}&playTimeMs={4}";
        private const int RecordingListLimit = 20;
        private const int RecordingLookupBeforeSeconds = 15 * 60;
        private const int MaxRecordingOffsetWithoutEndTimeMs = 24 * 60 * 60 * 1000;
        private static readonly DateTimeOffset MinimumPlausibleRecordingTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private static readonly Regex RecordingDateTimeInFileName = new(
            @"(?<date>\d{8})-(?<time>\d{6})(?:-|$)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Holds the entry point to the SYNO.API.Auth API entry point.
        /// </summary>
        private string _loginPath { get; set; }
        /// <summary>
        /// Holds the entry point to the SYNO.SurveillanceStation.Camera API entry point.
        /// </summary>
        private string _cameraPath { get; set; }
        /// <summary>
        /// Holds the entry point to the SYNO.SurveillanceStation.Recording API entry point.
        /// </summary>
        private string _recordingPath { get; set; }

        private readonly IHostApplicationLifetime _applicationLifetime;
        private readonly ILogger<SynologyService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;

        public SynologyService(
            IHostApplicationLifetime applicationLifetime,
            ILogger<SynologyService> logger,
            IHttpClientFactory httpClientFactory,
            SynologyCookieStore cookieStore)
        {
            _applicationLifetime = applicationLifetime;
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _cookieContainer = cookieStore.CookieContainer;
        }

        /// <summary>
        /// Fetches all the end points, because they're dynamic between DSM versions.
        /// </summary>
        public async Task<bool> GetEndPointsAsync()
        {
            _logger.LogInformation("API: Querying end points");

            HttpClient httpClient = GetHttpClient();
            using HttpResponseMessage result = await SendWithTransientRetriesAsync(
                () => httpClient.GetAsync(URI_INFO),
                "query API endpoints");
            if (result.IsSuccessStatusCode)
            {
                SynologyResponse<SynologyApiInfoResponse> response = await GetResponse<SynologyApiInfoResponse>(result);
                if (response.Success)
                {
                    // Find the Authentication entry point
                    if (response.Data.TryGetValue(API_LOGIN, out SynologyApiInfo loginInfo))
                    {
                        _logger.LogDebug($"API: Found path '{loginInfo.Path}' for {API_LOGIN}");

                        if (loginInfo.MaxVersion < Config.ApiVersionAuth)
                        {
                            _logger.LogError($"API: {API_LOGIN} only supports a max version of {loginInfo.MaxVersion}, but the system is set to use version {Config.ApiVersionAuth}.");
                        }

                        if (TryNormalizeApiPath(loginInfo.Path, out string loginPath))
                        {
                            _loginPath = loginPath;
                        }
                        else
                        {
                            _logger.LogError($"API: Ignoring unsafe path '{loginInfo.Path}' for {API_LOGIN}.");
                        }
                    }
                    else
                    {
                        _logger.LogError($"API: Failed to find {API_LOGIN}.");
                    }

                    // Find the Camera entry point
                    if (response.Data.TryGetValue(API_CAMERA, out SynologyApiInfo cameraInfo))
                    {
                        _logger.LogDebug($"API: Found path '{cameraInfo.Path}' for {API_CAMERA}");

                        if (cameraInfo.MaxVersion < Config.ApiVersionCamera)
                        {
                            _logger.LogError($"API: {API_CAMERA} only supports a max version of {cameraInfo.MaxVersion}, but the system is set to use version {Config.ApiVersionCamera}.");
                        }

                        if (TryNormalizeApiPath(cameraInfo.Path, out string cameraPath))
                        {
                            _cameraPath = cameraPath;
                        }
                        else
                        {
                            _logger.LogError($"API: Ignoring unsafe path '{cameraInfo.Path}' for {API_CAMERA}.");
                        }
                    }
                    else
                    {
                        _logger.LogError($"API: Failed to find {API_CAMERA}.");
                    }

                    // Find the Recording entry point. This is optional and only used by recording clip notifications.
                    if (response.Data.TryGetValue(API_RECORDING, out SynologyApiInfo recordingInfo))
                    {
                        _logger.LogDebug($"API: Found path '{recordingInfo.Path}' for {API_RECORDING}");
                        if (TryNormalizeApiPath(recordingInfo.Path, out string recordingPath))
                        {
                            _recordingPath = recordingPath;
                        }
                        else
                        {
                            _logger.LogWarning($"API: Ignoring unsafe path '{recordingInfo.Path}' for {API_RECORDING}.");
                        }
                    }
                    else
                    {
                        _logger.LogWarning($"API: Failed to find {API_RECORDING}. Recording clips will not be available.");
                    }

                    if (string.IsNullOrWhiteSpace(_loginPath) || string.IsNullOrWhiteSpace(_cameraPath))
                    {
                        _logger.LogError("API: Failed to map required end points.");
                        return false;
                    }

                    _logger.LogInformation("API: Successfully mapped all end points");
                    return true;
                }
                else
                {
                    _logger.LogError($"API: Failed due to error code '{response.Error.Code}'");
                }
            }
            else
            {
                _logger.LogError($"API: Failed due to HTTP status code '{result.StatusCode}'");
            }

            return false;
        }

        /// <summary>
        /// Generates a login cookie for the username and password in the config.
        /// </summary>
        /// <returns>A cookie, or null on failure.</returns>
        public async Task<Cookie> LoginAsync()
        {
            return await RefreshCookieAsync();
        }

        private async Task<Cookie> LoginCoreAsync()
        {
            _logger.LogInformation("Login: Authenticating");

            string loginUri = string.Format(URI_LOGIN, _loginPath);
            _logger.LogDebug($"Login: Logging in via '{loginUri}'");

            HttpClient httpClient = GetHttpClient();
            using HttpResponseMessage result = await SendWithTransientRetriesAsync(
                () => httpClient.PostAsync(loginUri, CreateLoginContent()),
                "login");
            if (result.IsSuccessStatusCode)
            {
                SynologyResponse<SynologyLogin> response = await GetResponse<SynologyLogin>(result);
                if (response.Success)
                {
                    _logger.LogInformation("Login: Successful");

                    IEnumerable<Cookie> cookies = _cookieContainer.GetCookies(httpClient.BaseAddress).Cast<Cookie>().ToList();
                    Cookie cookie = cookies.FirstOrDefault(x => x.Name == "id");
                    if (cookie == null)
                    {
                        _applicationLifetime.StopApplication();
                    }

                    return cookie;
                }
                else
                {
                    _logger.LogError($"Login: Failed due to Synology API error code '{response.Error.Code}'");
                }
            }
            else
            {
                _logger.LogError($"Login: Failed due to HTTP status code '{result.StatusCode}'");
            }

            return null;
        }

        private async Task<Cookie> EnsureCookieAsync()
        {
            if (Cookie != null)
            {
                return Cookie;
            }

            await _loginSemaphore.WaitAsync();
            try
            {
                if (Cookie == null)
                {
                    Cookie = await LoginCoreAsync();
                }

                return Cookie;
            }
            finally
            {
                _loginSemaphore.Release();
            }
        }

        private async Task<Cookie> RefreshCookieAsync()
        {
            await _loginSemaphore.WaitAsync();
            try
            {
                Cookie = await LoginCoreAsync();
                return Cookie;
            }
            finally
            {
                _loginSemaphore.Release();
            }
        }

        /// <summary>
        /// Fetches all of the required camera information from the API.
        /// </summary>
        /// <returns>A list of all cameras.</returns>
        public async Task<IEnumerable<SynologyCamera>> GetCamerasAsync()
        {
            _logger.LogInformation("GetCameras: Fetching Cameras");

            HttpClient client = GetHttpClient();
            _cookieContainer.Add(client.BaseAddress, new Cookie("id", Cookie.Value));

            string cameraInfoUri = string.Format(URI_CAMERA_INFO, _cameraPath, Config.ApiVersionCamera);
            using HttpResponseMessage result = await SendWithTransientRetriesAsync(
                () => client.GetAsync(cameraInfoUri),
                "get cameras");

            SynologyResponse<SynologyCameras> response = await GetResponse<SynologyCameras>(result);
            if (response.Success)
            {
                _logger.LogInformation($"GetCameras: Successful. Found {response.Data.Cameras.Count()} cameras.");
                return response.Data.Cameras;
            }
            else
            {
                _logger.LogError($"GetCameras: Failed due to error code '{response.Error.Code}'");
            }

            return null;
        }

        /// <summary>
        /// Takes a snapshot of the specified camera.
        /// </summary>
        /// <returns>A string to the file path.</returns>
        public async Task<byte[]> TakeSnapshotAsync(string cameraName)
        {
            return await TakeSnapshotAsync(cameraName, retryAfterLogin: true);
        }

        public async Task<ProcessedFile> DownloadLatestRecordingClipAsync(string cameraName, DateTimeOffset detectedAt, int offsetTimeMs, int playTimeMs)
        {
            if (string.IsNullOrWhiteSpace(_recordingPath))
            {
                _logger.LogWarning($"{cameraName}: Cannot download recording clip because the Recording API endpoint was not found.");
                return null;
            }

            if (playTimeMs <= 0)
            {
                _logger.LogWarning($"{cameraName}: Cannot download recording clip because the requested duration is invalid.");
                return null;
            }

            HttpClient client = GetHttpClient();

            if (await EnsureCookieAsync() == null)
            {
                _logger.LogError($"{cameraName}: Cannot download recording clip because Synology login failed.");
                return null;
            }

            _cookieContainer.Add(client.BaseAddress, new Cookie("id", Cookie.Value));

            if (Cameras == null || !Cameras.TryGetValue(cameraName, out int id))
            {
                _logger.LogError($"The camera with the name '{cameraName}' was not found in the Synology camera list.");
                return null;
            }

            List<SynologyRecording> recordings = await TryListRecordingsAsync(
                client,
                BuildRecordingListResource(_recordingPath, id, detectedAt),
                cameraName,
                "time-filtered");
            LogRecordingCandidates(cameraName, "time-filtered", recordings, detectedAt);
            SynologyRecording recording = SelectRecordingForDetection(recordings, detectedAt);
            if (ShouldRetryWithRecentRecordingList(recordings, recording, detectedAt))
            {
                _logger.LogInformation(
                    "{cameraName}: Time-filtered recording lookup did not find a usable clip candidate. Retrying with the latest recordings.",
                    cameraName);

                List<SynologyRecording> recentRecordings = await TryListRecordingsAsync(
                    client,
                    BuildRecentRecordingListResource(_recordingPath, id),
                    cameraName,
                    "latest");
                LogRecordingCandidates(cameraName, "latest", recentRecordings, detectedAt);
                SynologyRecording recentRecording = SelectRecordingForDetection(recentRecordings, detectedAt);
                if (recentRecording != null)
                {
                    recording = recentRecording;
                }
            }

            if (recording == null)
            {
                _logger.LogInformation($"{cameraName}: No recent recordings found.");
                return null;
            }

            if (ShouldRetryWithRecentRecordingList(new[] { recording }, recording, detectedAt))
            {
                _logger.LogWarning(
                    "{cameraName}: No usable recording candidate was found for detection at {detectedAt}; skipping video clip download.",
                    cameraName,
                    detectedAt.LocalDateTime);
                return null;
            }

            if (!TryCalculateRecordingDownloadWindowMs(recording, detectedAt, offsetTimeMs, playTimeMs, out int downloadOffsetTimeMs, out int downloadPlayTimeMs))
            {
                _logger.LogWarning(
                    "{cameraName}: Selected recording {recordingId} produced an implausible download offset for detection at {detectedAt}; skipping video clip download.",
                    cameraName,
                    recording.Id,
                    detectedAt.LocalDateTime);
                return null;
            }

            DateTimeOffset? recordingStart = GetRecordingStartTime(recording, detectedAt);
            if (recordingStart.HasValue)
            {
                _logger.LogInformation(
                    "{cameraName}: Selected recording {recordingId} starting at {recordingStart} for detection at {detectedAt}. Download offset is {offsetTimeMs}ms.",
                    cameraName,
                    recording.Id,
                    recordingStart.Value.LocalDateTime,
                    detectedAt.LocalDateTime,
                    downloadOffsetTimeMs);
            }

            if (downloadPlayTimeMs != playTimeMs)
            {
                _logger.LogInformation(
                    "{cameraName}: Recording clip duration capped from {requestedPlayTimeMs}ms to {downloadPlayTimeMs}ms because the selected recording ends earlier.",
                    cameraName,
                    playTimeMs,
                    downloadPlayTimeMs);
            }

            string safeCameraName = CaptureFileStore.ToSafePathSegment(cameraName);
            string directory = Path.Combine(Constants.DIRECTORY_CAPTURES, safeCameraName);
            Directory.CreateDirectory(directory);
            string filePath = Path.Combine(directory, $"{safeCameraName}_{DateTime.Now:yyyy_MM_dd_HH_mm_ss_FFF}_clip.mp4");

            string sourceFileName = string.IsNullOrWhiteSpace(recording.FilePath)
                ? $"{recording.Id}.mp4"
                : Path.GetFileName(recording.FilePath);
            string fileName = Uri.EscapeDataString(sourceFileName);
            string downloadResource = string.Format(URI_RECORDING_DOWNLOAD, _recordingPath, fileName, recording.Id, downloadOffsetTimeMs, downloadPlayTimeMs);
            using HttpResponseMessage downloadResponse = await SendWithTransientRetriesAsync(
                () => client.GetAsync(downloadResource, HttpCompletionOption.ResponseHeadersRead),
                "download recording clip",
                cameraName);
            if (!downloadResponse.IsSuccessStatusCode)
            {
                _logger.LogError($"{cameraName}: Failed to download recording clip with HTTP status code '{downloadResponse.StatusCode}'");
                return null;
            }
            long? contentLength = downloadResponse.Content.Headers.ContentLength;
            if (Config.MaxRecordingClipBytes > 0 && contentLength.HasValue && contentLength.Value > Config.MaxRecordingClipBytes)
            {
                _logger.LogWarning(
                    "{cameraName}: Recording clip download was rejected because it is {contentLength} bytes, above the configured limit of {maxBytes} bytes.",
                    cameraName,
                    contentLength.Value,
                    Config.MaxRecordingClipBytes);
                return null;
            }

            using Stream input = await downloadResponse.Content.ReadAsStreamAsync();
            using FileStream output = File.Create(filePath);
            bool copied = await CopyToFileWithLimitAsync(input, output, Config.MaxRecordingClipBytes);
            if (!copied)
            {
                _logger.LogWarning($"{cameraName}: Downloaded recording clip exceeded the configured size limit.");
                output.Dispose();
                File.Delete(filePath);
                return null;
            }

            await output.FlushAsync();
            output.Dispose();
            FileInfo file = new(filePath);
            if (file.Length == 0)
            {
                _logger.LogWarning($"{cameraName}: Downloaded recording clip was empty.");
                File.Delete(filePath);
                return null;
            }

            _logger.LogInformation($"{cameraName}: Downloaded recording clip '{filePath}' ({file.Length} bytes).");
            return new ProcessedFile(filePath);

        }

        internal static string BuildRecordingListResource(string recordingPath, int cameraId, DateTimeOffset detectedAt)
        {
            long fromTime = Math.Max(0, detectedAt.ToUnixTimeSeconds() - RecordingLookupBeforeSeconds);
            return string.Format(URI_RECORDING_LIST, recordingPath, cameraId, RecordingListLimit, fromTime);
        }

        internal static string BuildRecentRecordingListResource(string recordingPath, int cameraId)
        {
            return string.Format(URI_RECORDING_LIST_RECENT, recordingPath, cameraId, RecordingListLimit);
        }

        internal static bool ShouldRetryWithRecentRecordingList(
            IEnumerable<SynologyRecording> recordings,
            SynologyRecording selectedRecording,
            DateTimeOffset detectedAt)
        {
            List<SynologyRecording> recordingList = recordings?.ToList() ?? new List<SynologyRecording>();
            if (recordingList.Count == 0 || selectedRecording == null)
            {
                return true;
            }

            DateTimeOffset? selectedStart = GetRecordingStartTime(selectedRecording, detectedAt);
            DateTimeOffset? selectedEnd = GetRecordingEndTime(selectedRecording, detectedAt);
            if (!selectedStart.HasValue)
            {
                return true;
            }

            if (selectedStart.HasValue && selectedEnd.HasValue &&
                selectedStart.Value <= detectedAt && detectedAt <= selectedEnd.Value)
            {
                return false;
            }

            if (selectedStart.HasValue && !selectedEnd.HasValue &&
                selectedStart.Value < detectedAt.AddMilliseconds(-MaxRecordingOffsetWithoutEndTimeMs))
            {
                return true;
            }

            if (selectedStart.HasValue && selectedStart.Value > detectedAt)
            {
                return true;
            }

            return selectedEnd.HasValue && selectedEnd.Value < detectedAt;
        }

        internal static SynologyRecording SelectRecordingForDetection(IEnumerable<SynologyRecording> recordings, DateTimeOffset detectedAt)
        {
            List<SynologyRecording> recordingList = recordings?.ToList() ?? new List<SynologyRecording>();
            if (recordingList.Count == 0)
            {
                return null;
            }

            SynologyRecording containingRecording = recordingList
                .Where(x =>
                {
                    DateTimeOffset? start = GetRecordingStartTime(x, detectedAt);
                    DateTimeOffset? end = GetRecordingEndTime(x, detectedAt);
                    return start.HasValue && end.HasValue && start.Value <= detectedAt && detectedAt <= end.Value;
                })
                .OrderByDescending(x => GetRecordingStartTime(x, detectedAt))
                .FirstOrDefault();
            if (containingRecording != null)
            {
                return containingRecording;
            }

            SynologyRecording latestStartedBeforeDetection = recordingList
                .Where(x =>
                {
                    DateTimeOffset? start = GetRecordingStartTime(x, detectedAt);
                    return start.HasValue && start.Value <= detectedAt;
                })
                .OrderByDescending(x => GetRecordingStartTime(x, detectedAt))
                .FirstOrDefault();
            if (latestStartedBeforeDetection != null)
            {
                return latestStartedBeforeDetection;
            }

            SynologyRecording nearestKnownStart = recordingList
                .Where(x => GetRecordingStartTime(x, detectedAt).HasValue)
                .OrderBy(x => Math.Abs((GetRecordingStartTime(x, detectedAt).Value - detectedAt).TotalMilliseconds))
                .FirstOrDefault();

            return nearestKnownStart ?? recordingList.FirstOrDefault();
        }

        internal static int CalculateRecordingOffsetMs(SynologyRecording recording, DateTimeOffset detectedAt, int configuredOffsetMs)
        {
            return TryCalculateRecordingOffsetMs(recording, detectedAt, configuredOffsetMs, out int offsetMs)
                ? offsetMs
                : 0;
        }

        internal static bool TryCalculateRecordingOffsetMs(SynologyRecording recording, DateTimeOffset detectedAt, int configuredOffsetMs, out int offsetMs)
        {
            return TryCalculateRecordingDownloadWindowMs(
                recording,
                detectedAt,
                configuredOffsetMs,
                requestedPlayTimeMs: 0,
                out offsetMs,
                out _);
        }

        internal static bool TryCalculateRecordingDownloadWindowMs(
            SynologyRecording recording,
            DateTimeOffset detectedAt,
            int configuredOffsetMs,
            int requestedPlayTimeMs,
            out int offsetMs,
            out int playTimeMs)
        {
            DateTimeOffset? recordingStart = GetRecordingStartTime(recording, detectedAt);
            offsetMs = 0;
            playTimeMs = requestedPlayTimeMs;
            if (!recordingStart.HasValue || recordingStart.Value > detectedAt)
            {
                return false;
            }

            long calculatedOffsetMs = configuredOffsetMs;
            calculatedOffsetMs += (long)(detectedAt - recordingStart.Value).TotalMilliseconds;

            if (calculatedOffsetMs <= 0)
            {
                calculatedOffsetMs = 0;
            }

            DateTimeOffset? recordingEnd = GetRecordingEndTime(recording, detectedAt);
            if (recordingEnd.HasValue)
            {
                long recordingDurationMs = (long)(recordingEnd.Value - recordingStart.Value).TotalMilliseconds;
                if (recordingDurationMs <= 0 || calculatedOffsetMs >= recordingDurationMs)
                {
                    return false;
                }

                if (requestedPlayTimeMs > 0 && calculatedOffsetMs + requestedPlayTimeMs > recordingDurationMs)
                {
                    long remainingPlayTimeMs = recordingDurationMs - calculatedOffsetMs;
                    if (remainingPlayTimeMs <= 0 || remainingPlayTimeMs > int.MaxValue)
                    {
                        return false;
                    }

                    playTimeMs = (int)remainingPlayTimeMs;
                }
            }
            else if (calculatedOffsetMs > MaxRecordingOffsetWithoutEndTimeMs)
            {
                return false;
            }

            if (calculatedOffsetMs > int.MaxValue)
            {
                return false;
            }

            offsetMs = (int)calculatedOffsetMs;
            return true;
        }

        internal static DateTimeOffset? GetRecordingStartTime(SynologyRecording recording, DateTimeOffset? referenceTime = null)
        {
            if (recording == null)
            {
                return null;
            }

            DateTimeOffset? startTime = GetPlausibleUnixTime(recording.StartTimeUnixSeconds);
            if (startTime.HasValue)
            {
                return startTime;
            }

            string fileName = string.IsNullOrWhiteSpace(recording.FilePath)
                ? null
                : Path.GetFileNameWithoutExtension(recording.FilePath);
            string lastSegment = fileName?.Split('-').LastOrDefault();
            if (long.TryParse(lastSegment, out long unixSeconds))
            {
                DateTimeOffset? unixTime = GetPlausibleUnixTime(unixSeconds);
                if (unixTime.HasValue)
                {
                    return unixTime;
                }
            }

            DateTime? fileNameLocalTime = GetRecordingLocalTimeFromFileName(fileName);
            return fileNameLocalTime.HasValue
                ? ResolveLocalRecordingTime(fileNameLocalTime.Value, referenceTime)
                : null;
        }

        private static DateTimeOffset? GetRecordingEndTime(SynologyRecording recording, DateTimeOffset? referenceTime = null)
        {
            DateTimeOffset? start = GetRecordingStartTime(recording, referenceTime);
            DateTimeOffset? end = GetPlausibleUnixTime(recording?.EndTimeUnixSeconds);
            DateTimeOffset? stop = GetPlausibleUnixTime(recording?.StopTimeUnixSeconds);
            if (start.HasValue)
            {
                if (end.HasValue && end.Value >= start.Value)
                {
                    return end;
                }

                if (stop.HasValue && stop.Value >= start.Value)
                {
                    return stop;
                }
            }

            return end ?? stop;
        }

        private static DateTime? GetRecordingLocalTimeFromFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            Match match = RecordingDateTimeInFileName.Match(fileName);
            if (!match.Success)
            {
                return null;
            }

            string value = match.Groups["date"].Value + match.Groups["time"].Value;
            if (!DateTime.TryParseExact(
                value,
                "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime parsed))
            {
                return null;
            }

            return DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        }

        private static DateTimeOffset ResolveLocalRecordingTime(DateTime localRecordingTime, DateTimeOffset? referenceTime)
        {
            DateTime unspecifiedTime = DateTime.SpecifyKind(localRecordingTime, DateTimeKind.Unspecified);
            TimeSpan offset = referenceTime?.Offset ?? TimeZoneInfo.Local.GetUtcOffset(unspecifiedTime);
            return new DateTimeOffset(unspecifiedTime, offset);
        }

        private static DateTimeOffset? GetPlausibleUnixTime(long? unixSeconds)
        {
            if (!unixSeconds.HasValue || unixSeconds.Value <= 0)
            {
                return null;
            }

            try
            {
                DateTimeOffset timestamp = DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value);
                return timestamp >= MinimumPlausibleRecordingTime
                    ? timestamp
                    : null;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        private async Task<List<SynologyRecording>> TryListRecordingsAsync(
            HttpClient client,
            string listResource,
            string cameraName,
            string lookupDescription)
        {
            using HttpResponseMessage listResponse = await SendWithTransientRetriesAsync(
                () => client.GetAsync(listResource),
                $"list {lookupDescription} recordings",
                cameraName);
            if (!listResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "{cameraName}: Failed to list {lookupDescription} recordings with HTTP status code '{statusCode}'",
                    cameraName,
                    lookupDescription,
                    listResponse.StatusCode);
                return null;
            }

            SynologyResponse<SynologyRecordings> recordingsResponse = await GetResponse<SynologyRecordings>(listResponse);
            if (!recordingsResponse.Success)
            {
                _logger.LogWarning(
                    "{cameraName}: Failed to list {lookupDescription} recordings with error code '{errorCode}'",
                    cameraName,
                    lookupDescription,
                    recordingsResponse.Error?.Code);
                return null;
            }

            return recordingsResponse.Data?.Recordings?.ToList() ?? new List<SynologyRecording>();
        }

        private void LogRecordingCandidates(
            string cameraName,
            string lookupDescription,
            IEnumerable<SynologyRecording> recordings,
            DateTimeOffset detectedAt)
        {
            List<SynologyRecording> recordingList = recordings?.ToList() ?? new List<SynologyRecording>();
            _logger.LogDebug(
                "{cameraName}: {lookupDescription} recording lookup returned {recordingCount} candidate(s).",
                cameraName,
                lookupDescription,
                recordingList.Count);

            foreach (SynologyRecording recording in recordingList.Take(RecordingListLimit))
            {
                DateTimeOffset? start = GetRecordingStartTime(recording, detectedAt);
                DateTimeOffset? end = GetRecordingEndTime(recording, detectedAt);
                _logger.LogDebug(
                    "{cameraName}: Recording candidate {recordingId}: filePath='{filePath}', startUtc={startUtc}, endUtc={endUtc}, sizeByte={sizeByte}.",
                    cameraName,
                    recording.Id,
                    recording.FilePath,
                    FormatUtcTimestamp(start),
                    FormatUtcTimestamp(end),
                    recording.SizeByte);
            }
        }

        private static string FormatUtcTimestamp(DateTimeOffset? timestamp)
        {
            return timestamp.HasValue
                ? timestamp.Value.UtcDateTime.ToString("u", CultureInfo.InvariantCulture)
                : "(unknown)";
        }

        private async Task<byte[]> TakeSnapshotAsync(string cameraName, bool retryAfterLogin)
        {
            HttpClient client = GetHttpClient();

            if (await EnsureCookieAsync() == null)
            {
                _logger.LogError($"{cameraName}: Cannot take snapshot because Synology login failed.");
                return null;
            }

            _cookieContainer.Add(client.BaseAddress, new Cookie("id", Cookie.Value));

            if (Cameras != null && Cameras.TryGetValue(cameraName, out int id))
            {
                _logger.LogDebug($"{cameraName}: Found with Synology ID '{id}'.");

                string resource = BuildSnapshotResource(_cameraPath, Config.ApiVersionCamera, id, Config.Quality);
                _logger.LogDebug($"{cameraName}: Taking snapshot from '{resource}'.");

                _logger.LogInformation($"{cameraName}: Taking snapshot");
                try
                {
                    using (HttpResponseMessage response = await SendWithTransientRetriesAsync(
                        () => client.GetAsync(resource, HttpCompletionOption.ResponseHeadersRead),
                        "take snapshot",
                        cameraName))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            _logger.LogError($"{cameraName}: Failed to get snapshot with HTTP status code '{response.StatusCode}'");
                            return null;
                        }

                        if (response.Content.Headers.ContentType?.MediaType == "image/jpeg")
                        {
                            // Only return the bytes when we have a valid image back
                            _logger.LogDebug($"{cameraName}: Reading snapshot");
                            return await ReadSnapshotWithLimitAsync(response.Content, cameraName);
                        }
                        else
                        {
                            // We didn't get an image type back, so this must have errored
                            SynologyResponse errorResponse = await GetErrorResponse(response);
                            if (errorResponse.Success)
                            {
                                // This should never happen, but let's add logging just in case
                                _logger.LogError($"{cameraName}: Failed to get snapshot, but the API reported success.");
                            }
                            else
                            {
                                _logger.LogError($"{cameraName}: Failed to get snapshot with error code '{errorResponse.Error.Code}'");
                                if (retryAfterLogin && IsAuthenticationError(errorResponse))
                                {
                                    _logger.LogInformation($"{cameraName}: Synology session appears expired. Logging in again and retrying snapshot.");
                                    Cookie = await RefreshCookieAsync();
                                    if (Cookie != null)
                                    {
                                        return await TakeSnapshotAsync(cameraName, retryAfterLogin: false);
                                    }
                                }
                            }
                        }
                    }
                }
                catch (TaskCanceledException ex)
                {
                    _logger.LogError(ex, $"{cameraName}: Snapshot request timed out after {Config.SynologyTimeoutSeconds} seconds.");
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogError(ex, $"{cameraName}: Snapshot request failed.");
                }
            }
            else
            {
                _logger.LogError($"The camera with the name '{cameraName}' was not found in the Synology camera list.");
            }

            return null;

        }

        private static bool IsAuthenticationError(SynologyResponse response)
        {
            string code = response?.Error?.Code;
            return code == "105" || code == "106" || code == "107" || code == "119";
        }

        private static FormUrlEncodedContent CreateLoginContent()
        {
            return new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["api"] = "SYNO.API.Auth",
                ["method"] = "Login",
                ["version"] = Config.ApiVersionAuth.ToString(),
                ["account"] = Config.Username,
                ["passwd"] = Config.Password,
                ["session"] = "SurveillanceStation",
                ["format"] = "cookie"
            });
        }

        internal static string BuildSnapshotResource(string cameraPath, int apiVersionCamera, int cameraId, CameraQuality quality)
        {
            return string.Format(URI_CAMERA_SNAPSHOT, cameraPath, apiVersionCamera, cameraId, (int)quality);
        }

        internal static bool TryNormalizeApiPath(string path, out string normalizedPath)
        {
            normalizedPath = null;

            if (string.IsNullOrWhiteSpace(path) ||
                path.StartsWith("/", StringComparison.Ordinal) ||
                path.StartsWith("\\", StringComparison.Ordinal) ||
                path.Contains('?') ||
                path.Contains('#') ||
                Uri.TryCreate(path, UriKind.Absolute, out _))
            {
                return false;
            }

            string[] segments = path.Split(new[] { '/', '\\' }, StringSplitOptions.None);
            if (segments.Length == 0 || segments.Any(segment => !CaptureFileStore.IsSafePathSegment(segment)))
            {
                return false;
            }

            normalizedPath = string.Join("/", segments);
            return true;
        }

        private async Task<byte[]> ReadSnapshotWithLimitAsync(HttpContent content, string cameraName)
        {
            if (Config.MaxSnapshotBytes <= 0)
            {
                return await content.ReadAsByteArrayAsync();
            }

            if (content.Headers.ContentLength.HasValue && content.Headers.ContentLength.Value > Config.MaxSnapshotBytes)
            {
                _logger.LogWarning(
                    "{cameraName}: Snapshot content length {contentLength} exceeds configured limit {maxSnapshotBytes}.",
                    cameraName,
                    content.Headers.ContentLength.Value,
                    Config.MaxSnapshotBytes);
                return null;
            }

            using Stream input = await content.ReadAsStreamAsync();
            using MemoryStream output = new();
            byte[] buffer = new byte[81920];
            int read;
            long totalRead = 0;

            while ((read = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                totalRead += read;
                if (totalRead > Config.MaxSnapshotBytes)
                {
                    _logger.LogWarning(
                        "{cameraName}: Snapshot exceeded configured limit {maxSnapshotBytes}.",
                        cameraName,
                        Config.MaxSnapshotBytes);
                    return null;
                }

                output.Write(buffer, 0, read);
            }

            return output.ToArray();
        }

        private static async Task<bool> CopyToFileWithLimitAsync(Stream input, Stream output, int maxBytes)
        {
            byte[] buffer = new byte[81920];
            int read;
            long totalRead = 0;

            while ((read = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                totalRead += read;
                if (maxBytes > 0 && totalRead > maxBytes)
                {
                    return false;
                }

                await output.WriteAsync(buffer, 0, read);
            }

            return true;
        }

        /// <summary>
        /// Fetches the response content and parses it to the specified type.
        /// </summary>
        /// <typeparam name="T">The type of the return 'data'.</typeparam>
        /// <param name="message">The message to parse.</param>
        /// <returns>A Synology response object.</returns>
        private async Task<SynologyResponse<T>> GetResponse<T>(HttpResponseMessage message)
        {
            string content = await message.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<SynologyResponse<T>>(content);
        }

        /// <summary>
        /// Fetches the error response content and parses it to the specified type.
        /// </summary>
        /// <param name="message">The message to parse.</param>
        /// <returns>A Synology response object.</returns>
        private async Task<SynologyResponse> GetErrorResponse(HttpResponseMessage message)
        {
            string content = await message.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<SynologyResponse>(content);
        }

        private async Task<HttpResponseMessage> SendWithTransientRetriesAsync(
            Func<Task<HttpResponseMessage>> sendAsync,
            string operation,
            string cameraName = null)
        {
            int maxAttempts = Config.HttpRetryCount + 1;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    HttpResponseMessage response = await sendAsync();
                    if (ShouldRetry(response.StatusCode, attempt, maxAttempts))
                    {
                        _logger.LogWarning(
                            "{cameraNamePrefix}Synology {operation} returned transient HTTP status code '{statusCode}' on attempt {attempt} of {maxAttempts}.",
                            FormatCameraPrefix(cameraName),
                            operation,
                            response.StatusCode,
                            attempt,
                            maxAttempts);
                        response.Dispose();
                        await DelayBeforeRetry(operation, cameraName, attempt, maxAttempts);
                        continue;
                    }

                    return response;
                }
                catch (TaskCanceledException ex) when (attempt < maxAttempts)
                {
                    _logger.LogWarning(
                        ex,
                        "{cameraNamePrefix}Synology {operation} timed out on attempt {attempt} of {maxAttempts}.",
                        FormatCameraPrefix(cameraName),
                        operation,
                        attempt,
                        maxAttempts);
                    await DelayBeforeRetry(operation, cameraName, attempt, maxAttempts);
                }
                catch (HttpRequestException ex) when (attempt < maxAttempts)
                {
                    _logger.LogWarning(
                        ex,
                        "{cameraNamePrefix}Synology {operation} failed on attempt {attempt} of {maxAttempts}.",
                        FormatCameraPrefix(cameraName),
                        operation,
                        attempt,
                        maxAttempts);
                    await DelayBeforeRetry(operation, cameraName, attempt, maxAttempts);
                }
            }

            throw new InvalidOperationException($"Synology {operation} retry loop ended unexpectedly.");
        }

        private static bool ShouldRetry(HttpStatusCode statusCode, int attempt, int maxAttempts)
        {
            int status = (int)statusCode;
            return attempt < maxAttempts && (status == 408 || status == 429 || status >= 500);
        }

        private async Task DelayBeforeRetry(string operation, string cameraName, int attempt, int maxAttempts)
        {
            int delayMs = Config.HttpRetryDelayMs * attempt;
            if (delayMs <= 0)
            {
                return;
            }

            _logger.LogInformation(
                "{cameraNamePrefix}Retrying Synology {operation} after {delayMs}ms ({nextAttempt}/{maxAttempts}).",
                FormatCameraPrefix(cameraName),
                operation,
                delayMs,
                attempt + 1,
                maxAttempts);
            await Task.Delay(delayMs);
        }

        private static string FormatCameraPrefix(string cameraName)
        {
            return string.IsNullOrWhiteSpace(cameraName) ? string.Empty : $"{cameraName}: ";
        }

        public async Task InitialiseAsync()
        {
            _logger.LogInformation("Initialising");

            // Get the actual end points, because they're not guaranteed to be the same on all installations and DSM versions
            try
            {
                bool retrievedEndPoints = await GetEndPointsAsync();
                if (!retrievedEndPoints)
                {
                    _applicationLifetime.StopApplication();
                }

                // Perform a login first as all actions need a valid cookie
                Cookie = await LoginAsync();
                if (Cookie == null)
                {
                    // The login failed, so kill the application
                    _applicationLifetime.StopApplication();
                    return;
                }

                // If no cameras are specified, then bail out
                if (Config.Cameras == null || !Config.Cameras.Any())
                {
                    _logger.LogWarning("Aborting Initialisation: No Cameras were specified in the config.");
                    _applicationLifetime.StopApplication();
                    return;
                }

                // If no notifications are specified, then bail out
                if (Config.Notifiers == null || !Config.Notifiers.Any())
                {
                    _logger.LogWarning("Aborting Initialisation: No Notifications were specified in the config.");
                    _applicationLifetime.StopApplication();
                    return;
                }

                // Fetch all the cameras and store a Name to ID dictionary for quick lookup
                IEnumerable<SynologyCamera> synologyCameras = await GetCamerasAsync();
                if (synologyCameras == null)
                {
                    // We failed to fetch the cameras, so kill the application
                    _applicationLifetime.StopApplication();
                    return;
                }
                else
                {
                    Cameras = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (Camera camera in Config.Cameras)
                    {
                        SynologyCamera match = synologyCameras.FirstOrDefault(x => x.GetName().Equals(camera.Name, StringComparison.OrdinalIgnoreCase));
                        if (match == null)
                        {
                            _logger.LogWarning($"Initialise: The camera with the name '{camera.Name}' was not found in the Surveillance Station camera list.");
                        }
                        else
                        {
                            Cameras.Add(camera.Name, match.Id);
                        }
                    }
                }

                _logger.LogInformation("Initialisation successful.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unhandled exception occurred initialising SynoAI. Exiting...");
                _applicationLifetime.StopApplication();
            }
        }

        /// <summary>
        /// Generates an HttpClient object.
        /// </summary>
        /// <returns>An HttpClient.</returns>
        private HttpClient GetHttpClient()
        {
            HttpClient client = _httpClientFactory.CreateClient(HttpClientName);
            client.BaseAddress = new Uri(Config.Url);
            client.Timeout = TimeSpan.FromSeconds(Config.SynologyTimeoutSeconds);
            return client;
        }
    }
}
