using System;
using System.IO;

namespace SynoAI.Models
{
    public class ProcessedFile
    {
        public readonly string FilePath;
        public readonly string FileName;

        public ProcessedFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentNullException(nameof(filePath));

            FilePath = filePath;
            FileName = Path.GetFileName(filePath);
        }

        public FileStream GetReadonlyStream()
        {
            return File.OpenRead(FilePath);
        }
    }
}
