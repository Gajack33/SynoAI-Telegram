using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SynoAI.Services
{
    public static class CaptureFileStore
    {
        public static bool TryGetCapturePath(string cameraName, string filename, out string path)
        {
            path = null;

            if (string.IsNullOrWhiteSpace(cameraName) || string.IsNullOrWhiteSpace(filename))
            {
                return false;
            }

            string[] filenameSegments = filename
                .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (filenameSegments.Length == 0 || filenameSegments.Any(x => !IsSafePathSegment(x)))
            {
                return false;
            }

            string capturesRoot = Path.GetFullPath(Constants.DIRECTORY_CAPTURES);
            string cameraDirectory = Path.GetFullPath(Path.Combine(capturesRoot, ToSafePathSegment(cameraName)));
            string capturePath = Path.GetFullPath(Path.Combine(new[] { cameraDirectory }.Concat(filenameSegments).ToArray()));

            if (!capturePath.StartsWith(capturesRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!File.Exists(capturePath))
            {
                return false;
            }

            path = capturePath;
            return true;
        }

        public static bool IsSafePathSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            if (value.Contains('/') || value.Contains('\\'))
            {
                return false;
            }

            if (value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return false;
            }

            return value == Path.GetFileName(value) && value != "." && value != "..";
        }

        public static string ToSafePathSegment(string value, string fallback = "capture")
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            HashSet<char> invalidCharacters = Path.GetInvalidFileNameChars()
                .Concat(new[] { '/', '\\', ':' })
                .ToHashSet();

            char[] safeCharacters = value
                .Trim()
                .Select(character => invalidCharacters.Contains(character) || char.IsControl(character) ? '_' : character)
                .ToArray();

            string safeValue = new string(safeCharacters).Trim('.', ' ');
            return string.IsNullOrWhiteSpace(safeValue) ? fallback : safeValue;
        }

        public static string GetRelativePathFromCameraRoot(string cameraName, string filePath)
        {
            string capturesRoot = Path.GetFullPath(Constants.DIRECTORY_CAPTURES);
            string cameraDirectory = Path.GetFullPath(Path.Combine(capturesRoot, ToSafePathSegment(cameraName)));
            string fullPath = Path.GetFullPath(filePath);

            if (!fullPath.StartsWith(cameraDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFileName(filePath);
            }

            return Path.GetRelativePath(cameraDirectory, fullPath);
        }
    }
}
