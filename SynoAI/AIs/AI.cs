using Microsoft.Extensions.Logging;
using SynoAI.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SynoAI.AIs
{
    public abstract class AI
    {
        public abstract Task<IEnumerable<AIPrediction>> Process(ILogger logger, Camera camera, byte[] image);
    }
}
