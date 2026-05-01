using NUnit.Framework;
using SynoAI.Models;
using SynoAI.Services;

namespace SynoAI.Tests
{
    public class SynologyServiceTests
    {
        [Test]
        public void BuildSnapshotResource_UsesUnquotedApiNameAndConfiguredQuality()
        {
            string resource = SynologyService.BuildSnapshotResource("entry.cgi", 9, 42, CameraQuality.Balanced);

            Assert.That(resource, Is.EqualTo("webapi/entry.cgi?version=9&id=42&api=SYNO.SurveillanceStation.Camera&method=GetSnapshot&profileType=1"));
            Assert.That(resource, Does.Not.Contain("\"SYNO.SurveillanceStation.Camera\""));
        }

        [Test]
        public void TryNormalizeApiPath_AllowsRelativeSynologyApiPath()
        {
            bool result = SynologyService.TryNormalizeApiPath("entry.cgi", out string normalizedPath);

            Assert.That(result, Is.True);
            Assert.That(normalizedPath, Is.EqualTo("entry.cgi"));
        }

        [TestCase("http://attacker.local/auth.cgi")]
        [TestCase("//attacker.local/auth.cgi")]
        [TestCase("../auth.cgi")]
        [TestCase("auth.cgi?api=evil")]
        public void TryNormalizeApiPath_RejectsUnsafeSynologyApiPath(string path)
        {
            bool result = SynologyService.TryNormalizeApiPath(path, out string normalizedPath);

            Assert.That(result, Is.False);
            Assert.That(normalizedPath, Is.Null);
        }
    }
}
