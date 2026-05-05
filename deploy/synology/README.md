# Synology Deployment

This directory contains a Docker Compose deployment for running
SynoAI-Telegram on a Synology NAS with Surveillance Station, CodeProject.AI
object detection, and Telegram notifications.

The original SynoAI project was archived on April 11, 2025. SynoAI-Telegram is
a maintained fork that targets .NET 8 and a NAS deployment that is easy to
reproduce without committing local secrets.

## 1. Prepare NAS Directories

Connect to the NAS over SSH and create the persistent directories:

```sh
mkdir -p /volume1/docker/synoai/deploy/synology
mkdir -p /volume1/docker/synoai/captures
mkdir -p /volume1/docker/codeproject/data
mkdir -p /volume1/docker/codeproject/modules
```

Copy `docker-compose.yml` from this directory to the NAS, for example:

```text
/volume1/docker/synoai/deploy/synology/docker-compose.yml
```

The compose file pulls the SynoAI-Telegram image from GitHub Container Registry,
so the full repository does not need to be copied to the NAS.

Copy `appsettings.example.json` to:

```text
/volume1/docker/synoai/appsettings.json
```

Then edit `/volume1/docker/synoai/appsettings.json` on the NAS.

The image used by default is:

```text
ghcr.io/gajack33/synoai-telegram:latest
```

If the GitHub package is private, log in once from the NAS with a GitHub
personal access token that has `read:packages`:

```sh
echo "GITHUB_TOKEN_WITH_READ_PACKAGES" | docker login ghcr.io -u gajack33 --password-stdin
```

## 2. Configure SynoAI-Telegram

Replace these placeholders:

- `NAS_IP`: local IP address of the NAS, for example `192.168.1.20`.
- `SURVEILLANCE_STATION_USER`: DSM or Surveillance Station user allowed to read
  cameras and recordings.
- `SURVEILLANCE_STATION_PASSWORD`: password for that user.
- `CHANGE_ME_LONG_RANDOM_TOKEN`: long random token used to protect
  SynoAI-Telegram endpoints.
- `TELEGRAM_CHAT_ID`: Telegram chat ID.
- `TELEGRAM_BOT_TOKEN`: token created by BotFather.
- `Language`: Telegram notification language. Keep `en` for English or set
  `fr` for French.
- `CAMERA_NAME_EXACTLY_AS_IN_SURVEILLANCE_STATION`: exact camera name from
  Surveillance Station.
- `CameraMessageThreadIDs`: optional Telegram forum topic IDs per camera.

The compose file uses `TZ=UTC` by default. Change it to your IANA timezone, for
example `Europe/Paris`, if you want container timestamps to match local time.

For DSM over HTTPS, use:

```json
"Url": "https://NAS_IP:5001",
"AllowInsecureUrl": true
```

Only set `AllowInsecureUrl` to `true` when the container cannot validate your DSM
HTTPS certificate.

Telegram is the only notifier supported by this fork. Keep `PhotoBaseURL` empty
for the normal NAS setup; SynoAI-Telegram will upload the processed image
directly to Telegram. Only set `PhotoBaseURL` if SynoAI-Telegram is exposed
through a public URL that Telegram can fetch, because Telegram must be able to
reach `/Image/...` from the internet.

Set `SendRecordingClip` to `true` only after photo notifications are working.
`RecordingClipOffsetMs` is applied relative to the snapshot where SynoAI detected
the object when Synology exposes or encodes a recording start time. Use a
negative value such as `-5000` to start the clip five seconds before detection.
`RecordingClipDurationMs` supports clips up to `120000` milliseconds. Start at
60000 milliseconds, then increase if people are still visible at the end of the
clip. Longer clips may require higher `SynologyTimeoutSeconds` and
`TelegramTimeoutSeconds`.

If Telegram receives clips of only a few seconds, the clip is probably being
downloaded while Surveillance Station is still writing the recording. Increase
`RecordingClipDownloadDelayMs` so SynoAI-Telegram waits after sending the photo
before it downloads and sends the video. For one camera, setting it close to
`RecordingClipDurationMs` gives Surveillance Station time to make the requested
duration available.

`MaxSnapshotBytes`, `MaxAIResponseBytes`, and `MaxRecordingClipBytes` bound
untrusted Synology and AI responses before they are decoded, parsed, or written
to disk. Keep the defaults unless your cameras legitimately produce larger
files.

## 3. AI Settings

The provided compose file runs CodeProject.AI Server internally as
`http://codeproject-ai:32168`.

Recommended default:

```json
"AI": {
  "Type": "CodeProjectAIServer",
  "Url": "http://codeproject-ai:32168",
  "Path": "v1/vision/custom/ipcam-general"
}
```

