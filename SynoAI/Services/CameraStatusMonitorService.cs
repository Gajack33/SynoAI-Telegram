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
        private readonly PipelineDiagnostics _diagnostics;
        private readonly Dictionary<string, CameraStatusTransitionTracker> _trackers =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<(string Camera, ICameraStatusNotifier Notifier), bool> _deliveredStates = new();
        private readonly Dictionary<(string Camera, ICameraStatusNotifier Notifier), PendingAlert> _pendingAlerts = new();

        public CameraStatusMonitorService(
            ISynologyService synologyService,
            ILogger<CameraStatusMonitorService> logger,
            PipelineDiagnostics diagnostics = null)
        {
            _synologyService = synologyService;
            _logger = logger;
            _diagnostics = diagnostics;
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
            using var poll = _diagnostics?.Begin("camera-status", "synology", cancellationToken);
            IEnumerable<SynologyCamera> result = await _synologyService.GetCamerasAsync(cancellationToken);
            poll?.Complete(result != null);
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
                if (transition == true)
                {
                    _logger.LogInformation(
                        "{cameraName}: Camera is back online (Surveillance Station status {statusCode}: {status}).",
                        camera.Name,
                        (int)status,
                        status);
                }
                else if (transition == false)
                {
                    _logger.LogWarning(
                        "{cameraName}: Camera is offline (Surveillance Station status {statusCode}: {status}).",
                        camera.Name,
                        (int)status,
                        status);
                }

                await SendNotificationsAsync(camera, transition, observedOnline, cancellationToken);
            }
        }

        private async Task SendNotificationsAsync(
            Camera camera,
            bool? transition,
            bool observedOnline,
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

            foreach (ICameraStatusNotifier notifier in notifiers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = (camera.Name, notifier);
                string diagnosticTarget = $"{camera.Name}/{Config.Notifiers.ToList().IndexOf((INotifier)notifier)}";
                // Healthy startup is the implicit baseline, without an online notification.
                _deliveredStates.TryAdd(key, true);
                if (transition.HasValue)
                {
                    _pendingAlerts.Remove(key);
                    if (_deliveredStates[key] != transition.Value)
                    {
                        _pendingAlerts[key] = new PendingAlert(transition.Value, DateTimeOffset.Now);
                    }
                    else
                    {
                        _diagnostics?.Forget("telegram-status", diagnosticTarget);
                    }
                }

                if (!_pendingAlerts.TryGetValue(key, out PendingAlert pending) || pending.IsOnline != observedOnline)
                {
                    continue;
                }

                try
                {
                    using var operation = _diagnostics?.Begin("telegram-status", diagnosticTarget, cancellationToken);
                    await notifier.SendCameraStatusAsync(camera, pending.IsOnline, pending.ChangedAt, _logger, cancellationToken);
                    operation?.Complete(true);
                    _deliveredStates[key] = pending.IsOnline;
                    _pendingAlerts.Remove(key);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Keep the latest undelivered transition for the next polling interval.
                    _logger.LogError(ex, "{cameraName}: Camera status notification failed; it will be retried on a later poll.", camera.Name);
                }
            }
        }

        private sealed record PendingAlert(bool IsOnline, DateTimeOffset ChangedAt);
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
