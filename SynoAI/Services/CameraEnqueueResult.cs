using System;

namespace SynoAI.Services
{
    public enum CameraEnqueueStatus
    {
        Queued,
        MissingCameraName,
        CameraDisabled,
        CameraNotFound,
        CameraDelayed,
        CameraAlreadyProcessing,
        QueueUnavailable
    }

    public sealed class CameraEnqueueResult
    {
        public CameraEnqueueResult(CameraEnqueueStatus status, DateTime? ignoreUntil = null)
        {
            Status = status;
            IgnoreUntil = ignoreUntil;
        }

        public CameraEnqueueStatus Status { get; }
        public DateTime? IgnoreUntil { get; }
    }
}
