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
    }
}
