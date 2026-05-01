using System;
using System.Collections.Generic;

namespace SynoAI.Models
{
    public class Notification
    {
        /// <summary>
        /// Object for fetching the processed image.
        /// </summary>
        public ProcessedImage ProcessedImage { get; set; }
        /// <summary>
        /// Local time when the notification was created.
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        /// <summary>
        /// Optional recording clip associated with the detection.
        /// </summary>
        public ProcessedFile RecordingClip { get; set; }
        /// <summary>
        /// The list of valid predictions.
        /// </summary>
        public IEnumerable<AIPrediction> ValidPredictions { get; set; }
    }
}
