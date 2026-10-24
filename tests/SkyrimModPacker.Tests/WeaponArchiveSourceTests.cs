using System.IO.Compression;
using System.Security.Cryptography;
using SkyrimModPacker.Infrastructure.PluginBuilder;
using Xunit;
namespace SkyrimModPacker.Tests;
public sealed class WeaponArchiveSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WeaponArchiveTests", Guid.NewGuid().ToString("N"));
    private string Archive(params (string Name, byte[] Bytes)[] files)
    {
        Directory.CreateDirectory(_root); var path = Path.Combine(_root, Guid.NewGuid() + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, bytes) in files) { using var output = zip.CreateEntry(name).Open(); output.Write(bytes); }
        return path;
    }
    [Fact]
    public async Task ZipOnlyExtractsResourceBytesPreservesArchiveAndCleansUp()
    {
        var nif = PreviewFixtures.Nif(diffuse: "textures/Test.dds"); var dds = PreviewFixtures.Bc7();
        var path = Archive(("Data/meshes/Test.nif", nif), ("Data/textures/Test.dds", dds), ("run.exe", [1, 2, 3]), ("Other.esp", [3, 4, 5]));
        var hash = SHA256.HashData(File.ReadAllBytes(path)); string extracted;
        using (var source = await WeaponArchiveSource.OpenAsync(path))
        { extracted = source.Root; Assert.Equal(2, Directory.GetFiles(extracted, "*", SearchOption.AllDirectories).Length); Assert.Equal(nif, File.ReadAllBytes(Path.Combine(extracted, "Data/meshes/Test.nif"))); Assert.Equal(dds, File.ReadAllBytes(Path.Combine(extracted, "Data/textures/Test.dds"))); }
        Assert.False(Directory.Exists(extracted)); Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(path)));
    }
    [Theory]
    [InlineData("../escape.nif")] [InlineData("/absolute.nif")] [InlineData("C:/outside.nif")] [InlineData("folder/../../escape.dds")]
    [InlineData("alias.dds:stream")] [InlineData("CON.nif")] [InlineData("trailing./file.dds")]
    public async Task ZipRejectsUnsafeEntriesBeforeExtracting(string entry)
    { var path = Archive(("safe.nif", [1]), (entry, [1])); await Assert.ThrowsAnyAsync<Exception>(() => WeaponArchiveSource.OpenAsync(path)); }
    [Fact]
    public async Task ZipRejectsAmbiguousPathsLinksEmptySourcesAndCancellation()
    {
        var duplicate = Archive(("blade.nif", [1]), ("BLADE.NIF", [2])); await Assert.ThrowsAsync<IOException>(() => WeaponArchiveSource.OpenAsync(duplicate));
        var empty = Archive(("README.txt", [1])); await Assert.ThrowsAsync<IOException>(() => WeaponArchiveSource.OpenAsync(empty));
        var link = Archive(("linked.nif", [1])); using (var zip = ZipFile.Open(link, ZipArchiveMode.Update)) zip.Entries[0].ExternalAttributes = unchecked((int)0xA1FF0000);
        await Assert.ThrowsAsync<IOException>(() => WeaponArchiveSource.OpenAsync(link));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WeaponArchiveSource.OpenAsync(duplicate, cancelled.Token));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
