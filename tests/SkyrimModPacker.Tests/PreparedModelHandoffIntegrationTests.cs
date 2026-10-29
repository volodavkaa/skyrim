using System.Text.Json;
using SkyrimModPacker.UI.Services;
using Xunit;

namespace SkyrimModPacker.Tests;

public partial class WeaponProjectTests
{
    [Fact]
    public async Task PreparedPairPreviewIncludesBothNifsAndReferencedDdsWithoutSelection()
    {
        var root = Path.Combine(_root, "prepared");
        var meshes = Path.Combine(root, "meshes", "sample");
        var textures = Path.Combine(root, "textures", "sample");
        Directory.CreateDirectory(meshes);
        Directory.CreateDirectory(textures);
        File.WriteAllBytes(Path.Combine(meshes, "third.nif"),
            PreviewFixtures.Nif(diffuse: "textures/sample/blade.dds"));
        File.WriteAllBytes(Path.Combine(meshes, "first.nif"),
            PreviewFixtures.Nif(diffuse: "textures/sample/blade.dds"));
        File.WriteAllBytes(Path.Combine(textures, "blade.dds"), PreviewFixtures.Bc7());
        File.WriteAllText(Path.Combine(root, "report.json"), JsonSerializer.Serialize(new
        {
            weapon_type = "sword1h", model = "sample",
            views = new[] { new { output_nif = "meshes/sample/third.nif" },
                new { output_nif = "meshes/sample/first.nif" } }
        }));

        var project = (await Service.CreateAsync(Request())).Value;
        var handoff = PreparedModelHandoff.Read(root);
        var preview = await Service.PreviewWeaponFolderAsync(
            handoff.Request(project.ProjectFilePath, null, false), Data);
        Assert.True(preview.IsSuccess, preview.Error);
        Assert.True(preview.Value.CanImport);
        Assert.Equal(2, preview.Value.Files.Count(file => file.RelativePath.EndsWith(".nif")));
        Assert.Single(preview.Value.Files, file => file.RelativePath == "textures/sample/blade.dds");
        Assert.Equal(3, preview.Value.Files.Count);
        Assert.Equal("sample\\first.nif", preview.Value.FirstPersonPath);
    }
}
