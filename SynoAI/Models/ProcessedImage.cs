using System;
using System.IO;

namespace SynoAI.Models
{
    /// <summary>
    /// An object representing a processed image and the access to it.
    /// </summary>
    public class ProcessedImage
    {
        /// <summary>
        /// The full file path.
        /// </summary>
        public readonly string FilePath;
        /// <summary>
        /// The name of the file.
        /// </summary>
        public readonly string FileName;
        /// <summary>
        /// Capture path relative to the camera capture directory.
        /// </summary>
        public readonly string RelativePath;

        public ProcessedImage(string filePath, string relativePath = null)
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentNullException(nameof(filePath));

            FilePath = filePath;
            FileName = Path.GetFileName(filePath);
            RelativePath = string.IsNullOrWhiteSpace(relativePath) ? FileName : relativePath;
        }

        /// <summary>
        /// Opens a readonly stream to the processed image file. Each notifier must use it's own readonly
        /// stream, otherwise supplying the stream to MultipartFormDataContent will cause threading issues.
        /// </summary>
        public FileStream GetReadonlyStream()
        {
            return File.OpenRead(FilePath);
        }
    }
}
