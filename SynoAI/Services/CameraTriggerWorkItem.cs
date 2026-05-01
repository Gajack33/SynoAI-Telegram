using System;

namespace SynoAI.Services
{
    public sealed class CameraTriggerWorkItem
    {
        public CameraTriggerWorkItem(string cameraName)
        {
            CameraName = cameraName;
            QueuedAtUtc = DateTime.UtcNow;
        }

        public string CameraName { get; }
        public DateTime QueuedAtUtc { get; }
    }
}
