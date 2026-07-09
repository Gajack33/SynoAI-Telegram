using System.Threading;
using System.Threading.Tasks;

namespace SynoAI.Services
{
    public interface IRecordingClipProcessor
    {
        Task ProcessAsync(RecordingClipWorkItem workItem, CancellationToken cancellationToken);
    }
}
