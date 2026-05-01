using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;

namespace SynoAI.Notifiers.Telegram
{
    public class TelegramFactory : NotifierFactory
    {
        internal const int DefaultRecordingClipDurationMs = 60000;
        internal const int MaxRecordingClipDurationMs = 120000;
        internal const int DefaultRecordingClipDownloadDelayMs = 30000;
        internal const int MaxRecordingClipDownloadDelayMs = 300000;

        public override INotifier Create(ILogger logger, IConfigurationSection section)
        {
            using (logger.BeginScope(nameof(TelegramFactory)))
            {
                logger.LogInformation("Processing Telegram Config");

                string token = section.GetValue<string>("Token");
                string chatId = section.GetValue<string>("ChatID");
                string photoBaseURL = section.GetValue<string>("PhotoBaseURL");
                string language = section.GetValue<string>("Language", "en");
                int? messageThreadId = section.GetValue<int?>("MessageThreadID");
                Dictionary<string, int> cameraMessageThreadIds = section
                    .GetSection("CameraMessageThreadIDs")
                    .Get<Dictionary<string, int>>();
                bool sendRecordingClip = section.GetValue<bool>("SendRecordingClip", false);
                int configuredRecordingClipDownloadDelayMs = section.GetValue<int>("RecordingClipDownloadDelayMs", DefaultRecordingClipDownloadDelayMs);
                int recordingClipDownloadDelayMs = Math.Clamp(
                    configuredRecordingClipDownloadDelayMs,
                    0,
                    MaxRecordingClipDownloadDelayMs);
                if (configuredRecordingClipDownloadDelayMs > MaxRecordingClipDownloadDelayMs)
                {
                    logger.LogWarning(
                        "Telegram RecordingClipDownloadDelayMs was capped from {configuredDelayMs}ms to {maxDelayMs}ms.",
                        configuredRecordingClipDownloadDelayMs,
                        MaxRecordingClipDownloadDelayMs);
                }

                int recordingClipOffsetMs = section.GetValue<int>("RecordingClipOffsetMs", 0);
                int configuredRecordingClipDurationMs = section.GetValue<int>("RecordingClipDurationMs", DefaultRecordingClipDurationMs);
                int recordingClipDurationMs = Math.Min(configuredRecordingClipDurationMs, MaxRecordingClipDurationMs);
                if (configuredRecordingClipDurationMs > MaxRecordingClipDurationMs)
                {
                    logger.LogWarning(
                        "Telegram RecordingClipDurationMs was capped from {configuredDurationMs}ms to {maxDurationMs}ms.",
                        configuredRecordingClipDurationMs,
                        MaxRecordingClipDurationMs);
                }

                return new Telegram()
                {
                    ChatID = chatId,
                    Token = token,
                    PhotoBaseURL = photoBaseURL,
                    Language = language,
                    MessageThreadID = messageThreadId,
                    CameraMessageThreadIDs = cameraMessageThreadIds,
                    SendRecordingClip = sendRecordingClip,
                    RecordingClipDownloadDelayMs = recordingClipDownloadDelayMs,
                    RecordingClipOffsetMs = recordingClipOffsetMs,
                    RecordingClipDurationMs = recordingClipDurationMs
                };
            }
        }
    }
}
