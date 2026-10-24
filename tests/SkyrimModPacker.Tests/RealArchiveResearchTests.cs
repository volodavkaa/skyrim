using System.Text.Json;
using SkyrimModPacker.Infrastructure.PluginBuilder;
using SkyrimModPacker.Infrastructure.Preview;
using Xunit;
namespace SkyrimModPacker.Tests;
public sealed class RealArchiveResearchTests
{
    [Fact]
    public async Task InspectAvailableUserWeaponArchive()
    {
        var path = Environment.GetEnvironmentVariable("SKYRIM_WEAPON_ARCHIVE"); if (string.IsNullOrWhiteSpace(path)) return;
        await using var source = await WeaponArchiveSource.OpenAsync(path);
        var report = Directory.GetFiles(source.Root, "*.nif", SearchOption.AllDirectories).Select(file =>
        {
            var model = SseNifReader.Read(File.ReadAllBytes(file));
            return new { file = Path.GetRelativePath(source.Root, file), meshes = model.Meshes.Count, model.TexturePaths, model.TextureDependenciesComplete, model.Warnings };
        }).ToArray();
        Assert.NotEmpty(report);
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/audit")); Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "user-archive-model-research.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
