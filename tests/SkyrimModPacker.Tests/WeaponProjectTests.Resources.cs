using System.Text.Json;
using Xunit;

namespace SkyrimModPacker.Tests;

public partial class WeaponProjectTests
{
    private async Task<string> AssetAsync(string relative, string content = "fixture resource")
    {
        var path = Path.Combine(_root, "assets", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (relative.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) await File.WriteAllBytesAsync(path, PreviewFixtures.ResourceDds(content));
        else await File.WriteAllTextAsync(path, content);
        return path;
    }

    [Theory]
    [InlineData(@"loose\Sword.nif", @"MySword\Sword.nif")]
    [InlineData(@"export\meshes\Custom\Sword.nif", @"Custom\Sword.nif")]
    public async Task ImportModel_CopiesBytesAndPersistsRelativePath_AcrossProjectMove(string sourceRelative, string expected)
    {
        var source = await AssetAsync(sourceRelative);
        var project = (await Service.CreateAsync(Request())).Value;
        var originalPlugin = await File.ReadAllBytesAsync(project.PluginPath);
        var oldManifest = await File.ReadAllBytesAsync(project.ProjectFilePath);
        var imported = await Service.ImportModelAsync(project.ProjectFilePath, source, Data);
        Assert.True(imported.IsSuccess, imported.Error);
        Assert.Equal(expected, imported.Value);
        Assert.Equal(oldManifest, await File.ReadAllBytesAsync(project.ProjectFilePath + ".bak"));
        var valid = await Service.ValidateModelAsync(project.ProjectFilePath, imported.Value, Data);
        Assert.True(valid.IsSuccess, valid.Error);
        Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(valid.Value));
        Assert.Equal(originalPlugin, await File.ReadAllBytesAsync(project.PluginPath));
        var moved = Path.Combine(_root, "moved-project");
        Directory.Move(Path.GetDirectoryName(project.ProjectFilePath)!, moved);
        var manifest = Path.Combine(moved, "mod-project.json");
        Assert.True((await Service.OpenAsync(manifest, Data)).IsSuccess);
        var assets = await Service.ValidateResourcesAsync(manifest, Data);
        Assert.True(assets.IsSuccess, assets.Error);
        Assert.Equal("meshes/" + expected.Replace('\\', '/'), Assert.Single(assets.Value));
    }

