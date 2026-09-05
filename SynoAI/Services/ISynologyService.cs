using System.Threading;
using SynoAI.Models;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;

namespace SynoAI.Services
{
    public interface ISynologyService
    {
        Task InitialiseAsync(CancellationToken cancellationToken = default);
        Task<Cookie> LoginAsync(CancellationToken cancellationToken = default);
        Task<IEnumerable<SynologyCamera>> GetCamerasAsync(CancellationToken cancellationToken = default);
        Task<byte[]> TakeSnapshotAsync(string cameraName, CancellationToken cancellationToken = default);
        Task<ProcessedFile> DownloadLatestRecordingClipAsync(string cameraName, DateTimeOffset detectedAt, int offsetTimeMs, int playTimeMs, CancellationToken cancellationToken = default);
    }
}
