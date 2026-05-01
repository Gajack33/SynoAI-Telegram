using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SynoAI.Models;

namespace SynoAI.Notifiers
{
    public abstract class NotifierBase : INotifier
    {
        public IEnumerable<string> Cameras { get; set; }
        public IEnumerable<string> Types { get; set; }

        public abstract Task SendAsync(Camera camera, Notification notification, ILogger logger);
    }
}
