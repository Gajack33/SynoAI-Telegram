using Microsoft.AspNetCore.Mvc;
using SynoAI.App;
using SynoAI.Services;

namespace SynoAI.Controllers
{
    public class ImageController : Controller
    {
        /// <summary>
        /// Returns the file for the specified camera.
        /// </summary>
        [Route("Image/{cameraName}/{filename}")]
        public ActionResult Get(string cameraName, string filename)
        {
            if (!RequestAuthorization.IsAuthorized(Request))
            {
                return Unauthorized();
            }

            if (!CaptureFileStore.TryGetCapturePath(cameraName, filename, out string path))
            {
                return NotFound();
            }

            return PhysicalFile(path, "image/jpeg");
        }
    }
}
