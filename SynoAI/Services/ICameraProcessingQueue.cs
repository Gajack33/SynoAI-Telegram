using System.Threading;
using System.Threading.Tasks;

namespace SynoAI.Services
{
    public interface ICameraProcessingQueue
    {
        CameraEnqueueResult TryEnqueue(string cameraName);
        void SetCameraEnabled(string cameraName, bool enabled);
        void AddCameraDelay(string cameraName, int delayMs);
        void Complete(string cameraName);
        ValueTask<CameraTriggerWorkItem> ReadAsync(CancellationToken cancellationToken);
    }
}
