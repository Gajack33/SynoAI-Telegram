# SynoAI-Telegram

SynoAI-Telegram is a self-hosted notification bridge for Synology Surveillance
Station. It receives motion webhooks, fetches camera snapshots from the
Synology API, runs object detection, and only sends notifications when
configured object types are detected.

SynoAI-Telegram is a maintained fork of the archived
[`djdd87/SynoAI`](https://github.com/djdd87/SynoAI) project. It has been
modernized for .NET 8, Telegram notifications, and a practical Synology Docker
deployment.

## What This Fork Adds

- .NET 8 runtime and Docker image.
- Synology-friendly Docker Compose deployment under `deploy/synology`.
- CodeProject.AI support through the DeepStack-compatible multipart API,
  including optional face recognition routing.
- Optional DeepStack compatibility for existing setups.
- Telegram-only notifications through direct Telegram Bot API calls.
- Telegram forum topic routing globally or per camera.
- Telegram caption translations through `telegram-translations.json`; English is
  the default and French is included.
- Optional recording clip delivery after a positive detection.
- Perfect Shot mode to analyze all configured snapshots and notify the highest
  confidence match.
- Duplicate snapshot and stationary object filters to reduce repeated alerts.
- Date-based capture folders through `CapturePathPattern`.
- Request authorization with `AccessToken`.
- Safer capture file serving to prevent path traversal.
- Health checks for SynoAI-Telegram and CodeProject.AI.
- Focused tests for authorization, capture paths, AI response handling, config,
  and Telegram payloads.

Non-Telegram upstream notifiers have been removed from this fork to keep the
NAS deployment small and focused.

## Runtime Flow

1. Surveillance Station detects motion.
2. An action rule calls `http://NAS_IP:8080/Camera/CAMERA_NAME?token=ACCESS_TOKEN`.
3. SynoAI-Telegram downloads one or more snapshots from Surveillance Station.
4. SynoAI-Telegram sends each snapshot to the configured AI detector.
5. If a configured object type is detected above threshold, SynoAI-Telegram
   saves and annotates the capture.
6. SynoAI-Telegram sends the Telegram photo notification and, when enabled, a
   recording clip from the latest Surveillance Station recording.
7. If no valid object is detected, no notification is sent.

## Requirements

- Synology NAS with Docker or Container Manager.
- Synology Surveillance Station with at least one configured camera.
- CodeProject.AI Server or DeepStack running in Docker.
- .NET 8 SDK for local development.
- Telegram bot and chat/topic IDs if Telegram notifications are used.

## Quick Start On Synology

The Synology deployment files live in [`deploy/synology`](deploy/synology).

1. Copy [`deploy/synology/appsettings.example.json`](deploy/synology/appsettings.example.json)
   to `/volume1/docker/synoai-telegram/appsettings.json` on the NAS.
2. Edit the copied file with your NAS URL, Surveillance Station credentials,
   access token, camera names, AI URL, and Telegram settings.
3. From `deploy/synology`, run:

```sh
docker compose up -d --build
```

4. Create a Surveillance Station action rule that calls:

```text
http://NAS_IP:8080/Camera/CAMERA_NAME?token=ACCESS_TOKEN
```

For the full NAS setup guide, see
[`deploy/synology/README.md`](deploy/synology/README.md).

## Local Development

Restore, build, and test:

```powershell
Copy-Item .\SynoAI\appsettings.example.json .\SynoAI\appsettings.json
dotnet restore SynoAI.sln
dotnet build SynoAI.sln --no-restore
dotnet test SynoAI.sln --no-build
```

Check vulnerable packages:

```powershell
dotnet list SynoAI\SynoAI.csproj package --vulnerable --include-transitive
```

Build the Docker image:

```powershell
docker build -t synoai-telegram:local .\SynoAI
```

Validate the Synology compose file:

```powershell
docker compose -f .\deploy\synology\docker-compose.yml config
```

## Configuration Notes

Important settings are configured in `appsettings.json`:

- `Url`: Synology DSM/Surveillance Station URL, for example `http://NAS_IP:5000`
  or `https://NAS_IP:5001`.
- `User` / `Password`: account allowed to read Surveillance Station cameras and
  recordings.
- `AccessToken`: shared token required by incoming SynoAI-Telegram endpoints.
- `AI:Type`: `CodeProjectAIServer` or `DeepStack`.
- `AI:Url`: detector base URL from inside the SynoAI-Telegram container.
- `AI:Path`: detector route, such as `v1/vision/custom/ipcam-general` for
  CodeProject.AI. This must be a relative path, not a full URL.
- `AI:DetectionMode`: `ObjectDetection` or `FaceRecognition`. Face recognition
  uses `AI:FaceRecognitionPath` and can map returned user ids through
  `AI:FaceLabels`.
- `OutputJpegQuality`: JPEG quality used for saved/Telegram images. Keep `100`
  for best Telegram input quality.
- `MaxAIResponseBytes` / `MaxRecordingClipBytes`: safety limits for detector
  JSON responses and downloaded recording clips. Set to `0` to disable.
- `CapturePathPattern`: capture directory pattern. The default is `{camera}`;
  use `{camera}/{yyyy}/{MM}/{dd}` to keep folders small.
- `PerfectShotEnabled`: when `true`, SynoAI-Telegram evaluates every
  `MaxSnapshots` attempt and sends the highest-confidence valid snapshot.
- `DuplicateSnapshotIgnoreSeconds`: ignores identical snapshot bytes within the
  configured window. `0` disables this filter.
- `StationaryObjectIgnoreSeconds`: ignores detections matching recently
  notified objects in nearly the same position. `0` disables this filter.
- `MaxSnapshotBytes`, `MaxAIResponseBytes`, and `MaxRecordingClipBytes`: size
  limits for untrusted Synology/AI responses. Set to `0` only if you explicitly
  want to disable a limit.
- `Cameras`: camera names and detection thresholds.
- `Notifiers`: Telegram notification settings.
- `Language`: optional Telegram notifier language. The default is `en`; use
  `fr` for French captions.
- `SendRecordingClip`: enables optional Telegram video clip delivery.

Example Telegram notifier:

```json
{
  "Type": "Telegram",
  "ChatID": "TELEGRAM_CHAT_ID",
  "Token": "TELEGRAM_BOT_TOKEN",
  "Language": "en",
  "PhotoBaseURL": "",
  "MessageThreadID": null,
  "CameraMessageThreadIDs": {
    "CAMERA_NAME_EXACTLY_AS_IN_SURVEILLANCE_STATION": 123
  },
  "SendRecordingClip": false,
  "RecordingClipDownloadDelayMs": 30000,
  "RecordingClipOffsetMs": -5000,
  "RecordingClipDurationMs": 60000
}
```

`RecordingClipOffsetMs` is applied relative to the snapshot where SynoAI detected
the object when Synology exposes or encodes a recording start time. A negative
value starts the clip before the detection; `-5000` gives five seconds of lead-in.
`RecordingClipDurationMs` supports up to `120000` milliseconds. For cameras where
people are still visible at the end of the clip, increase this value and raise
`SynologyTimeoutSeconds` / `TelegramTimeoutSeconds` if transfers time out. If
Telegram receives very short clips, increase `RecordingClipDownloadDelayMs` so
Surveillance Station has time to write more of the current recording before
SynoAI-Telegram downloads it.

`PhotoBaseURL` should normally stay empty. In that mode SynoAI-Telegram uploads
the processed image directly to Telegram, which works on a private LAN. If you
set `PhotoBaseURL`, Telegram receives a URL such as
`https://example.com/Image/CAMERA/CAPTURE.jpg?token=...`; that URL must be
reachable by Telegram's servers.

Telegram translations live in
`SynoAI/Notifiers/Telegram/telegram-translations.json`. Add a new top-level
language key there, then set the same code in the Telegram notifier `Language`
setting.

Authorized requests can pass the token with:

- Query string: `?token=...`
- Header: `X-SynoAI-Token`
- Bearer token: `Authorization: Bearer ...`

## Security Notes

Use a dedicated Synology account with only the Surveillance Station permissions
needed to read cameras and recordings.

Set a long random `AccessToken` and include it in every Surveillance Station
webhook URL. Keep `PhotoBaseURL` empty unless SynoAI-Telegram is exposed through
a public URL that Telegram can reach.

Configuration templates are provided as `appsettings.example.json`. Copy one to
`appsettings.json` for your own deployment and keep that file private because it
contains NAS credentials and Telegram settings.

## Repository Layout

- `SynoAI/`: main ASP.NET Core application.
- `SynoAI/Controllers/CameraController.cs`: motion trigger workflow.
- `SynoAI/Services/SynologyService.cs`: Synology API login, snapshots, and
  recording clips.
- `SynoAI/Services/SnapshotManager.cs`: image saving and annotation.
- `SynoAI/Notifiers/Telegram/`: Telegram notification implementation.
- `SynoAI.Tests/`: automated tests.
- `deploy/synology/`: Synology Docker Compose deployment.

## License

This fork keeps the original project license. See [`LICENSE`](LICENSE).
