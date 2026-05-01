using SynoAI.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace SynoAI.Services
{
    public class DetectionMemory : IDetectionMemory
    {
        private readonly ConcurrentDictionary<string, SnapshotRecord> _snapshots = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, PredictionRecord> _predictions = new(StringComparer.OrdinalIgnoreCase);

        public bool IsDuplicateSnapshot(string cameraName, byte[] snapshot, TimeSpan window)
        {
            if (window <= TimeSpan.Zero || snapshot == null || snapshot.Length == 0)
            {
                return false;
            }

            DateTime now = DateTime.UtcNow;
            string hash = Convert.ToHexString(SHA256.HashData(snapshot));
            SnapshotRecord current = new(hash, now);

            bool duplicate = false;
            _snapshots.AddOrUpdate(
                cameraName,
                current,
                (_, previous) =>
                {
                    duplicate = previous.Hash == hash && now - previous.SeenAtUtc <= window;
                    return current;
                });

            return duplicate;
        }

        public IReadOnlyList<AIPrediction> FilterStationaryPredictions(
            string cameraName,
            IEnumerable<AIPrediction> predictions,
            TimeSpan window,
            int movementThresholdPixels)
        {
            List<AIPrediction> predictionList = predictions?.ToList() ?? new List<AIPrediction>();
            if (window <= TimeSpan.Zero || movementThresholdPixels < 0 || predictionList.Count == 0)
            {
                return predictionList;
            }

            if (!_predictions.TryGetValue(cameraName, out PredictionRecord previous) ||
                DateTime.UtcNow - previous.SeenAtUtc > window)
            {
                return predictionList;
            }

            return predictionList
                .Where(prediction => !previous.Predictions.Any(x => IsSameStationaryObject(x, prediction, movementThresholdPixels)))
                .ToList();
        }

        public void RememberNotifiedPredictions(string cameraName, IEnumerable<AIPrediction> predictions)
        {
            List<AIPrediction> predictionList = predictions?.ToList() ?? new List<AIPrediction>();
            if (predictionList.Count == 0)
            {
                return;
            }

            _predictions[cameraName] = new PredictionRecord(predictionList, DateTime.UtcNow);
        }

        private static bool IsSameStationaryObject(AIPrediction previous, AIPrediction current, int movementThresholdPixels)
        {
            if (!string.Equals(previous.Label, current.Label, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            int previousCenterX = previous.MinX + previous.SizeX / 2;
            int previousCenterY = previous.MinY + previous.SizeY / 2;
            int currentCenterX = current.MinX + current.SizeX / 2;
            int currentCenterY = current.MinY + current.SizeY / 2;

            return Math.Abs(previousCenterX - currentCenterX) <= movementThresholdPixels &&
                   Math.Abs(previousCenterY - currentCenterY) <= movementThresholdPixels &&
                   Math.Abs(previous.SizeX - current.SizeX) <= movementThresholdPixels &&
                   Math.Abs(previous.SizeY - current.SizeY) <= movementThresholdPixels;
        }

        private sealed record SnapshotRecord(string Hash, DateTime SeenAtUtc);

        private sealed record PredictionRecord(IReadOnlyList<AIPrediction> Predictions, DateTime SeenAtUtc);
    }
}
