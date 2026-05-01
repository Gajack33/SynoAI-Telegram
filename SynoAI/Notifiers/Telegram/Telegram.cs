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
    public class Telegram : NotifierBase, IRecordingClipNotifier
    {
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
        /// Offset in milliseconds from the start of the latest recording.
        /// </summary>
        public int RecordingClipOffsetMs { get; set; }
        /// <summary>
        /// Clip duration in milliseconds.
        /// </summary>
        public int RecordingClipDurationMs { get; set; }

        /// <summary>
        /// Sends a message and an image using the Telegram API.
        /// </summary>
        /// <param name="camera">The camera that triggered the notification.</param>
        /// <param name="notification">The notification data to process.</param>
        /// <param name="logger">A logger.</param>
        public override async Task SendAsync(Camera camera, Notification notification, ILogger logger)
        {
            using (logger.BeginScope("Telegram"))
            {
                // Assign camera name to variable for logger placeholder
                string cameraName = camera.Name;
                ProcessedImage processedImage = notification.ProcessedImage;

                try
                {
                    string message = GetTelegramMessage(camera, notification);
                    int? messageThreadId = GetMessageThreadId(camera);
                    await SendPhotoAsync(camera, processedImage, message, messageThreadId, logger);

                    logger.LogInformation("{cameraName}: Telegram notification sent successfully", cameraName);
                }
                catch (Exception ex)
                {
                    logger.LogError("{cameraName}: Error occurred sending telegram", cameraName);
                    logger.LogError(ex, "An exception occurred");
                }
            }
        }

        public async Task SendRecordingClipAsync(Camera camera, Notification notification, ILogger logger)
        {
            if (!SendRecordingClip || notification.RecordingClip == null)
            {
                return;
            }

            try
            {
                await SendRecordingClipAsync(camera, notification.RecordingClip, GetMessageThreadId(camera), logger);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{cameraName}: Telegram photo was sent, but the recording clip could not be sent.", camera.Name);
            }
        }

        private async Task SendRecordingClipAsync(Camera camera, ProcessedFile recordingClip, int? messageThreadId, ILogger logger)
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
            }, logger);
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

        private async Task SendPhotoAsync(Camera camera, ProcessedImage processedImage, string message, int? messageThreadId, ILogger logger)
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
                    string photoUrl = new Uri(
                        new Uri(PhotoBaseURL.TrimEnd('/') + "/"),
                        $"Image/{Uri.EscapeDataString(camera.Name)}/{Uri.EscapeDataString(processedImage.FileName)}").ToString();
                    form.Add(new StringContent(RequestAuthorization.AppendToken(photoUrl)), "photo");
                }

                return form;
            }, logger);
        }

        private static async Task PostTelegramFormAsync(string url, Func<MultipartFormDataContent> createForm, ILogger logger)
        {
            int maxAttempts = Config.HttpRetryCount + 1;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    using MultipartFormDataContent form = createForm();
                    using CancellationTokenSource cancellationTokenSource = new(TimeSpan.FromSeconds(Config.TelegramTimeoutSeconds));
                    using HttpResponseMessage response = await SynoAI.App.Shared.HttpClient.PostAsync(new Uri(url), form, cancellationTokenSource.Token);
                    string responseContent = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        logger.LogError("Telegram responded with HTTP status code '{statusCode}': {response}", response.StatusCode, responseContent);
                        if (ShouldRetry(response.StatusCode, attempt, maxAttempts))
                        {
                            await DelayBeforeRetry(logger, attempt, maxAttempts);
                            continue;
                        }

                        throw new HttpRequestException($"Telegram responded with HTTP status code '{response.StatusCode}'.");
                    }

                    JObject responseJson = JsonConvert.DeserializeObject<JObject>(responseContent);
                    if (responseJson?["ok"]?.Value<bool>() == true)
                    {
                        return;
                    }

                    string description = responseJson?["description"]?.Value<string>() ?? "Unknown Telegram error";
                    logger.LogError("Telegram API returned an error: {description}", description);
                    if (IsRetryableTelegramError(description) && attempt < maxAttempts)
                    {
                        await DelayBeforeRetry(logger, attempt, maxAttempts);
                        continue;
                    }

                    throw new InvalidOperationException(description);
                }
                catch (TaskCanceledException ex) when (attempt < maxAttempts)
                {
                    logger.LogWarning(ex, "Telegram request timed out on attempt {attempt} of {maxAttempts}.", attempt, maxAttempts);
                    await DelayBeforeRetry(logger, attempt, maxAttempts);
                }
                catch (HttpRequestException ex) when (attempt < maxAttempts)
                {
                    logger.LogWarning(ex, "Telegram request failed on attempt {attempt} of {maxAttempts}.", attempt, maxAttempts);
                    await DelayBeforeRetry(logger, attempt, maxAttempts);
                }
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

        private static async Task DelayBeforeRetry(ILogger logger, int attempt, int maxAttempts)
        {
            int delayMs = Config.HttpRetryDelayMs * attempt;
            if (delayMs <= 0)
            {
                return;
            }

            logger.LogInformation(
                "Retrying Telegram transient failure after {delayMs}ms ({nextAttempt}/{maxAttempts}).",
                delayMs,
                attempt + 1,
                maxAttempts);
            await Task.Delay(delayMs);
        }
    }
}
