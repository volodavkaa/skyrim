using SkyrimModPacker.Infrastructure.ModelPreparation;
using Xunit;

namespace SkyrimModPacker.Tests;

public class ModelPreparationRunnerTests
{
    [Theory]
    [InlineData(5, 0, 0)]
    [InlineData(0, 45, 0)]
    [InlineData(0, 0, 5)]
    public async Task LiveRotationDirectionMatchesExportedWeaponGeometry(double x, double y, double z)
    {
        var data = Environment.GetEnvironmentVariable("SKYRIM_TEST_DATA");
        var blender = Environment.GetEnvironmentVariable("SKYRIM_TEST_BLENDER");
        var addon = Environment.GetEnvironmentVariable("SKYRIM_TEST_ADDON");
        var archive = Environment.GetEnvironmentVariable("SKYRIM_TEST_SINGLE_ZIP");
        if (new[] { data, blender, addon, archive }.Any(string.IsNullOrWhiteSpace)) return;
        var root = Path.Combine(Path.GetTempPath(), "skyrim-rotation-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var first = await ModelPreparationRunner.PrepareDraftAsync(archive!, data!, blender!, addon!, root, false);
            using var second = await ModelPreparationRunner.PrepareDraftAsync(archive!, data!, blender!, addon!, root, false,
                adjustments: new(RotationXDegrees: x, RotationYDegrees: y, RotationZDegrees: z));
            static System.Numerics.Vector3[] Weapon(ModelPreparationDraft draft)
            {
                var bytes = File.ReadAllBytes(Path.Combine(draft.StagingDirectory, draft.ThirdPersonPath));
                return SkyrimModPacker.Infrastructure.Preview.SseNifReader.Read(bytes).Meshes
                    .First(mesh => mesh.Name == "IronLongSword01:0").Positions;
            }
            var before = Weapon(first); var after = Weapon(second);
            Assert.Equal(before.Length, after.Length);
            static System.Windows.Media.Media3D.Vector3D Display(System.Numerics.Vector3 p) => new(p.X, p.Z, -p.Y);
            var matrix = SkyrimModPacker.UI.Services.FitRotationPreview.Difference(new(), new(x, y, z));
            var maxError = 0.0;
            for (var i = 1; i < before.Length; i++)
            {
                var expected = matrix.Transform(Display(before[i] - before[0]));
                var actual = Display(after[i] - after[0]);
                maxError = Math.Max(maxError, (expected - actual).Length);
            }
            Assert.True(maxError < 0.03, $"Preview rotation differs from NIF geometry by {maxError}");
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ShaderSettingsRoundTripBothViewsAndAllMaterialSlots(bool defaultShader)
    {
        var data = Environment.GetEnvironmentVariable("SKYRIM_TEST_DATA");
        var blender = Environment.GetEnvironmentVariable("SKYRIM_TEST_BLENDER");
        var addon = Environment.GetEnvironmentVariable("SKYRIM_TEST_ADDON");
        var archive = Environment.GetEnvironmentVariable("SKYRIM_TEST_TWO_MATERIAL_ZIP");
        var texconv = Environment.GetEnvironmentVariable("TEXCONV_PATH");
        if (new[] { data, blender, addon, archive, texconv }.Any(string.IsNullOrWhiteSpace)) return;
        var root = Path.Combine(Path.GetTempPath(), "skyrim-default-shader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var mappings = new[] {
                new ModelPreparationMaterialTexture(0, "BaseColor0.png", "Normal0.png", defaultShader ? "ignored.png" : null),
                new ModelPreparationMaterialTexture(1, "BaseColor1.png", "Normal1.png", defaultShader ? "ignored.png" : null) };
            using var draft = await ModelPreparationRunner.PrepareDraftAsync(archive!, data!, blender!, addon!, root,
                true, textures: new("", "", texconv!, Materials: mappings, MaxTextureSize: 256), defaultShader: defaultShader, vertexColor: 0.5,
                shaderSettings: new(2.25, 1.75, 0.2, 0.4, 0.7));
            using var report = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(draft.StagingDirectory, "report.json")));
            Assert.Equal(defaultShader ? "Default" : "template", report.RootElement.GetProperty("weapon_shader").GetString());
            Assert.Equal(0.5, report.RootElement.GetProperty("vertex_color").GetDouble());
            Assert.Equal(256, report.RootElement.GetProperty("textures").GetProperty("max_texture_size").GetInt32());
            foreach (var path in Directory.GetFiles(Path.Combine(draft.StagingDirectory, "textures"), "*.dds", SearchOption.AllDirectories))
            {
                var texture = SkyrimModPacker.Infrastructure.Preview.DdsResourceInspector.Inspect(path);
                Assert.True(Math.Max(texture.Width, texture.Height) <= 256);
            }
            Assert.All(report.RootElement.GetProperty("views").EnumerateArray(), view =>
                Assert.Equal(2, view.GetProperty("weapon_parts").GetArrayLength()));
            if (!defaultShader)
                foreach (var inspection in Directory.GetFiles(draft.StagingDirectory, "inspection-*.json"))
                {
                    using var imported = System.Text.Json.JsonDocument.Parse(File.ReadAllText(inspection));
                    var matching = imported.RootElement.EnumerateArray().Where(item =>
                        item.TryGetProperty("material_nodes", out var nodes) && nodes.GetArrayLength() == 1 &&
                        nodes[0].GetProperty("shader_type").GetString() == "Environment_Map" &&
                        Math.Abs(nodes[0].GetProperty("environment_strength").GetDouble() - 2.25) < 0.001).ToArray();
                    Assert.True(matching.Length >= 2);
                }
            Assert.Empty(Directory.GetFiles(Path.Combine(draft.StagingDirectory, "textures"), "*_em.dds", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void GripFractionMustBeFiniteAndWithinWeaponLength()
    {
        new ModelPreparationAdjustments(SourceGripFraction: 0.3).Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ModelPreparationAdjustments(SourceGripFraction: double.NaN).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ModelPreparationAdjustments(SourceGripFraction: 1.1).Validate());
    }

    [Fact]
    public void DraftPublishesOnlyOnceAndKeepsItsFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "skyrim-model-draft-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var staging = Path.Combine(root, ".model-preparation-test");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "report.json"), "{}");
        try
        {
            using var draft = new ModelPreparationDraft(staging, root, "example", "meshes/example/example.nif",
                "meshes/example/example_1stperson.nif");
            var published = draft.Publish();
            Assert.True(File.Exists(Path.Combine(published, "report.json")));
            Assert.False(Directory.Exists(staging));
            Assert.Throws<InvalidOperationException>(() => draft.Publish());
            draft.Dispose();
            Assert.True(File.Exists(Path.Combine(published, "report.json")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void DisposedDraftRemovesUnpublishedFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "skyrim-model-draft-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var staging = Path.Combine(root, ".model-preparation-test");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "report.json"), "{}");
        try
        {
            using (var draft = new ModelPreparationDraft(staging, root, "example", "a.nif", "b.nif")) { }
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CancelledPreparationLeavesNoPublishedOrStagingFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "skyrim-model-runner-test-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "game", "Data");
        var addon = Path.Combine(root, "addon");
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(Path.Combine(addon, "io_scene_nifly"));
        foreach (var file in new[] { "source.zip", "blender.exe", "export.py" })
            File.WriteAllBytes(Path.Combine(root, file), []);
        File.WriteAllBytes(Path.Combine(root, "model_preparation_template_inspect.py"), []);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ModelPreparationRunner.RunAsync(
                Path.Combine(root, "source.zip"), data, Path.Combine(root, "blender.exe"), addon,
                output, false, cancellation.Token, Path.Combine(root, "export.py")));
            if (Directory.Exists(output)) Assert.Empty(Directory.EnumerateFileSystemEntries(output));
        }
        finally { Directory.Delete(root, true); }
    }
}
