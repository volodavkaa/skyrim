using SkyrimModPacker.Infrastructure.ModelPreparation;
using Xunit;

namespace SkyrimModPacker.Tests;

public class ModelPreparationRunnerTests
{
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
            Assert.Empty(Directory.EnumerateFileSystemEntries(output));
        }
        finally { Directory.Delete(root, true); }
    }
}
