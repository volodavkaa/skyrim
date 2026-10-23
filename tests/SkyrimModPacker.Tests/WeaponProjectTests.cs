using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NSubstitute;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure.PluginBuilder;
using Xunit;

namespace SkyrimModPacker.Tests;

public partial class WeaponProjectTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WeaponProjectTests", Guid.NewGuid().ToString("N"));
    private readonly PluginBuilderService _builder = new();
    private string Data => Path.Combine(_root, "Game", "Data");
    private string Output => Path.Combine(_root, "projects");
    private WeaponProjectService Service => new(_builder);

    public WeaponProjectTests()
    {
        Directory.CreateDirectory(Data);
        var master = new SkyrimMod(ModKey.FromNameAndExtension("Skyrim.esm"), SkyrimRelease.SkyrimSE);
        master.Weapons.Add(new Weapon(new FormKey(master.ModKey, 0x12EB7), SkyrimRelease.SkyrimSE)
        {
            EditorID = "IronSword", Name = "Iron Sword", BasicStats = new WeaponBasicStats { Damage = 7, Weight = 9, Value = 25 },
            Data = new WeaponData { AnimationType = WeaponAnimationType.OneHandSword, Speed = 1, Reach = 1 },
            Model = new Model { File = @"Weapons\Iron\Sword.nif" }
        });
        master.WriteToBinary(Path.Combine(Data, "Skyrim.esm"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private CreatePluginRequest Request(string extension = ".esp", string? output = null) => new()
    {
        PluginFileName = "MySword" + extension, OutputDirectory = output ?? Output, GameDataPath = Data, Masters = []
    };

    private async Task<string> SourceAsync()
    {
        var path = Path.Combine(_root, "source");
        var created = await _builder.CreateAsync(new CreatePluginRequest
        {
            PluginFileName = "Existing.esp", OutputDirectory = path, GameDataPath = Data, Masters = ["Skyrim.esm"]
        });
        Assert.True(created.IsSuccess, created.Error);
        var cloned = await _builder.CloneSwordAsync(new CloneSwordRequest
        {
            PluginPath = created.Value, GameDataPath = Data, TemplateFormId = 0x12EB7, EditorId = "CustomSword", Name = "Custom sword"
        });
        Assert.True(cloned.IsSuccess, cloned.Error);
        var installed = Path.Combine(Data, "Existing.esp");
        File.Copy(created.Value, installed);
        return installed;
    }

    [Theory]
    [InlineData(".esp")]
    [InlineData(".esl")]
    public async Task CreateAndMoveProject_ReopensWithRelativePluginPathAndAutomaticMaster(string extension)
    {
        var result = await Service.CreateAsync(Request(extension));
        Assert.True(result.IsSuccess, result.Error);
        var folder = Path.GetDirectoryName(result.Value.ProjectFilePath)!;
        Assert.True(Directory.Exists(Path.Combine(folder, "meshes")));
        Assert.True(Directory.Exists(Path.Combine(folder, "textures")));
        using (var mod = SkyrimMod.CreateFromBinaryOverlay(result.Value.PluginPath, SkyrimRelease.SkyrimSE))
            Assert.Equal("Skyrim.esm", Assert.Single(mod.ModHeader.MasterReferences).Master.FileName.String);
        var moved = Path.Combine(_root, "moved");
        Directory.Move(folder, moved);
        var opened = await Service.OpenAsync(Path.Combine(moved, WeaponProjectService.ProjectFileName), Data);
        Assert.True(opened.IsSuccess, opened.Error);
        Assert.Equal(Path.Combine(moved, "MySword" + extension), opened.Value.PluginPath);
    }

    [Fact]
    public async Task ImportFromGame_PreservesBytesNameIdsAndSource_AfterEditingCopy()
    {
        var source = await SourceAsync();
        var original = await File.ReadAllBytesAsync(source);
        var result = await Service.ImportAsync(source, Output, Data);
        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("Existing.esp", Path.GetFileName(result.Value.PluginPath));
        Assert.Equal(original, await File.ReadAllBytesAsync(result.Value.PluginPath));
        var weapons = await _builder.GetWeaponsAsync(result.Value.PluginPath);
        var sword = Assert.Single(weapons.Value);
        Assert.Equal(0x800u, sword.FormId);
        var update = await _builder.UpdateSwordAsync(new UpdateSwordRequest
        {
            PluginPath = result.Value.PluginPath, GameDataPath = Data, FormId = sword.FormId,
            EditorId = sword.EditorId, Name = "Edited copy", Damage = 50, Weight = 5, Value = 100
        });
        Assert.True(update.IsSuccess, update.Error);
        Assert.Equal(original, await File.ReadAllBytesAsync(source));
        Assert.Equal(original, await File.ReadAllBytesAsync(result.Value.PluginPath + ".bak"));
        Assert.False(File.Exists(source + ".bak"));
    }

    [Fact]
    public async Task ExistingProject_IsNeverOverwrittenByCreateOrImport()
    {
        var source = await SourceAsync();
        var first = await Service.ImportAsync(source, Output, Data);
        Assert.True(first.IsSuccess, first.Error);
        var bytes = await File.ReadAllBytesAsync(first.Value.PluginPath);
        Assert.True((await Service.ImportAsync(source, Output, Data)).IsFailure);
        Assert.True((await Service.CreateAsync(new CreatePluginRequest
        {
            PluginFileName = "Existing.esp", OutputDirectory = Output, GameDataPath = Data, Masters = []
        })).IsFailure);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(first.Value.PluginPath));
        Assert.Empty(Directory.GetDirectories(Output, ".project-*"));
    }

    [Theory]
    [InlineData("master")]
    [InlineData("empty")]
    [InlineData("corrupt")]
    public async Task UnsupportedImport_CreatesNoProject(string kind)
    {
        var source = Path.Combine(Data, "Skyrim.esm");
        if (kind == "empty")
        {
            var created = await _builder.CreateAsync(new CreatePluginRequest
            {
                PluginFileName = "Empty.esp", OutputDirectory = Path.Combine(_root, "empty"), GameDataPath = Data, Masters = []
            });
            source = created.Value;
        }
        if (kind == "corrupt") { source = Path.Combine(_root, "Corrupt.esp"); await File.WriteAllTextAsync(source, "not a plugin"); }
        var result = await Service.ImportAsync(source, Output, Data);
        Assert.True(result.IsFailure);
        Assert.False(Directory.Exists(Output));
    }

    [Theory]
    [InlineData("..\\outside.esp", 1)]
    [InlineData("Skyrim.esm", 1)]
    [InlineData("Missing.esp", 1)]
    [InlineData("Sword.esp", 2)]
    public async Task InvalidManifest_IsRejectedWithoutWriting(string plugin, int version)
    {
        Directory.CreateDirectory(Output);
        var manifest = Path.Combine(Output, WeaponProjectService.ProjectFileName);
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new { Name = "Invalid", Version = version, PluginFileName = plugin }));
        var result = await Service.OpenAsync(manifest, Data);
        Assert.True(result.IsFailure);
        Assert.Single(Directory.GetFiles(Output));
    }

    [Fact]
    public async Task GameOutput_IsRejectedAndGameManifestCannotBeOpenedForEditing()
    {
        Assert.True((await Service.CreateAsync(Request(output: Data))).IsFailure);
        var source = await SourceAsync();
        Assert.True((await Service.ImportAsync(source, Data, Data)).IsFailure);
        Assert.True((await Service.OpenAsync(Path.Combine(Data, WeaponProjectService.ProjectFileName), Data)).IsFailure);
        Assert.Empty(Directory.GetDirectories(Data));
    }

    [Fact]
    public async Task FailedCreation_CleansOwnedStagingAndCanRetry()
    {
        var failed = await Service.CreateAsync(new CreatePluginRequest
        {
            PluginFileName = "MySword.esp", OutputDirectory = Output + Path.DirectorySeparatorChar, GameDataPath = Data, Masters = ["Missing.esm"]
        });
        Assert.True(failed.IsFailure);
        Assert.Empty(Directory.GetFileSystemEntries(Output));
        Assert.True((await Service.CreateAsync(Request())).IsSuccess);
    }

    [Fact]
    public async Task CancelledCreationAfterWritingPlugin_CleansIncompleteWorkspace()
    {
        using var cancellation = new CancellationTokenSource();
        var builder = Substitute.For<IPluginBuilderService>();
        builder.CreateAsync(Arg.Any<CreatePluginRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var request = call.Arg<CreatePluginRequest>();
            File.WriteAllText(Path.Combine(request.OutputDirectory, request.PluginFileName), "partial plugin");
            cancellation.Cancel();
            return Task.FromCanceled<Result<string>>(cancellation.Token);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WeaponProjectService(builder).CreateAsync(Request(), cancellation.Token));
        Assert.Empty(Directory.GetFileSystemEntries(Output));
    }

    [Fact]
    public async Task CancelledCreation_WritesNothing()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.CreateAsync(Request(), cancellation.Token));
        Assert.False(Directory.Exists(Output));
    }
}