    [Fact]
    public async Task TextureFolderAndLooseFiles_PreservePaths_IgnoreNonDdsAndKeepSources()
    {
        var file = await AssetAsync(@"export\textures\Custom\blade.dds", "diffuse");
        await AssetAsync(@"export\textures\Custom\blade_n.dds", "normal");
        await AssetAsync(@"export\textures\notes.txt");
        var loose = await AssetAsync(@"loose\metal.dds", "metal");
        var project = (await Service.CreateAsync(Request())).Value;
        var imported = await Service.ImportTexturesAsync(project.ProjectFilePath, [Path.Combine(_root, "assets", "export", "textures")], Data);
        Assert.True(imported.IsSuccess, imported.Error);
        Assert.Equal(new[] { "textures/Custom/blade.dds", "textures/Custom/blade_n.dds" }, imported.Value.OrderBy(x => x, StringComparer.Ordinal));
        var other = await Service.ImportTexturesAsync(project.ProjectFilePath, [loose], Data, "Custom/Metal");
        Assert.True(other.IsSuccess, other.Error);
        Assert.Equal("textures/Custom/Metal/metal.dds", Assert.Single(other.Value));
        Assert.Equal(PreviewFixtures.ResourceDds("diffuse"), await File.ReadAllBytesAsync(file));
        var assets = await Service.ValidateResourcesAsync(project.ProjectFilePath, Data);
        Assert.Equal(3, assets.Value.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SelectedSubfolderOrFile_UnderTextures_KeepsItsOriginalRelativePath(bool folder)
    {
        var file = await AssetAsync(@"textures\Custom\nested\blade.dds");
        var project = (await Service.CreateAsync(Request())).Value;
        var imported = await Service.ImportTexturesAsync(project.ProjectFilePath,
            [folder ? Path.Combine(_root, "assets", "textures", "Custom") : file], Data, "IgnoredForSourcesUnderTextures");
        Assert.True(imported.IsSuccess, imported.Error);
        Assert.Equal("textures/Custom/nested/blade.dds", Assert.Single(imported.Value));
    }

    [Fact]
    public async Task FolderOutsideTextures_KeepsFolderNameAndNestedStructure()
    {
        await AssetAsync(@"Custom\nested\blade.dds");
        var project = (await Service.CreateAsync(Request())).Value;
        var imported = await Service.ImportTexturesAsync(project.ProjectFilePath, [Path.Combine(_root, "assets", "Custom")], Data);
        Assert.True(imported.IsSuccess, imported.Error);
        Assert.Equal("textures/Custom/nested/blade.dds", Assert.Single(imported.Value));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("C:/outside")]
    [InlineData("x/../../outside")]
    [InlineData("x:stream")]
    public async Task UnsafeDestination_DoesNotWriteResourcesOrManifest(string destination)
    {
        var source = await AssetAsync("blade.dds");
        var project = (await Service.CreateAsync(Request())).Value;
        var original = await File.ReadAllBytesAsync(project.ProjectFilePath);
        var imported = await Service.ImportTexturesAsync(project.ProjectFilePath, [source], Data, destination);
        Assert.True(imported.IsFailure);
        Assert.Equal(original, await File.ReadAllBytesAsync(project.ProjectFilePath));
        Assert.Empty(Directory.GetFiles(Path.Combine(Path.GetDirectoryName(project.PluginPath)!, "textures"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ConflictingBatchAndReimport_DoNotOverwriteFilesOrMetadata()
    {
        var first = await AssetAsync(@"one\blade.dds", "first");
        var second = await AssetAsync(@"two\blade.dds", "second");
        var project = (await Service.CreateAsync(Request())).Value;
        var manifest = await File.ReadAllBytesAsync(project.ProjectFilePath);
        Assert.True((await Service.ImportTexturesAsync(project.ProjectFilePath, [first, second], Data)).IsFailure);
        Assert.Equal(manifest, await File.ReadAllBytesAsync(project.ProjectFilePath));
        Assert.True((await Service.ImportTexturesAsync(project.ProjectFilePath, [first], Data)).IsSuccess);
        manifest = await File.ReadAllBytesAsync(project.ProjectFilePath);
        Assert.True((await Service.ImportTexturesAsync(project.ProjectFilePath, [second], Data)).IsFailure);
        Assert.Equal(manifest, await File.ReadAllBytesAsync(project.ProjectFilePath));
        Assert.Equal(PreviewFixtures.ResourceDds("first"), await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(project.PluginPath)!, "textures", "blade.dds")));
    }

    [Fact]
    public async Task FilesAlreadyInProject_CanBeRegisteredWithoutRecopying()
    {
        var project = (await Service.CreateAsync(Request())).Value;
        var file = Path.Combine(Path.GetDirectoryName(project.PluginPath)!, "meshes", "blade.nif");
        await File.WriteAllTextAsync(file, "fixture");
        Assert.True((await Service.ImportModelAsync(project.ProjectFilePath, file, Data)).IsSuccess);
        Assert.True((await Service.ImportModelAsync(project.ProjectFilePath, file, Data)).IsSuccess);
        Assert.Single((await Service.GetResourcesAsync(project.ProjectFilePath, Data)).Value);
        Assert.Equal("fixture", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task MissingModelAndTexture_AreReportedAfterReopening()
    {
        var project = (await Service.CreateAsync(Request())).Value;
        var nif = await AssetAsync("blade.nif");
        var dds = await AssetAsync("blade.dds");
        var imported = await Service.ImportModelAsync(project.ProjectFilePath, nif, Data);
        await Service.ImportTexturesAsync(project.ProjectFilePath, [dds], Data);
        var folder = Path.GetDirectoryName(project.PluginPath)!;
        File.Delete(Path.Combine(folder, "meshes", imported.Value));
        File.Delete(Path.Combine(folder, "textures", "blade.dds"));
        Assert.True((await Service.OpenAsync(project.ProjectFilePath, Data)).IsSuccess);
        var validation = await Service.ValidateResourcesAsync(project.ProjectFilePath, Data);
        Assert.True(validation.IsFailure);
        Assert.Contains("blade.nif", validation.Error);
        Assert.Contains("blade.dds", validation.Error);
        Assert.True((await Service.ValidateModelAsync(project.ProjectFilePath, imported.Value, Data)).IsFailure);
    }

    [Theory]
    [InlineData("blade.txt", "data")]
    [InlineData("empty.nif", "")]
    public async Task InvalidModel_IsRejected(string name, string content)
    {
        var source = await AssetAsync(name, content);
        var project = (await Service.CreateAsync(Request())).Value;
        var imported = await Service.ImportModelAsync(project.ProjectFilePath, source, Data);
        Assert.True(imported.IsFailure);
        Assert.Empty((await Service.GetResourcesAsync(project.ProjectFilePath, Data)).Value);
    }

    [Fact]
    public async Task MetadataWriteFailure_RollsBackCopiedResourcesAndNewDirectories()
    {
        var project = (await Service.CreateAsync(Request())).Value;
        Directory.CreateDirectory(project.ProjectFilePath + ".bak"); // Force a late failure after copying assets.
        var source = await AssetAsync("blade.dds");
        var manifest = await File.ReadAllBytesAsync(project.ProjectFilePath);
        var result = await Service.ImportTexturesAsync(project.ProjectFilePath, [source], Data, "New/Subfolder");
        Assert.True(result.IsFailure);
        Assert.Equal(manifest, await File.ReadAllBytesAsync(project.ProjectFilePath));
        var folder = Path.GetDirectoryName(project.PluginPath)!;
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(folder, "textures")));
        Assert.Empty(Directory.GetDirectories(folder, ".assets-*"));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
    }

    [Fact]
    public async Task ResourcesReadFromGame_AreCopiedWithoutSourceWrites_AndGameProjectIsRejected()
    {
        var source = Path.Combine(Data, "Sword.nif");
        await File.WriteAllTextAsync(source, "game model");
        var project = (await Service.CreateAsync(Request())).Value;
        Assert.True((await Service.ImportModelAsync(project.ProjectFilePath, source, Data)).IsSuccess);
        Assert.Equal("game model", await File.ReadAllTextAsync(source));
        var gameManifest = Path.Combine(Data, "mod-project.json");
        File.Copy(project.ProjectFilePath, gameManifest);
        Assert.True((await Service.ImportModelAsync(gameManifest, source, Data)).IsFailure);
        Assert.False(File.Exists(gameManifest + ".bak"));
        Assert.False(Directory.Exists(Path.Combine(Data, "meshes")));
    }

    [Fact]
    public async Task MaliciousResourceManifest_CannotEscapeWorkspace()
    {
        var project = (await Service.CreateAsync(Request())).Value;
        await File.WriteAllTextAsync(project.ProjectFilePath, JsonSerializer.Serialize(new
        {
            Version = 1, Name = "MySword", PluginFileName = "MySword.esp", Resources = new[] { "meshes/../../outside.nif" }
        }));
        Assert.True((await Service.OpenAsync(project.ProjectFilePath, Data)).IsFailure);
        Assert.True((await Service.ValidateResourcesAsync(project.ProjectFilePath, Data)).IsFailure);
    }

    [Fact]
    public async Task CancelledImport_LeavesManifestAndResourcesUnchanged()
    {
        var project = (await Service.CreateAsync(Request())).Value;
        var source = await AssetAsync("blade.nif");
        var manifest = await File.ReadAllBytesAsync(project.ProjectFilePath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.ImportModelAsync(project.ProjectFilePath, source, Data, cancellation.Token));
        Assert.Equal(manifest, await File.ReadAllBytesAsync(project.ProjectFilePath));
        Assert.Empty((await Service.GetResourcesAsync(project.ProjectFilePath, Data)).Value);
    }
}
