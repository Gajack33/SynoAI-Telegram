using Microsoft.Extensions.Logging;
using SynoAI.Models;
using System;
using System.Threading.Tasks;

namespace SynoAI.Notifiers
{
    public interface ICameraStatusNotifier
    {
        bool SendCameraStatusNotifications { get; }
        Task SendCameraStatusAsync(Camera camera, bool isOnline, DateTimeOffset changedAt, ILogger logger);
    }
}
