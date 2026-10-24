using SkyrimModPacker.Infrastructure.Preview;
using Xunit;

namespace SkyrimModPacker.Tests;

public sealed class NifCompatibilityInspectorTests
{
    [Theory]
    [InlineData("valid", NifFormatFamily.SkyrimSe, 100)]
    [InlineData("LE", NifFormatFamily.SkyrimLe, 83)]
    public void HeaderIdentificationIsReadOnly_AndDoesNotImplyGeometrySupport(string scenario, NifFormatFamily family, int version)
    {
        var path = Path.Combine(Path.GetTempPath(), "NifHeader-" + Guid.NewGuid().ToString("N") + ".nif");
        try
        {
            var bytes = PreviewFixtures.Nif(scenario); File.WriteAllBytes(path, bytes);
            var header = NifCompatibilityInspector.Read(path);
            Assert.Equal(family, header.Family); Assert.Equal((uint)version, header.StreamVersion);
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }
}
