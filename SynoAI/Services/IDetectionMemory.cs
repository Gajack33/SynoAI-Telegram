using SynoAI.Models;
using System;
using System.Collections.Generic;

namespace SynoAI.Services
{
    public interface IDetectionMemory
    {
        bool IsDuplicateSnapshot(string cameraName, byte[] snapshot, TimeSpan window);

        IReadOnlyList<AIPrediction> FilterStationaryPredictions(
            string cameraName,
            IEnumerable<AIPrediction> predictions,
            TimeSpan window,
            int movementThresholdPixels);

        void RememberNotifiedPredictions(string cameraName, IEnumerable<AIPrediction> predictions);
    }
}
