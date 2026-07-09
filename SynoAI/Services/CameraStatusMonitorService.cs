using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SynoAI.Models;
using SynoAI.Notifiers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SynoAI.Services
{
    public sealed class CameraStatusMonitorService : BackgroundService
    {
        private readonly ISynologyService _synologyService;
        private readonly ILogger<CameraStatusMonitorService> _logger;
        private readonly Dictionary<string, CameraStatusTransitionTracker> _trackers =
            new(StringComparer.OrdinalIgnoreCase);

        public CameraStatusMonitorService(
            ISynologyService synologyService,
            ILogger<CameraStatusMonitorService> logger)
        {
            _synologyService = synologyService;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!Config.CameraStatusMonitoringEnabled)
            {
                _logger.LogInformation("Camera status monitoring is disabled.");
                return;
            }

            _logger.LogInformation(
                "Camera status monitoring started with a {pollingIntervalSeconds}s interval and {confirmationCount} confirmation(s).",
                Config.CameraStatusPollingIntervalSeconds,
                Config.CameraStatusConfirmationCount);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckCameraStatusesAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Camera status check failed. No camera state was changed.");
                }

                try
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(Config.CameraStatusPollingIntervalSeconds),
                        stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        internal async Task CheckCameraStatusesAsync(CancellationToken cancellationToken)
        {
            IEnumerable<SynologyCamera> result = await _synologyService.GetCamerasAsync();
            if (result == null)
            {
                _logger.LogWarning("Camera status check returned no data. Existing states are kept unchanged.");
                return;
            }

            Dictionary<string, SynologyCamera> synologyCameras = result
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.GetName()))
                .GroupBy(x => x.GetName(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

            foreach (Camera camera in Config.Cameras ?? Enumerable.Empty<Camera>())
            {
                cancellationToken.ThrowIfCancellationRequested();

                synologyCameras.TryGetValue(camera.Name, out SynologyCamera synologyCamera);
                SynologyCameraStatus status = synologyCamera?.Status ?? SynologyCameraStatus.Unknown;
                bool observedOnline = status == SynologyCameraStatus.Normal;

                if (!_trackers.TryGetValue(camera.Name, out CameraStatusTransitionTracker tracker))
                {
                    tracker = new CameraStatusTransitionTracker(Config.CameraStatusConfirmationCount);
                    _trackers[camera.Name] = tracker;
                }

                bool? transition = tracker.Observe(observedOnline);
                if (!transition.HasValue)
                {
                    continue;
                }

                if (transition.Value)
                {
                    _logger.LogInformation(
                        "{cameraName}: Camera is back online (Surveillance Station status {statusCode}: {status}).",
                        camera.Name,
                        (int)status,
                        status);
                }
                else
                {
                    _logger.LogWarning(
                        "{cameraName}: Camera is offline (Surveillance Station status {statusCode}: {status}).",
                        camera.Name,
                        (int)status,
                        status);
                }

                await SendNotificationsAsync(camera, transition.Value, DateTimeOffset.Now, cancellationToken);
            }
        }

        private async Task SendNotificationsAsync(
            Camera camera,
            bool isOnline,
            DateTimeOffset changedAt,
            CancellationToken cancellationToken)
        {
            List<ICameraStatusNotifier> notifiers = (Config.Notifiers ?? Enumerable.Empty<INotifier>())
                .Where(x =>
                    x is ICameraStatusNotifier statusNotifier &&
                    statusNotifier.SendCameraStatusNotifications &&
                    (x.Cameras == null ||
                     !x.Cameras.Any() ||
                     x.Cameras.Any(name => name.Equals(camera.Name, StringComparison.OrdinalIgnoreCase))))
                .Cast<ICameraStatusNotifier>()
                .ToList();

            if (notifiers.Count == 0)
            {
                _logger.LogInformation(
                    "{cameraName}: No notifier is configured for camera status changes.",
                    camera.Name);
                return;
            }

            foreach (ICameraStatusNotifier notifier in notifiers)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await notifier.SendCameraStatusAsync(camera, isOnline, changedAt, _logger);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "{cameraName}: Failed to send camera {status} notification.",
                        camera.Name,
                        isOnline ? "online" : "offline");
                }
            }
        }
    }

    internal sealed class CameraStatusTransitionTracker
    {
        private readonly int _confirmationCount;
        private bool? _confirmedOnline;
        private bool? _candidateOnline;
        private int _candidateCount;

        public CameraStatusTransitionTracker(int confirmationCount)
        {
            _confirmationCount = Math.Max(1, confirmationCount);
        }

        public bool? Observe(bool observedOnline)
        {
            if (_confirmedOnline == observedOnline)
            {
                ResetCandidate();
                return null;
            }

            // A healthy startup establishes the baseline without generating a noisy online alert.
            if (!_confirmedOnline.HasValue && observedOnline)
            {
                _confirmedOnline = true;
                ResetCandidate();
                return null;
            }

            if (_candidateOnline != observedOnline)
            {
                _candidateOnline = observedOnline;
                _candidateCount = 1;
            }
            else
            {
                _candidateCount++;
            }

            if (_candidateCount < _confirmationCount)
            {
                return null;
            }

            _confirmedOnline = observedOnline;
            ResetCandidate();
            return observedOnline;
        }

        private void ResetCandidate()
        {
            _candidateOnline = null;
            _candidateCount = 0;
        }
    }
}
