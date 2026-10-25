using SkyrimModPacker.Infrastructure.ModelPreparation;
using Xunit;

namespace SkyrimModPacker.Tests;

public class ModelPreparationRunnerTests
{
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
