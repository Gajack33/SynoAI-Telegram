using SynoAI.Models;
using SynoAI.Notifiers;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SynoAI.Services
{
    public sealed class RecordingClipWorkItem
    {
        public RecordingClipWorkItem(
            Camera camera,
            DateTimeOffset detectedAt,
            IEnumerable<IRecordingClipNotifier> notifiers)
        {
            Camera = camera ?? throw new ArgumentNullException(nameof(camera));
            DetectedAt = detectedAt;
            Notifiers = notifiers?.Where(x => x != null).ToList()
                ?? throw new ArgumentNullException(nameof(notifiers));
        }

        public Camera Camera { get; }
        public DateTimeOffset DetectedAt { get; }
        public IReadOnlyList<IRecordingClipNotifier> Notifiers { get; }
    }
}
