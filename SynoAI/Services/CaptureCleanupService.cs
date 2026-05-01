using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace SynoAI.Services
{
    public class CaptureCleanupService : BackgroundService
    {
        private readonly ILogger<CaptureCleanupService> _logger;

        public CaptureCleanupService(ILogger<CaptureCleanupService> logger)
        {
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Capture Cleanup Service is starting.");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    CleanupOldImages();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error occurred executing Capture Cleanup.");
                }

                // Run cleanup every 1 hour
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }

        private void CleanupOldImages()
        {
            if (!Directory.Exists(Constants.DIRECTORY_CAPTURES))
            {
                return;
            }

            if (Config.DaysToKeepCaptures > 0)
            {
                _logger.LogInformation($"Captures Clean Up: Cleaning up images older than {Config.DaysToKeepCaptures} day(s).");
                DirectoryInfo directory = new(Constants.DIRECTORY_CAPTURES);
                IEnumerable<FileInfo> files = directory.GetFiles("*", new EnumerationOptions() { RecurseSubdirectories = true });
                foreach (FileInfo file in files)
                {
                    double age = (DateTime.Now - file.LastWriteTime).TotalDays;
                    if (age > Config.DaysToKeepCaptures)
                    {
                        _logger.LogInformation($"Captures Clean Up: {file.FullName} is {age} day(s) old and will be deleted.");
                        System.IO.File.Delete(file.FullName);
                        _logger.LogInformation($"Captures Clean Up: {file.FullName} deleted.");
                    }
                }
            }
        }
    }
}
