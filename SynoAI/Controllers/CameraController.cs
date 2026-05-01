using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SynoAI.App;
using SynoAI.Models.DTOs;
using SynoAI.Services;

namespace SynoAI.Controllers
{
    /// <summary>
    /// Controller triggered on a motion alert from Synology Surveillance Station.
    /// </summary>
    [ApiController]
    [Route("[controller]")]
    public class CameraController : ControllerBase
    {
        private readonly ICameraProcessingQueue _cameraQueue;
        private readonly ILogger<CameraController> _logger;

        public CameraController(ICameraProcessingQueue cameraQueue, ILogger<CameraController> logger)
        {
            _cameraQueue = cameraQueue;
            _logger = logger;
        }

        /// <summary>
        /// Called by the Synology motion alert hook.
        /// </summary>
        /// <param name="id">The name of the camera.</param>
        [HttpGet]
        [Route("{id}")]
        public IActionResult Get(string id)
        {
            if (!RequestAuthorization.IsAuthorized(Request))
            {
                return Unauthorized();
            }

            CameraEnqueueResult result = _cameraQueue.TryEnqueue(id);
            switch (result.Status)
            {
                case CameraEnqueueStatus.Queued:
                    return StatusCode(202, "Camera trigger queued.");
                case CameraEnqueueStatus.MissingCameraName:
                    return BadRequest("Camera name is required.");
                case CameraEnqueueStatus.CameraDisabled:
                    return Ok("Camera disabled.");
                case CameraEnqueueStatus.CameraNotFound:
                    return NotFound();
                case CameraEnqueueStatus.CameraDelayed:
                    return Ok("Camera delayed.");
                case CameraEnqueueStatus.CameraAlreadyProcessing:
                    return Ok("Camera already processing.");
                case CameraEnqueueStatus.QueueUnavailable:
                    return StatusCode(503, "Camera queue unavailable.");
                default:
                    _logger.LogError("{cameraName}: Unexpected camera enqueue status {status}.", id, result.Status);
                    return StatusCode(500, "Camera trigger failed.");
            }
        }

        [HttpPost]
        [Route("{id}")]
        public IActionResult Post(string id, [FromBody] CameraOptionsDto options)
        {
            if (!RequestAuthorization.IsAuthorized(Request))
            {
                return Unauthorized();
            }

            if (options == null)
            {
                return BadRequest("Camera options are required.");
            }

            if (options.HasChanged(x => x.Enabled))
            {
                _cameraQueue.SetCameraEnabled(id, options.Enabled);
            }

            return Ok();
        }
    }
}
