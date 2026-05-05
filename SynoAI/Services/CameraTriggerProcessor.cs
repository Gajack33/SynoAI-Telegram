using Microsoft.Extensions.Logging;
using SkiaSharp;
using SynoAI.Models;
using SynoAI.Notifiers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SynoAI.Services
{
    public class CameraTriggerProcessor : ICameraTriggerProcessor
    {
        private readonly IAIService _aiService;
        private readonly ISynologyService _synologyService;
        private readonly ICameraProcessingQueue _cameraQueue;
        private readonly IDetectionMemory _detectionMemory;
        private readonly ILogger<CameraTriggerProcessor> _logger;

        public CameraTriggerProcessor(
            IAIService aiService,
            ISynologyService synologyService,
            ICameraProcessingQueue cameraQueue,
            IDetectionMemory detectionMemory,
            ILogger<CameraTriggerProcessor> logger)
        {
            _aiService = aiService;
            _synologyService = synologyService;
            _cameraQueue = cameraQueue;
            _detectionMemory = detectionMemory;
            _logger = logger;
        }

        public async Task<CameraProcessingStatus> ProcessAsync(string cameraName, CancellationToken cancellationToken)
        {
            Camera camera = Config.Cameras?.FirstOrDefault(x =>
                !string.IsNullOrWhiteSpace(x.Name) &&
                x.Name.Equals(cameraName, StringComparison.OrdinalIgnoreCase));
            if (camera == null)
            {
                _logger.LogError("{cameraName}: The camera was not found when processing queued trigger.", cameraName);
                return CameraProcessingStatus.CameraNotFound;
            }

            try
            {
                if (camera.Wait > 0)
                {
                    _logger.LogInformation("{cameraName}: Waiting for {waitMs}ms before fetching snapshot.", cameraName, camera.Wait);
                    await Task.Delay(camera.Wait, cancellationToken);
                }

                Stopwatch overallStopwatch = Stopwatch.StartNew();
                int maxSnapshots = camera.GetMaxSnapshots();
                List<SnapshotCandidate> perfectShotCandidates = new();
                for (int snapshotCount = 1; snapshotCount <= maxSnapshots; snapshotCount++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    _logger.LogInformation(
                        "{cameraName}: Snapshot {snapshotCount} of {maxSnapshots} requested at EVENT TIME {elapsedMs}ms.",
                        cameraName,
                        snapshotCount,
                        maxSnapshots,
                        overallStopwatch.ElapsedMilliseconds);

                    byte[] snapshot = await GetSnapshot(cameraName);
                    if (snapshot == null)
                    {
                        continue;
                    }

                    _logger.LogInformation(
                        "{cameraName}: Snapshot {snapshotCount} of {maxSnapshots} received at EVENT TIME {elapsedMs}ms.",
                        cameraName,
                        snapshotCount,
                        maxSnapshots,
                        overallStopwatch.ElapsedMilliseconds);
                    DateTimeOffset snapshotCapturedAt = DateTimeOffset.Now;

                    snapshot = PreProcessSnapshot(camera, snapshot);
                    if (snapshot == null)
                    {
                        _logger.LogWarning(
                            "{cameraName}: Snapshot {snapshotCount} of {maxSnapshots} could not be decoded for preprocessing and was skipped.",
                            cameraName,
                            snapshotCount,
                            maxSnapshots);
                        continue;
                    }

                    if (Config.DuplicateSnapshotIgnoreSeconds > 0 &&
                        _detectionMemory.IsDuplicateSnapshot(
                            cameraName,
                            snapshot,
                            TimeSpan.FromSeconds(Config.DuplicateSnapshotIgnoreSeconds)))
                    {
                        _logger.LogInformation(
                            "{cameraName}: Snapshot {snapshotCount} of {maxSnapshots} is identical to a recent snapshot and was skipped.",
                            cameraName,
                            snapshotCount,
                            maxSnapshots);
                        continue;
                    }

                    IEnumerable<AIPrediction> predictions = await GetAIPredications(camera, snapshot);
                    if (predictions == null)
                    {
                        _cameraQueue.AddCameraDelay(cameraName, Config.AIFailureDelayMs);
                        return CameraProcessingStatus.AiProcessingFailed;
                    }

                    List<AIPrediction> predictionList = predictions as List<AIPrediction> ?? predictions.ToList();
                    predictionList = NormalizePredictions(camera, snapshot, predictionList);

                    _logger.LogInformation(
                        "{cameraName}: Snapshot {snapshotCount} of {maxSnapshots} contains {predictionCount} objects at EVENT TIME {elapsedMs}ms.",
                        cameraName,
                        snapshotCount,
                        maxSnapshots,
                        predictionList.Count,
                        overallStopwatch.ElapsedMilliseconds);

                    int minSizeX = camera.GetMinSizeX();
                    int minSizeY = camera.GetMinSizeY();
                    int maxSizeX = camera.GetMaxSizeX();
                    int maxSizeY = camera.GetMaxSizeY();

                    List<AIPrediction> validPredictions = GetValidPredictions(
                        cameraName,
                        camera,
                        overallStopwatch,
                        predictionList,
                        minSizeX,
                        minSizeY,
                        maxSizeX,
                        maxSizeY);

                    if (validPredictions.Count > 0 && Config.StationaryObjectIgnoreSeconds > 0)
                    {
                        int beforeStationaryFilter = validPredictions.Count;
                        validPredictions = _detectionMemory
                            .FilterStationaryPredictions(
                                cameraName,
                                validPredictions,
                                TimeSpan.FromSeconds(Config.StationaryObjectIgnoreSeconds),
                                Config.StationaryObjectMovementThresholdPixels)
                            .ToList();

                        if (validPredictions.Count < beforeStationaryFilter)
                        {
                            _logger.LogInformation(
                                "{cameraName}: Ignored {stationaryCount} stationary object(s) seen within the last {ignoreSeconds} seconds.",
                                cameraName,
                                beforeStationaryFilter - validPredictions.Count,
                                Config.StationaryObjectIgnoreSeconds);
                        }
                    }

                    if (Config.SaveOriginalSnapshot == SaveSnapshotMode.Always ||
                        (Config.SaveOriginalSnapshot == SaveSnapshotMode.WithPredictions && predictionList.Count > 0) ||
                        (Config.SaveOriginalSnapshot == SaveSnapshotMode.WithValidPredictions && validPredictions.Count > 0))
                    {
                        _logger.LogInformation("{cameraName}: Saving original image", cameraName);
                        SnapshotManager.SaveOriginalImage(_logger, camera, snapshot);
                    }

                    if (validPredictions.Count > 0)
                    {
                        SnapshotCandidate candidate = new(
                            snapshotCount,
                            snapshotCapturedAt,
                            snapshot,
                            predictionList,
                            validPredictions,
                            CalculateSnapshotScore(validPredictions));

                        if (Config.PerfectShotEnabled && snapshotCount < maxSnapshots)
                        {
                            perfectShotCandidates.Add(candidate);
                            _logger.LogInformation(
                                "{cameraName}: Snapshot {snapshotCount} of {maxSnapshots} is a Perfect Shot candidate with score {score}.",
                                cameraName,
                                snapshotCount,
                                maxSnapshots,
                                candidate.Score);
                            continue;
                        }

                        if (Config.PerfectShotEnabled)
                        {
                            perfectShotCandidates.Add(candidate);
                            candidate = SelectPerfectShot(perfectShotCandidates);
                            _logger.LogInformation(
                                "{cameraName}: Selected snapshot {snapshotCount} as Perfect Shot from {candidateCount} candidate(s) with score {score}.",
                                cameraName,
                                candidate.SnapshotCount,
                                perfectShotCandidates.Count,
                                candidate.Score);
                        }

                        CameraProcessingStatus status = await SendValidSnapshotAsync(camera, candidate, cancellationToken);
                        if (status != CameraProcessingStatus.ValidObjectDetected)
                        {
                            return status;
                        }

                        _logger.LogInformation(
                            "{cameraName}: Valid object found in snapshot {snapshotCount} of {maxSnapshots} at EVENT TIME {elapsedMs}ms.",
                            cameraName,
                            candidate.SnapshotCount,
                            maxSnapshots,
                            overallStopwatch.ElapsedMilliseconds);

                        _cameraQueue.AddCameraDelay(cameraName, camera.GetDelayAfterSuccess());
                        return CameraProcessingStatus.ValidObjectDetected;
                    }

                    if (predictionList.Count > 0)
                    {
                        _logger.LogInformation("{cameraName}: No valid objects at EVENT TIME {elapsedMs}ms.", cameraName, overallStopwatch.ElapsedMilliseconds);
                    }
                    else
                    {
                        _logger.LogInformation("{cameraName}: Nothing detected by the AI at EVENT TIME {elapsedMs}ms.", cameraName, overallStopwatch.ElapsedMilliseconds);

                        StringBuilder nothingFoundOutput = new($"{cameraName}: No objects ");
                        if (camera.Types != null && camera.Types.Any())
                        {
                            nothingFoundOutput.Append($"in the specified list ({string.Join(", ", camera.Types)}) ");
                        }
                        nothingFoundOutput.Append($"were detected by the AI exceeding the confidence level ({camera.Threshold}%) and/or minimum size ({minSizeX}x{minSizeY} and/or maximum size ({maxSizeX},{maxSizeY}))");

                        _logger.LogDebug(nothingFoundOutput.ToString());
                    }

                    _logger.LogInformation("{cameraName}: Finished snapshot attempt ({elapsedMs}ms).", cameraName, overallStopwatch.ElapsedMilliseconds);
                }

                if (Config.PerfectShotEnabled && perfectShotCandidates.Count > 0)
                {
                    SnapshotCandidate candidate = SelectPerfectShot(perfectShotCandidates);
                    _logger.LogInformation(
                        "{cameraName}: Selected snapshot {snapshotCount} as Perfect Shot from {candidateCount} candidate(s) with score {score}.",
                        cameraName,
                        candidate.SnapshotCount,
                        perfectShotCandidates.Count,
                        candidate.Score);

                    CameraProcessingStatus status = await SendValidSnapshotAsync(camera, candidate, cancellationToken);
                    if (status != CameraProcessingStatus.ValidObjectDetected)
                    {
                        return status;
                    }

                    _logger.LogInformation(
                        "{cameraName}: Valid object found in snapshot {snapshotCount} of {maxSnapshots} at EVENT TIME {elapsedMs}ms.",
                        cameraName,
                        candidate.SnapshotCount,
                        maxSnapshots,
                        overallStopwatch.ElapsedMilliseconds);

                    _cameraQueue.AddCameraDelay(cameraName, camera.GetDelayAfterSuccess());
                    return CameraProcessingStatus.ValidObjectDetected;
                }

                _cameraQueue.AddCameraDelay(cameraName, camera.GetDelay());
                return CameraProcessingStatus.NoValidObjectDetected;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("{cameraName}: Camera trigger processing was cancelled.", cameraName);
                return CameraProcessingStatus.ProcessingFailed;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{cameraName}: Unhandled error while processing camera trigger.", cameraName);
                _cameraQueue.AddCameraDelay(cameraName, Config.AIFailureDelayMs);
                return CameraProcessingStatus.ProcessingFailed;
            }
        }

        private List<AIPrediction> GetValidPredictions(
            string cameraName,
            Camera camera,
            Stopwatch overallStopwatch,
            IEnumerable<AIPrediction> predictions,
            int minSizeX,
            int minSizeY,
            int maxSizeX,
            int maxSizeY)
        {
            List<AIPrediction> validPredictions = new();
            foreach (AIPrediction prediction in predictions)
            {
                if (camera.Types != null && !camera.Types.Contains(prediction.Label, StringComparer.OrdinalIgnoreCase))
                {
                    _logger.LogDebug(
                        "{cameraName}: Ignored '{label}' ([{minX},{minY}],[{maxX},{maxY}]) as it is not in the valid type list ({types}) at EVENT TIME {elapsedMs}ms.",
                        cameraName,
                        prediction.Label,
                        prediction.MinX,
                        prediction.MinY,
                        prediction.MaxX,
                        prediction.MaxY,
                        string.Join(",", camera.Types),
                        overallStopwatch.ElapsedMilliseconds);
                }
                else if (prediction.SizeX < minSizeX || prediction.SizeY < minSizeY)
                {
                    _logger.LogDebug(
                        "{cameraName}: Ignored '{label}' ([{minX},{minY}],[{maxX},{maxY}]) as it is under the minimum specified size ({minSizeX}x{minSizeY}) at EVENT TIME {elapsedMs}ms.",
                        cameraName,
                        prediction.Label,
                        prediction.MinX,
                        prediction.MinY,
                        prediction.MaxX,
                        prediction.MaxY,
                        minSizeX,
                        minSizeY,
                        overallStopwatch.ElapsedMilliseconds);
                }
                else if (prediction.SizeX > maxSizeX || prediction.SizeY > maxSizeY)
                {
                    _logger.LogDebug(
                        "{cameraName}: Ignored '{label}' ([{minX},{minY}],[{maxX},{maxY}]) as it exceeds the maximum specified size ({maxSizeX}x{maxSizeY}) at EVENT TIME {elapsedMs}ms.",
                        cameraName,
                        prediction.Label,
                        prediction.MinX,
                        prediction.MinY,
                        prediction.MaxX,
                        prediction.MaxY,
                        maxSizeX,
                        maxSizeY,
                        overallStopwatch.ElapsedMilliseconds);
                }
                else if (ShouldIncludePrediction(cameraName, camera, overallStopwatch, prediction))
                {
                    validPredictions.Add(prediction);
                    _logger.LogDebug(
                        "{cameraName}: Found valid prediction '{label}' ([{minX},{minY}],[{maxX},{maxY}]) at EVENT TIME {elapsedMs}ms.",
                        cameraName,
                        prediction.Label,
                        prediction.MinX,
                        prediction.MinY,
                        prediction.MaxX,
                        prediction.MaxY,
                        overallStopwatch.ElapsedMilliseconds);
                }
            }

            return validPredictions;
        }

        private List<AIPrediction> NormalizePredictions(Camera camera, byte[] snapshot, IEnumerable<AIPrediction> predictions)
        {
            List<AIPrediction> predictionList = predictions?.ToList() ?? new List<AIPrediction>();
            if (predictionList.Count == 0)
            {
                return predictionList;
            }

            using SKBitmap image = DecodeBitmap(snapshot);
            if (image == null)
            {
                _logger.LogWarning("{cameraName}: Could not decode snapshot dimensions before filtering AI predictions.", camera.Name);
                return predictionList;
            }

            return SnapshotManager.NormalizePredictions(camera, image.Width, image.Height, predictionList, _logger);
        }

        private bool ShouldIncludePrediction(string cameraName, Camera camera, Stopwatch overallStopwatch, AIPrediction prediction)
        {
            if (camera.Exclusions != null && camera.Exclusions.Count > 0)
            {
                SKRectI boundary = SKRectI.Create(prediction.MinX, prediction.MinY, prediction.SizeX, prediction.SizeY);
                foreach (Zone exclusion in camera.Exclusions)
                {
                    int startX = Math.Min(exclusion.Start.X, exclusion.End.X);
                    int startY = Math.Min(exclusion.Start.Y, exclusion.End.Y);
                    int endX = Math.Max(exclusion.Start.X, exclusion.End.X);
                    int endY = Math.Max(exclusion.Start.Y, exclusion.End.Y);
                    SKRectI exclusionZoneBoundary = SKRectI.Create(startX, startY, endX - startX, endY - startY);
                    bool exclude = exclusion.Mode == OverlapMode.Contains ? exclusionZoneBoundary.Contains(boundary) : exclusionZoneBoundary.IntersectsWith(boundary);
                    if (exclude)
                    {
                        _logger.LogDebug(
                            "{cameraName}: Ignored matching '{label}' ([{minX},{minY}],[{maxX},{maxY}]) as it fell within the exclusion zone ([{zoneStartX},{zoneStartY}],[{zoneEndX},{zoneEndY}]) with exclusion mode '{mode}' at EVENT TIME {elapsedMs}ms.",
                            cameraName,
                            prediction.Label,
                            prediction.MinX,
                            prediction.MinY,
                            prediction.MaxX,
                            prediction.MaxY,
                            exclusion.Start.X,
                            exclusion.Start.Y,
                            exclusion.End.X,
                            exclusion.End.Y,
                            exclusion.Mode,
                            overallStopwatch.ElapsedMilliseconds);
                        return false;
                    }
                }
            }

            return true;
        }

        private async Task<CameraProcessingStatus> SendValidSnapshotAsync(
            Camera camera,
            SnapshotCandidate candidate,
            CancellationToken cancellationToken)
        {
            ProcessedImage processedImage = SnapshotManager.DressImage(
                camera,
                candidate.Snapshot,
                candidate.Predictions,
                candidate.ValidPredictions,
                _logger);
            if (processedImage == null)
            {
                _logger.LogError("{cameraName}: Valid detections were found, but the snapshot could not be annotated.", camera.Name);
                _cameraQueue.AddCameraDelay(camera.Name, Config.AIFailureDelayMs);
                return CameraProcessingStatus.ImageAnnotationFailed;
            }

            Notification notification = new()
            {
                CreatedAt = candidate.CapturedAt.LocalDateTime,
                ProcessedImage = processedImage,
                ValidPredictions = candidate.ValidPredictions
            };

            List<INotifier> notifiers = GetMatchingNotifiers(
                camera,
                candidate.ValidPredictions.Select(x => x.Label).Distinct().ToList()).ToList();
            try
            {
                await SendNotifications(camera, notification, notifiers);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{cameraName}: Failed to send photo notification.", camera.Name);
                _cameraQueue.AddCameraDelay(camera.Name, Config.AIFailureDelayMs);
                return CameraProcessingStatus.NotificationFailed;
            }

            _detectionMemory.RememberNotifiedPredictions(camera.Name, candidate.ValidPredictions);
            await AttachRecordingClipIfNeeded(camera, candidate.CapturedAt, notification, notifiers, cancellationToken);
            await SendRecordingClipNotifications(camera, notification, notifiers);

            return CameraProcessingStatus.ValidObjectDetected;
        }

        private static decimal CalculateSnapshotScore(IEnumerable<AIPrediction> validPredictions)
        {
            List<AIPrediction> predictions = validPredictions?.ToList() ?? new List<AIPrediction>();
            if (predictions.Count == 0)
            {
                return 0;
            }

            return predictions.Max(x => x.Confidence) + predictions.Count / 1000m;
        }

        private static SnapshotCandidate SelectPerfectShot(IEnumerable<SnapshotCandidate> candidates)
        {
            return candidates
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.ValidPredictions.Count)
                .ThenBy(x => x.SnapshotCount)
                .First();
        }

        private sealed class SnapshotCandidate
        {
            public SnapshotCandidate(
                int snapshotCount,
                DateTimeOffset capturedAt,
                byte[] snapshot,
                IReadOnlyList<AIPrediction> predictions,
                IReadOnlyList<AIPrediction> validPredictions,
                decimal score)
            {
                SnapshotCount = snapshotCount;
                CapturedAt = capturedAt;
                Snapshot = snapshot;
                Predictions = predictions;
                ValidPredictions = validPredictions;
                Score = score;
            }

            public int SnapshotCount { get; }
            public DateTimeOffset CapturedAt { get; }
            public byte[] Snapshot { get; }
            public IReadOnlyList<AIPrediction> Predictions { get; }
            public IReadOnlyList<AIPrediction> ValidPredictions { get; }
            public decimal Score { get; }
        }

        private byte[] PreProcessSnapshot(Camera camera, byte[] snapshot)
        {
            if (camera.Rotate == 0)
            {
                return snapshot;
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            using SKBitmap originalBitmap = DecodeBitmap(snapshot);
            if (originalBitmap == null)
            {
                _logger.LogWarning("{cameraName}: Failed to decode image before rotation.", camera.Name);
                return null;
            }

            _logger.LogInformation("{cameraName}: Rotating image {rotation} degrees.", camera.Name, camera.Rotate);
            using SKBitmap rotatedBitmap = Rotate(originalBitmap, camera.Rotate);
            if (rotatedBitmap == null)
            {
                _logger.LogWarning("{cameraName}: Failed to rotate image.", camera.Name);
                return null;
            }

            using SKPixmap pixmap = rotatedBitmap.PeekPixels();
            using SKData data = pixmap.Encode(SKEncodedImageFormat.Jpeg, Config.OutputJpegQuality);
            _logger.LogInformation("{cameraName}: Image preprocessing complete ({elapsedMs}ms).", camera.Name, stopwatch.ElapsedMilliseconds);
            return data.ToArray();
        }

        private static SKBitmap Rotate(SKBitmap bitmap, double angle)
        {
            double radians = Math.PI * angle / 180;
            float sine = (float)Math.Abs(Math.Sin(radians));
            float cosine = (float)Math.Abs(Math.Cos(radians));
            int originalWidth = bitmap.Width;
            int originalHeight = bitmap.Height;
            int rotatedWidth = (int)(cosine * originalWidth + sine * originalHeight);
            int rotatedHeight = (int)(cosine * originalHeight + sine * originalWidth);

            SKBitmap rotatedBitmap = new(rotatedWidth, rotatedHeight);
            using SKCanvas canvas = new(rotatedBitmap);
            canvas.Clear();
            canvas.Translate(rotatedWidth / 2, rotatedHeight / 2);
            canvas.RotateDegrees((float)angle);
            canvas.Translate(-originalWidth / 2, -originalHeight / 2);
            canvas.DrawBitmap(bitmap, new SKPoint());

            return rotatedBitmap;
        }

        private static SKBitmap DecodeBitmap(byte[] snapshot)
        {
            try
            {
                return SKBitmap.Decode(snapshot);
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private async Task SendNotifications(Camera camera, Notification notification, IEnumerable<INotifier> notifiers)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();

            List<Task> tasks = new();
            foreach (INotifier notifier in notifiers)
            {
                tasks.Add(notifier.SendAsync(camera, notification, _logger));
            }

            await Task.WhenAll(tasks);
            stopwatch.Stop();
            _logger.LogInformation("{cameraName}: Notifications sent ({elapsedMs}ms).", camera.Name, stopwatch.ElapsedMilliseconds);
        }

        private static IEnumerable<INotifier> GetMatchingNotifiers(Camera camera, IEnumerable<string> labels)
        {
            return Config.Notifiers
                .Where(x =>
                    (x.Cameras == null || !x.Cameras.Any() || x.Cameras.Any(c => c.Equals(camera.Name, StringComparison.OrdinalIgnoreCase))) &&
                    (x.Types == null || !x.Types.Any() || x.Types.Any(t => labels.Contains(t, StringComparer.OrdinalIgnoreCase)))
                ).ToList();
        }

        private async Task AttachRecordingClipIfNeeded(
            Camera camera,
            DateTimeOffset detectedAt,
            Notification notification,
            IEnumerable<INotifier> notifiers,
            CancellationToken cancellationToken)
        {
            IRecordingClipNotifier recordingClipNotifier = notifiers
                .OfType<IRecordingClipNotifier>()
                .FirstOrDefault(x => x.SendRecordingClip);

            if (recordingClipNotifier == null)
            {
                return;
            }

            try
            {
                int downloadDelayMs = Math.Max(0, recordingClipNotifier.RecordingClipDownloadDelayMs);
                if (downloadDelayMs > 0)
                {
                    _logger.LogInformation(
                        "{cameraName}: Waiting {delayMs}ms before downloading the recording clip.",
                        camera.Name,
                        downloadDelayMs);
                    await Task.Delay(downloadDelayMs, cancellationToken);
                }

                notification.RecordingClip = await _synologyService.DownloadLatestRecordingClipAsync(
                    camera.Name,
                    detectedAt,
                    recordingClipNotifier.RecordingClipOffsetMs,
                    recordingClipNotifier.RecordingClipDurationMs);

                if (notification.RecordingClip == null)
                {
                    _logger.LogWarning("{cameraName}: Recording clip was not available; skipping video notification.", camera.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{cameraName}: Recording clip download failed after photo notifications were sent.", camera.Name);
            }
        }

        private async Task SendRecordingClipNotifications(Camera camera, Notification notification, IEnumerable<INotifier> notifiers)
        {
            List<Task> tasks = notifiers
                .OfType<IRecordingClipNotifier>()
                .Where(x => x.SendRecordingClip)
                .Select(x => x.SendRecordingClipAsync(camera, notification, _logger))
                .ToList();

            if (tasks.Count == 0)
            {
                return;
            }

            await Task.WhenAll(tasks);
        }

        private async Task<byte[]> GetSnapshot(string cameraName)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            byte[] imageBytes = await _synologyService.TakeSnapshotAsync(cameraName);
            stopwatch.Stop();

            if (imageBytes == null)
            {
                _logger.LogError("{cameraName}: Failed to get snapshot.", cameraName);
            }
            else
            {
                _logger.LogInformation("{cameraName}: Snapshot received in {elapsedMs}ms.", cameraName, stopwatch.ElapsedMilliseconds);
            }

            return imageBytes;
        }

        private async Task<IEnumerable<AIPrediction>> GetAIPredications(Camera camera, byte[] imageBytes)
        {
            IEnumerable<AIPrediction> predictions = await _aiService.ProcessAsync(camera, imageBytes);
            if (predictions == null)
            {
                _logger.LogError("{cameraName}: Failed to get predictions.", camera.Name);
                return null;
            }

            foreach (AIPrediction prediction in predictions)
            {
                _logger.LogInformation(
                    "AI Detected '{cameraName}': {label} ({confidence}%) [Size: {sizeX}x{sizeY}] [Start: {minX},{minY} | End: {maxX},{maxY}]",
                    camera.Name,
                    prediction.Label,
                    prediction.Confidence,
                    prediction.SizeX,
                    prediction.SizeY,
                    prediction.MinX,
                    prediction.MinY,
                    prediction.MaxX,
                    prediction.MaxY);
            }

            return predictions;
        }
    }
}
