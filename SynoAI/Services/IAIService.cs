using System.Threading;
using SynoAI.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SynoAI.Services
{
    public interface IAIService
    {
        Task<IEnumerable<AIPrediction>> ProcessAsync(Camera camera, byte[] image, CancellationToken cancellationToken = default);
        Task<bool> WarmupAsync(CancellationToken cancellationToken = default);
    }
}
