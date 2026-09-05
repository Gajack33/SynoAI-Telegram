using Microsoft.AspNetCore.Mvc;
using SynoAI.App;
using SynoAI.Services;

namespace SynoAI.Controllers
{
    [ApiController]
    [Route("Diagnostics")]
    public sealed class DiagnosticsController : ControllerBase
    {
        private readonly PipelineDiagnostics _diagnostics;
        private readonly ICameraProcessingQueue _cameras;
        private readonly IRecordingClipQueue _clips;

        public DiagnosticsController(PipelineDiagnostics diagnostics, ICameraProcessingQueue cameras, IRecordingClipQueue clips)
        {
            _diagnostics = diagnostics;
            _cameras = cameras;
            _clips = clips;
        }

        [HttpGet]
        public IActionResult Get()
        {
            if (!RequestAuthorization.IsAuthorized(Request)) return Unauthorized();
            return Ok(new
            {
                CameraQueueDepth = _cameras.PendingCount,
                RecordingClipQueueDepth = _clips.PendingCount,
                Pipeline = _diagnostics.GetSnapshot()
            });
        }
    }
}
