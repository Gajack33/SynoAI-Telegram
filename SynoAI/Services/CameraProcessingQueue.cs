using Microsoft.Extensions.Logging;
using SynoAI.Models;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace SynoAI.Services
{
    public class CameraProcessingQueue : ICameraProcessingQueue
    {
        private readonly Channel<CameraTriggerWorkItem> _queue = Channel.CreateUnbounded<CameraTriggerWorkItem>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

        private readonly ConcurrentDictionary<string, bool> _runningCameraChecks = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> _delayedCameraChecks = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, bool> _enabledCameras = new(StringComparer.OrdinalIgnoreCase);
        private readonly ILogger<CameraProcessingQueue> _logger;

        public CameraProcessingQueue(ILogger<CameraProcessingQueue> logger)
        {
            _logger = logger;
        }

        public CameraEnqueueResult TryEnqueue(string cameraName)
        {
            if (string.IsNullOrWhiteSpace(cameraName))
            {
                return new CameraEnqueueResult(CameraEnqueueStatus.MissingCameraName);
            }

            if (_enabledCameras.TryGetValue(cameraName, out bool enabled) && !enabled)
            {
                _logger.LogInformation("{cameraName}: Requests for this camera will not be processed as it is currently disabled.", cameraName);
                return new CameraEnqueueResult(CameraEnqueueStatus.CameraDisabled);
            }

            Camera camera = Config.Cameras?.FirstOrDefault(x =>
                !string.IsNullOrWhiteSpace(x.Name) &&
                x.Name.Equals(cameraName, StringComparison.OrdinalIgnoreCase));
            if (camera == null)
            {
                _logger.LogError("{cameraName}: The camera was not found.", cameraName);
                return new CameraEnqueueResult(CameraEnqueueStatus.CameraNotFound);
            }

            if (_delayedCameraChecks.TryGetValue(cameraName, out DateTime ignoreUntil))
            {
                if (ignoreUntil >= DateTime.UtcNow)
                {
                    _logger.LogInformation("{cameraName}: Requests for this camera will not be processed until {ignoreUntil}.", cameraName, ignoreUntil);
                    return new CameraEnqueueResult(CameraEnqueueStatus.CameraDelayed, ignoreUntil);
                }

                _delayedCameraChecks.TryRemove(cameraName, out _);
            }

            if (!_runningCameraChecks.TryAdd(cameraName, true))
            {
                _logger.LogInformation("{cameraName}: The request for this camera is already queued or running and was ignored.", cameraName);
                return new CameraEnqueueResult(CameraEnqueueStatus.CameraAlreadyProcessing);
            }

            if (!_queue.Writer.TryWrite(new CameraTriggerWorkItem(camera.Name)))
            {
                _runningCameraChecks.TryRemove(cameraName, out _);
                _logger.LogError("{cameraName}: Failed to enqueue camera trigger.", cameraName);
                return new CameraEnqueueResult(CameraEnqueueStatus.QueueUnavailable);
            }

            _logger.LogInformation("{cameraName}: Camera trigger queued.", cameraName);
            return new CameraEnqueueResult(CameraEnqueueStatus.Queued);
        }

        public void SetCameraEnabled(string cameraName, bool enabled)
        {
            _enabledCameras.AddOrUpdate(cameraName, enabled, (key, oldValue) => enabled);
        }

        public void AddCameraDelay(string cameraName, int delayMs)
        {
            if (delayMs <= 0)
            {
                return;
            }

            DateTime ignoreUntil = DateTime.UtcNow.AddMilliseconds(delayMs);
            _delayedCameraChecks.AddOrUpdate(cameraName, ignoreUntil, (key, oldValue) => ignoreUntil);
            _logger.LogDebug("{cameraName}: Added delay of {delayMs}ms until the next request will be processed.", cameraName, delayMs);
        }

        public void Complete(string cameraName)
        {
            _runningCameraChecks.TryRemove(cameraName, out _);
        }

        public ValueTask<CameraTriggerWorkItem> ReadAsync(CancellationToken cancellationToken)
        {
            return _queue.Reader.ReadAsync(cancellationToken);
        }
    }
}
