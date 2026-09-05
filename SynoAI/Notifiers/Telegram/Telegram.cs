using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SynoAI.App;
using SynoAI.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SynoAI.Notifiers.Telegram
{
    /// <summary>
    /// Calls a third party API.
    /// </summary>
    public class Telegram : NotifierBase, IRecordingClipNotifier, ICameraStatusNotifier
    {
        private readonly IHttpClient _httpClient;
        private long _retryNotBeforeTicks;

        public Telegram()
            : this(new HttpClientWrapper())
        {
        }

        public Telegram(IHttpClient httpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        /// <summary>
        /// The ID of the chat to send notifications to
        /// </summary>
        public string ChatID { get; set; }
        /// <summary>
        /// The token used to authenticate to Telegram
        /// </summary>
        public string Token { get; set; }
        /// <summary>
        /// Photo base URL
        /// </summary>
        public string PhotoBaseURL { get; set; }
        /// <summary>
        /// Default Telegram forum topic ID to send notifications to.
        /// </summary>
        public int? MessageThreadID { get; set; }
        /// <summary>
        /// Telegram forum topic IDs keyed by camera name.
        /// </summary>
        public IDictionary<string, int> CameraMessageThreadIDs { get; set; }
        /// <summary>
        /// Language code used for Telegram captions. Defaults to English.
        /// </summary>
        public string Language { get; set; } = "en";
        /// <summary>
        /// Whether to send a short Surveillance Station recording clip after the photo.
        /// </summary>
        public bool SendRecordingClip { get; set; }
        /// <summary>
        /// Delay in milliseconds before downloading the clip, allowing in-progress recordings to grow.
        /// </summary>
        public int RecordingClipDownloadDelayMs { get; set; }
        /// <summary>
        /// Offset in milliseconds relative to the detected snapshot when recording timestamps are available.
        /// </summary>
        public int RecordingClipOffsetMs { get; set; }
        /// <summary>
        /// Clip duration in milliseconds.
        /// </summary>
        public int RecordingClipDurationMs { get; set; }
        /// <summary>
        /// Whether camera online/offline transitions should be sent to this Telegram destination.
        /// </summary>
        public bool SendCameraStatusNotifications { get; set; } = true;

        /// <summary>
        /// Sends a message and an image using the Telegram API.
        /// </summary>
        /// <param name="camera">The camera that triggered the notification.</param>
        /// <param name="notification">The notification data to process.</param>
        /// <param name="logger">A logger.</param>
        public override async Task SendAsync(Camera camera, Notification notification, ILogger logger, CancellationToken cancellationToken = default)
        {
            using (logger.BeginScope("Telegram"))
            {
                // Assign camera name to variable for logger placeholder
                string cameraName = camera.Name;
                ProcessedImage processedImage = notification.ProcessedImage;

                string message = GetTelegramMessage(camera, notification);
                int? messageThreadId = GetMessageThreadId(camera);
                await SendPhotoAsync(camera, processedImage, message, messageThreadId, logger, cancellationToken);

                logger.LogInformation("{cameraName}: Telegram notification sent successfully", cameraName);
            }
        }

        public async Task SendRecordingClipAsync(Camera camera, Notification notification, ILogger logger, CancellationToken cancellationToken = default)
        {
            if (!SendRecordingClip || notification.RecordingClip == null)
            {
                return;
            }

            try
            {
                await SendRecordingClipAsync(camera, notification.RecordingClip, GetMessageThreadId(camera), logger, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(ex, "{cameraName}: Telegram photo was sent, but the recording clip could not be sent.", camera.Name);
                throw;
            }
        }

        public async Task SendCameraStatusAsync(
            Camera camera,
            bool isOnline,
            DateTimeOffset changedAt,
            ILogger logger,
            CancellationToken cancellationToken = default)
        {
            if (!SendCameraStatusNotifications)
            {
                return;
            }

            TelegramTranslation translation = TelegramTranslationCatalog.Get(Language);
            string title = isOnline ? translation.CameraOnlineTitle : translation.CameraOfflineTitle;
            string message = string.Join(
                Environment.NewLine,
                translation.Format(title, camera),
                $"{translation.TimeLabel}: {changedAt.ToLocalTime().ToString("g", translation.GetCulture())}");
            int? messageThreadId = GetMessageThreadId(camera);
            string url = $"https://api.telegram.org/bot{Token}/sendMessage";

            await PostTelegramFormAsync(url, () =>
            {
                MultipartFormDataContent form = new()
                {
                    { new StringContent(ChatID), "chat_id" },
                    { new StringContent(message), "text" }
                };

                if (messageThreadId.HasValue)
                {
                    form.Add(new StringContent(messageThreadId.Value.ToString()), "message_thread_id");
                }

                return form;
            }, logger, cancellationToken);

            logger.LogInformation(
                "{cameraName}: Telegram camera {status} notification sent successfully",
                camera.Name,
                isOnline ? "online" : "offline");
        }

        private async Task SendRecordingClipAsync(Camera camera, ProcessedFile recordingClip, int? messageThreadId, ILogger logger, CancellationToken cancellationToken)
        {
            if (!SendRecordingClip || recordingClip == null)
            {
                return;
            }

            string url = $"https://api.telegram.org/bot{Token}/sendVideo";
            await PostTelegramFormAsync(url, () =>
            {
                TelegramTranslation translation = TelegramTranslationCatalog.Get(Language);
                MultipartFormDataContent form = new()
                {
                    { new StringContent(ChatID), "chat_id" },
                    { new StringContent(translation.Format(translation.VideoCaption, camera)), "caption" }
                };

                if (messageThreadId.HasValue)
                {
                    form.Add(new StringContent(messageThreadId.Value.ToString()), "message_thread_id");
                }

                FileStream fileStream = recordingClip.GetReadonlyStream();
                StreamContent videoContent = new(fileStream);
                videoContent.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
                form.Add(videoContent, "video", recordingClip.FileName);

                return form;
            }, logger, cancellationToken);
        }

        private string GetTelegramMessage(Camera camera, Notification notification)
        {
            TelegramTranslation translation = TelegramTranslationCatalog.Get(Language);
            List<AIPrediction> predictions = notification.ValidPredictions?.ToList() ?? new List<AIPrediction>();
            StringBuilder message = new();

            message.AppendLine(translation.Format(translation.PhotoCaptionTitle, camera));
            message.AppendLine($"{translation.TimeLabel}: {notification.CreatedAt.ToString("g", translation.GetCulture())}");
            message.AppendLine();
            message.AppendLine($"{translation.DetectionLabel}: {FormatDetectionSummary(predictions, translation)}");

            return message.ToString().Trim();
        }

        private static string FormatDetectionSummary(IEnumerable<AIPrediction> predictions, TelegramTranslation translation)
        {
            List<IGrouping<string, AIPrediction>> groups = predictions
                .Where(x => !string.IsNullOrWhiteSpace(x.Label))
                .GroupBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(x => x.Count())
                .ThenBy(x => x.Key)
                .ToList();

            if (groups.Count == 0)
            {
                return translation.DefaultObject;
            }

            return string.Join(", ", groups.Select(x => FormatDetectedLabel(x.Key, x.Count(), translation)));
        }

        private static string FormatDetectedLabel(string label, int count, TelegramTranslation translation)
        {
            (string singular, string plural) = TranslateLabel(label, translation);
            return count == 1 ? singular : $"{count} {plural.ToLowerInvariant()}";
        }

        private static (string Singular, string Plural) TranslateLabel(string label, TelegramTranslation translation)
        {
            string key = label?.ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(key) &&
                translation.Labels != null &&
                translation.Labels.TryGetValue(key, out TelegramLabelTranslation labelTranslation) &&
                !string.IsNullOrWhiteSpace(labelTranslation.Singular) &&
                !string.IsNullOrWhiteSpace(labelTranslation.Plural))
            {
                return (labelTranslation.Singular, labelTranslation.Plural);
            }

            CultureInfo culture = translation.GetCulture();
            string fallbackSingular = culture.TextInfo.ToTitleCase(label?.ToLowerInvariant() ?? translation.DefaultObject.ToLowerInvariant());
            return (fallbackSingular, translation.DefaultObjectPlural);
        }

        private int? GetMessageThreadId(Camera camera)
        {
            if (CameraMessageThreadIDs != null &&
                CameraMessageThreadIDs.TryGetValue(camera.Name, out int cameraMessageThreadId))
            {
                return cameraMessageThreadId;
            }

            return MessageThreadID;
        }

        private async Task SendPhotoAsync(Camera camera, ProcessedImage processedImage, string message, int? messageThreadId, ILogger logger, CancellationToken cancellationToken)
        {
            string url = $"https://api.telegram.org/bot{Token}/sendPhoto";
            await PostTelegramFormAsync(url, () =>
            {
                MultipartFormDataContent form = new()
                {
                    { new StringContent(ChatID), "chat_id" },
                    { new StringContent(message), "caption" }
                };

                if (messageThreadId.HasValue)
                {
                    form.Add(new StringContent(messageThreadId.Value.ToString()), "message_thread_id");
                }

                if (string.IsNullOrWhiteSpace(PhotoBaseURL))
                {
                    FileStream fileStream = processedImage.GetReadonlyStream();
                    StreamContent imageContent = new(fileStream);
                    imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                    form.Add(imageContent, "photo", processedImage.FileName);
                }
                else
                {
                    string relativePath = string.Join(
                        "/",
                        processedImage.RelativePath
                            .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(Uri.EscapeDataString));
                    string photoUrl = new Uri(
                        new Uri(PhotoBaseURL.TrimEnd('/') + "/"),
                        $"Image/{Uri.EscapeDataString(camera.Name)}/{relativePath}").ToString();
                    form.Add(new StringContent(RequestAuthorization.AppendImageToken(photoUrl)), "photo");
                }

                return form;
            }, logger, cancellationToken);
        }

        private async Task PostTelegramFormAsync(string url, Func<MultipartFormDataContent> createForm, ILogger logger, CancellationToken cancellationToken)
        {
            int maxAttempts = Config.HttpRetryCount + 1;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                await WaitForRetryWindowAsync(cancellationToken);
                try
                {
                    using MultipartFormDataContent form = createForm();
                    using CancellationTokenSource cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(Config.TelegramTimeoutSeconds));
                    using HttpResponseMessage response = await _httpClient.PostAsync(new Uri(url), form, cancellationTokenSource.Token);
                    string responseContent = await response.Content.ReadAsStringAsync(cancellationTokenSource.Token);

                    TimeSpan? retryAfter = GetRetryAfter(responseContent);
                    if (retryAfter.HasValue)
                    {
                        RememberRetryWindow(retryAfter.Value);
                    }
                    if (!response.IsSuccessStatusCode)
                    {
                        logger.LogError("Telegram responded with HTTP status code '{statusCode}': {response}", response.StatusCode, responseContent);
                        if (ShouldRetry(response.StatusCode, attempt, maxAttempts))
                        {
                            await DelayBeforeRetry(logger, attempt, maxAttempts, cancellationToken, retryAfter);
                            continue;
                        }

                        throw new TelegramApiException($"Telegram responded with HTTP status code '{response.StatusCode}'.");
                    }

                    JObject responseJson = JsonConvert.DeserializeObject<JObject>(responseContent);
                    if (responseJson?["ok"]?.Value<bool>() == true)
                    {
                        return;
                    }

                    string description = responseJson?["description"]?.Value<string>() ?? "Unknown Telegram error";
                    logger.LogError("Telegram API returned an error: {description}", description);
                    if ((retryAfter.HasValue || IsRetryableTelegramError(description)) && attempt < maxAttempts)
                    {
                        await DelayBeforeRetry(logger, attempt, maxAttempts, cancellationToken, retryAfter);
                        continue;
                    }

                    throw new InvalidOperationException(description);
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && attempt < maxAttempts)
                {
                    logger.LogWarning(ex, "Telegram request timed out on attempt {attempt} of {maxAttempts}.", attempt, maxAttempts);
                    await DelayBeforeRetry(logger, attempt, maxAttempts, cancellationToken);
                }
                catch (HttpRequestException ex) when (ex is not TelegramApiException && !cancellationToken.IsCancellationRequested && attempt < maxAttempts)
                {
                    logger.LogWarning(ex, "Telegram request failed on attempt {attempt} of {maxAttempts}.", attempt, maxAttempts);
                    await DelayBeforeRetry(logger, attempt, maxAttempts, cancellationToken);
                }
            }
        }

        private sealed class TelegramApiException : HttpRequestException
        {
            public TelegramApiException(string message)
                : base(message)
            {
            }
        }

        private static bool ShouldRetry(HttpStatusCode statusCode, int attempt, int maxAttempts)
        {
            int status = (int)statusCode;
            return attempt < maxAttempts && (status == 408 || status == 429 || status >= 500);
        }

        private static bool IsRetryableTelegramError(string description)
        {
            return description != null &&
                   description.IndexOf("retry after", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static TimeSpan? GetRetryAfter(string responseContent)
        {
            try
            {
                JObject response = JObject.Parse(responseContent);
                JToken value = response["parameters"] is JObject parameters ? parameters["retry_after"] : null;
                if (value?.Type == JTokenType.Integer &&
                    int.TryParse(value.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) && seconds >= 0)
                {
                    return TimeSpan.FromSeconds(seconds);
                }
            }
            catch (JsonException)
            {
                // Non-JSON error bodies still use the configured transient-error backoff.
            }
            return null;
        }

        private void RememberRetryWindow(TimeSpan delay)
        {
            long until = DateTime.UtcNow.Add(delay).Ticks;
            long previous;
            do
            {
                previous = Interlocked.Read(ref _retryNotBeforeTicks);
                if (previous >= until) return;
            } while (Interlocked.CompareExchange(ref _retryNotBeforeTicks, until, previous) != previous);
        }

        private async Task WaitForRetryWindowAsync(CancellationToken cancellationToken)
        {
            // Keep server backoff across calls, including when the last allowed attempt was rate-limited.
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TimeSpan remaining = TimeSpan.FromTicks(Interlocked.Read(ref _retryNotBeforeTicks) - DateTime.UtcNow.Ticks);
                if (remaining <= TimeSpan.Zero) return;
                await Task.Delay(remaining > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : remaining, cancellationToken);
            }
        }

        private static async Task DelayBeforeRetry(
            ILogger logger, int attempt, int maxAttempts, CancellationToken cancellationToken,
            TimeSpan? retryAfter = null)
        {
            TimeSpan delay = retryAfter ?? TimeSpan.FromMilliseconds((long)Config.HttpRetryDelayMs * attempt);
            logger.LogInformation(
                "Retrying Telegram transient failure after {delayMs}ms ({nextAttempt}/{maxAttempts}).",
                delay.TotalMilliseconds, attempt + 1, maxAttempts);
            // Split unusually long server delays to stay within Task.Delay's supported range.
            while (delay > TimeSpan.Zero)
            {
                TimeSpan part = delay > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : delay;
                await Task.Delay(part, cancellationToken);
                delay -= part;
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
