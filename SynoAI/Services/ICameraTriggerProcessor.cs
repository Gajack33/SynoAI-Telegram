using System.Threading;
using System.Threading.Tasks;

namespace SynoAI.Services
{
    public interface ICameraTriggerProcessor
    {
        Task<CameraProcessingStatus> ProcessAsync(string cameraName, CancellationToken cancellationToken);
    }
}