`AI:Path` and `AI:FaceRecognitionPath` must be relative paths. Do not set them
to full URLs; use `AI:Url` for the detector host.

Useful tuning options:

- `AI:DetectionMode`: `ObjectDetection` for normal object labels, or
  `FaceRecognition` to call `AI:FaceRecognitionPath`.
- `AI:FaceLabels`: optional mapping from returned face user ids to display
  names used in camera `Types` and Telegram captions.
- `AI:TimeoutSeconds`: timeout for AI requests.
- `AI:FailureDelayMs`: delay after AI failure before accepting another camera
  trigger.
- `AI:MaxImageWidth`: maximum image width sent to the AI. SynoAI-Telegram scales
  detections back to the original image before annotation.
- `AI:JpegQuality`: JPEG quality for the resized AI input.
- `AI:WarmupEnabled`: keeps the first real motion event from paying the model
  startup cost.
- `MaxAIResponseBytes` and `MaxRecordingClipBytes`: cap unexpectedly large AI
  responses and video downloads. Set either to `0` only if you intentionally
  want no size limit.

If the custom IP camera model does not work well for your scene, try
`v1/vision/detection` and compare the logs.

## 4. Start The Containers

From the directory containing the copied `docker-compose.yml`, for example
`/volume1/docker/synoai/deploy/synology`:

```sh
docker compose pull
docker compose up -d
```

If your NAS still uses the legacy Compose binary:

```sh
docker-compose pull
docker-compose up -d
```

Exposed port:

- SynoAI-Telegram: `http://NAS_IP:8080`

CodeProject.AI is not published to the NAS network by default. SynoAI-Telegram
reaches it over the Docker network at `http://codeproject-ai:32168`.

To update SynoAI-Telegram after a new image has been published:

```sh
docker compose pull synoai-telegram
docker compose up -d synoai-telegram
```

## 5. Create The Surveillance Station Action Rule

In Surveillance Station:

1. Open **Action Rules**.
2. Create a new rule.
3. Event:
   - Source: Camera
   - Device: your camera
   - Event: Motion Detected
4. Action:
   - Action device: Webhook
   - URL: `http://NAS_IP:8080/Camera/CAMERA_NAME?token=CHANGE_ME_LONG_RANDOM_TOKEN`
   - Method: `GET`
   - Username: empty
   - Password: empty
   - Times: `1`

The camera name in the URL must match the `Name` value in `appsettings.json`.

## 6. Test

Check CodeProject.AI from the SynoAI-Telegram container:

```sh
docker exec synoai-telegram curl --fail \
  http://codeproject-ai:32168/v1/status/ping
```

Check available custom models:

```sh
docker exec synoai-telegram curl -s -X POST \
  http://codeproject-ai:32168/v1/vision/custom/list
```

Trigger SynoAI-Telegram manually:

```sh
curl "http://NAS_IP:8080/Camera/CAMERA_NAME?token=CHANGE_ME_LONG_RANDOM_TOKEN"
```

A positive detection should send a Telegram photo. If `SendRecordingClip` is
enabled, SynoAI-Telegram sends a recording clip afterward.

## 7. Tuning

- Too many alerts: increase `Threshold`, `MinSizeX`, `MinSizeY`, or
  `DelayAfterSuccess`.
- Repeated identical alerts: set `DuplicateSnapshotIgnoreSeconds` or
  `StationaryObjectIgnoreSeconds`.
- Better snapshot selection: enable `PerfectShotEnabled` with `MaxSnapshots`
  greater than `1`.
- Large capture folders: set `CapturePathPattern` to `{camera}/{yyyy}/{MM}/{dd}`.
- Missed distant people: reduce `MinSizeX` and `MinSizeY`, or increase
  `AI:MaxImageWidth`.
- Snapshot too early or late: adjust per-camera `Wait`.
- Best Telegram image quality: keep `OutputJpegQuality` at `100`. If uploads
  are too slow, reduce it, lower `RecordingClipDurationMs`, or increase
  `TelegramTimeoutSeconds`.
- Video misses the detected person: set `RecordingClipOffsetMs` to a negative
  value such as `-5000` and keep `RecordingClipDownloadDelayMs` high enough for
  Surveillance Station to finish writing the relevant segment.
- Debug exclusion zones: temporarily enable `DrawExclusions`.
- Keep `PhotoBaseURL` empty for Telegram; SynoAI-Telegram uploads the photo
  directly.

## 8. Telegram Translations

Telegram captions use English by default:

```json
"Language": "en"
```

French is included:

```json
"Language": "fr"
```

The translation catalog is stored in
`SynoAI/Notifiers/Telegram/telegram-translations.json`. To add another language,
add a new top-level language key in that file and set the Telegram notifier
`Language` value to the same key.
