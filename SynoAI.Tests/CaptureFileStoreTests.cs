using NUnit.Framework;
using SynoAI.Services;
using System;
using System.IO;

namespace SynoAI.Tests
{
    public class CaptureFileStoreTests
    {
        private string _previousDirectory;
        private string _workspace;

        [SetUp]
        public void Setup()
        {
            _previousDirectory = Environment.CurrentDirectory;
            _workspace = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_workspace, "Captures", "Driveway"));
            File.WriteAllText(Path.Combine(_workspace, "Captures", "Driveway", "capture.jpeg"), "test");
            Directory.CreateDirectory(Path.Combine(_workspace, "Captures", "Door_1"));
            File.WriteAllText(Path.Combine(_workspace, "Captures", "Door_1", "capture.jpeg"), "test");
            Environment.CurrentDirectory = _workspace;
        }

        [TearDown]
        public void TearDown()
        {
            Environment.CurrentDirectory = _previousDirectory;
            if (Directory.Exists(_workspace))
            {
                Directory.Delete(_workspace, recursive: true);
            }
        }

        [Test]
        public void TryGetCapturePath_ReturnsExistingCapture()
        {
            bool found = CaptureFileStore.TryGetCapturePath("Driveway", "capture.jpeg", out string path);

            Assert.That(found, Is.True);
            Assert.That(File.Exists(path), Is.True);
        }

        [Test]
        public void TryGetCapturePath_RejectsTraversal()
        {
            bool found = CaptureFileStore.TryGetCapturePath("Driveway", "..", out string path);

            Assert.That(found, Is.False);
            Assert.That(path, Is.Null);
        }

        [Test]
        public void TryGetCapturePath_MapsUnsafeCameraNameToSafeDirectory()
        {
            bool found = CaptureFileStore.TryGetCapturePath("Door:1", "capture.jpeg", out string path);

            Assert.That(found, Is.True);
            Assert.That(path, Does.Contain("Door_1"));
        }

        [Test]
        public void ToSafePathSegment_ReplacesPathSeparators()
        {
            string segment = CaptureFileStore.ToSafePathSegment("../Door\\1");

            Assert.That(segment, Is.EqualTo("_Door_1"));
        }
    }
}
