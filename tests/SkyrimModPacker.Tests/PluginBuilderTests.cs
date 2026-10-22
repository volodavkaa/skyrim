using System.IO.Compression;
using Microsoft.Extensions.DependencyInjection;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkyrimModPacker.Application;
using SkyrimModPacker.Application.Services;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure;
using SkyrimModPacker.Infrastructure.Esl;
using SkyrimModPacker.Infrastructure.PluginBuilder;
using SkyrimModPacker.Infrastructure.Persistence;
using SkyrimModPacker.UI.ViewModels;
using Xunit;

namespace SkyrimModPacker.Tests;

public class PluginBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PluginBuilderTests", Guid.NewGuid().ToString("N"));
    private string DataPath => Path.Combine(_root, "Game", "Data");
    private string OutputPath => Path.Combine(_root, "workspace");
    private const uint SwordId = 0x12EB7;
    private const uint AxeId = 0x12EB8;

    public PluginBuilderTests()
    {
        Directory.CreateDirectory(DataPath);
        var master = new SkyrimMod(ModKey.FromNameAndExtension("Skyrim.esm"), SkyrimRelease.SkyrimSE);
        master.Keywords.Add(new Keyword(new FormKey(master.ModKey, 0x1E711), SkyrimRelease.SkyrimSE) { EditorID = "WeapTypeSword" });
        master.Weapons.Add(new Weapon(new FormKey(master.ModKey, SwordId), SkyrimRelease.SkyrimSE)
        {
            EditorID = "IronSword",
            Name = "Iron Sword",
            BasicStats = new WeaponBasicStats { Damage = 7, Weight = 9, Value = 25 },
            Data = new WeaponData { AnimationType = WeaponAnimationType.OneHandSword, Speed = 1, Reach = 1 },
            Model = new Model { File = @"Weapons\Iron\LongSword.nif" },
            Keywords = [new FormLink<IKeywordGetter>(new FormKey(master.ModKey, 0x1E711))]
        });
        master.Weapons.Add(new Weapon(new FormKey(master.ModKey, AxeId), SkyrimRelease.SkyrimSE)
        {
            EditorID = "IronWarAxe",
            Name = "Iron War Axe",
            BasicStats = new WeaponBasicStats { Damage = 8, Weight = 11, Value = 30 },
            Data = new WeaponData { AnimationType = WeaponAnimationType.OneHandAxe }
        });
        master.WriteToBinary(Path.Combine(DataPath, "Skyrim.esm"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private CreatePluginRequest CreateRequest(string fileName = "Swords.esp", IReadOnlyList<string>? masters = null) => new()
    {
        PluginFileName = fileName,
        OutputDirectory = OutputPath,
        GameDataPath = DataPath,
        Masters = masters ?? ["Skyrim.esm"]
    };

    private CloneSwordRequest CloneRequest(string path, string editorId = "CustomSword", uint template = SwordId) => new()
    {
        PluginPath = path,
        GameDataPath = DataPath,
        TemplateFormId = template,
        EditorId = editorId,
        Name = "Крижаний меч",
        Damage = 19,
        Weight = 6.5f,
        Value = 450
    };

    [Theory]
    [InlineData("Swords.esp", false)]
    [InlineData("Swords.esl", true)]
    public async Task CreateAndClone_UsesNewCompactRecord_PreservesVanillaLinksAndBackup(string name, bool isEsl)
    {
        var service = new PluginBuilderService();
        var originalMaster = await File.ReadAllBytesAsync(Path.Combine(DataPath, "Skyrim.esm"));
        var creation = await service.CreateAsync(CreateRequest(name));
        Assert.True(creation.IsSuccess, creation.Error);
        var emptyPlugin = await File.ReadAllBytesAsync(creation.Value);
        Assert.False(File.Exists(creation.Value + ".bak"));
        using (var empty = SkyrimMod.CreateFromBinaryOverlay(creation.Value, SkyrimRelease.SkyrimSE))
        {
            Assert.Equal(isEsl, empty.IsSmallMaster);
            Assert.Equal(isEsl, empty.IsMaster);
            Assert.Equal(1.70f, empty.ModHeader.Stats.Version);
            Assert.Equal("Skyrim.esm", Assert.Single(empty.ModHeader.MasterReferences).Master.FileName.String);
            Assert.Empty(empty.EnumerateMajorRecords());
        }

        var clone = await service.CloneSwordAsync(CloneRequest(creation.Value));
        Assert.True(clone.IsSuccess, clone.Error);
        Assert.Equal(0x800u, clone.Value.FormId);
        Assert.Equal((ushort)19, clone.Value.Damage);
        Assert.Equal(6.5f, clone.Value.Weight);
        Assert.Equal(450u, clone.Value.Value);
        Assert.Equal(emptyPlugin, await File.ReadAllBytesAsync(creation.Value + ".bak"));
        using (var saved = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(creation.Value).WithUtf8Encoding().Construct())
        {
            var sword = Assert.Single(saved.Weapons);
            Assert.Equal(saved.ModKey, sword.FormKey.ModKey);
            Assert.Equal(0x800u, sword.FormKey.ID);
            Assert.Equal("CustomSword", sword.EditorID);
            Assert.Equal("Крижаний меч", sword.Name!.String);
            Assert.Equal(WeaponAnimationType.OneHandSword, sword.Data!.AnimationType);
            Assert.Equal(1f, sword.Data.Speed);
            Assert.Equal(1f, sword.Data.Reach);
            Assert.Equal(@"Weapons\Iron\LongSword.nif", sword.Model!.File.GivenPath);
            Assert.Equal(new FormKey(ModKey.FromNameAndExtension("Skyrim.esm"), 0x1E711), Assert.Single(sword.Keywords!).FormKey);
            Assert.Equal(0x801u, saved.ModHeader.Stats.NextFormID);
        }
        var analysis = await new EslService().AnalyzeAsync(creation.Value);
        Assert.True(analysis.IsSuccess, analysis.Error);
        Assert.True(analysis.Value.IsCompatible);
        Assert.Equal(originalMaster, await File.ReadAllBytesAsync(Path.Combine(DataPath, "Skyrim.esm")));
        Assert.Single(Directory.GetFiles(DataPath));
        Assert.Empty(Directory.GetDirectories(OutputPath, ".plugin-*"));
    }

    [Fact]
    public async Task Templates_IncludeOnlyOneHandedSwords()
    {
        var result = await new PluginBuilderService().GetSwordTemplatesAsync(DataPath);
        Assert.True(result.IsSuccess, result.Error);
        var sword = Assert.Single(result.Value);
        Assert.Equal(SwordId, sword.FormId);
        Assert.Equal("IronSword", sword.EditorId);
        Assert.Equal("Iron Sword", sword.Name);
    }

    [Fact]
    public async Task Clone_RepeatedCallsAllocateNewIds_AndRefreshBackup()
    {
        var service = new PluginBuilderService();
        var created = await service.CreateAsync(CreateRequest());
        Assert.True(created.IsSuccess, created.Error);
        Assert.True((await service.CloneSwordAsync(CloneRequest(created.Value, "FirstSword"))).IsSuccess);
        var firstPlugin = await File.ReadAllBytesAsync(created.Value);
        var second = await service.CloneSwordAsync(CloneRequest(created.Value, "SecondSword"));
        Assert.True(second.IsSuccess, second.Error);
        Assert.Equal(0x801u, second.Value.FormId);
        Assert.Equal(firstPlugin, await File.ReadAllBytesAsync(created.Value + ".bak"));
        using var saved = SkyrimMod.CreateFromBinaryOverlay(created.Value, SkyrimRelease.SkyrimSE);
        Assert.Equal(new[] { 0x800u, 0x801u }, saved.Weapons.Select(w => w.FormKey.ID));
    }

    [Theory]
    [InlineData(AxeId, "AxeClone")]
    [InlineData(0x123456u, "MissingClone")]
    [InlineData(SwordId, "1InvalidEditorId")]
    [InlineData(SwordId, "Invalid Editor Id")]
    public async Task Clone_InvalidRequest_LeavesPluginUnchanged(uint templateId, string editorId)
    {
        var service = new PluginBuilderService();
        var created = await service.CreateAsync(CreateRequest());
        Assert.True(created.IsSuccess, created.Error);
        var original = await File.ReadAllBytesAsync(created.Value);
        var result = await service.CloneSwordAsync(CloneRequest(created.Value, editorId, templateId));
        Assert.True(result.IsFailure);
        Assert.Equal(original, await File.ReadAllBytesAsync(created.Value));
        Assert.False(File.Exists(created.Value + ".bak"));
    }

    [Fact]
    public async Task Clone_DuplicateEditorId_IsRejectedWithoutChangingBackup()
    {
        var service = new PluginBuilderService();
        var created = await service.CreateAsync(CreateRequest());
        Assert.True(created.IsSuccess, created.Error);
        Assert.True((await service.CloneSwordAsync(CloneRequest(created.Value))).IsSuccess);
        var original = await File.ReadAllBytesAsync(created.Value);
        var backup = await File.ReadAllBytesAsync(created.Value + ".bak");
        Assert.True((await service.CloneSwordAsync(CloneRequest(created.Value, "customsword"))).IsFailure);
        Assert.Equal(original, await File.ReadAllBytesAsync(created.Value));
        Assert.Equal(backup, await File.ReadAllBytesAsync(created.Value + ".bak"));
    }

    [Fact]
    public async Task Create_RejectsOverwriteMissingMastersAndGameOutput()
    {
        var service = new PluginBuilderService();
        var created = await service.CreateAsync(CreateRequest());
        Assert.True(created.IsSuccess, created.Error);
        var original = await File.ReadAllBytesAsync(created.Value);
        Assert.True((await service.CreateAsync(CreateRequest())).IsFailure);
        Assert.True((await service.CreateAsync(CreateRequest("Missing.esp", ["Missing.esm"]))).IsFailure);
        Assert.True((await service.CreateAsync(CreateRequest("Self.esp", ["Self.esp"]))).IsFailure);
        Assert.True((await service.CreateAsync(new CreatePluginRequest
        {
            PluginFileName = "Blocked.esp", OutputDirectory = DataPath, GameDataPath = DataPath
        })).IsFailure);
        Assert.Equal(original, await File.ReadAllBytesAsync(created.Value));
        Assert.False(File.Exists(Path.Combine(DataPath, "Blocked.esp")));
    }

    [Theory]
    [InlineData("../Escape.esp")]
    [InlineData("Wrong.esm")]
    [InlineData("Wrong.txt")]
    public async Task Create_InvalidFilename_IsRejected(string name)
    {
        var result = await new PluginBuilderService().CreateAsync(CreateRequest(name));
        Assert.True(result.IsFailure);
        Assert.False(Directory.Exists(OutputPath));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task BuiltPlugin_PacksFromWorkspace_WithoutWritingToGame(bool useTemp, bool enableEsl)
    {
        var service = new PluginBuilderService();
        var created = await service.CreateAsync(CreateRequest());
        Assert.True(created.IsSuccess, created.Error);
        Assert.True((await service.CloneSwordAsync(CloneRequest(created.Value))).IsSuccess);
        var original = await File.ReadAllBytesAsync(created.Value);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplicationServices();
        services.AddInfrastructureServices(Path.Combine(_root, "appdata"));
        using var provider = services.BuildServiceProvider();
        var profile = new ModProfile { Name = "Swords", PluginFileName = "Swords.esp", PluginSourcePath = created.Value, EnableEsl = enableEsl };
        var scan = await provider.GetRequiredService<IModScanner>().ScanAsync(DataPath, profile);
        Assert.True(scan.IsSuccess, scan.Error);
        Assert.Equal("Swords.esp", Assert.Single(scan.Value).RelativePath);
        var result = await provider.GetRequiredService<PackingPipeline>().ExecuteAsync(profile, new AppSettings
        {
            SkyrimDataPath = DataPath, OutputDirectory = Path.Combine(_root, "archives"), UseTempDirectory = useTemp
        });
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        using var zip = ZipFile.OpenRead(result.OutputArchivePath);
        var entry = Assert.Single(zip.Entries);
        Assert.Equal("Swords.esp", entry.FullName);
        using var memory = new MemoryStream();
        await using (var stream = entry.Open())
            await stream.CopyToAsync(memory);
        Assert.Equal(enableEsl, (BitConverter.ToUInt32(memory.ToArray(), 8) & 0x200) != 0);
        var unpackedDirectory = Path.Combine(_root, "unpacked");
        Directory.CreateDirectory(unpackedDirectory);
        var unpackedPath = Path.Combine(unpackedDirectory, "Swords.esp");
        await File.WriteAllBytesAsync(unpackedPath, memory.ToArray());
        using (var unpacked = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(unpackedPath).WithUtf8Encoding().Construct())
        {
            var sword = Assert.Single(unpacked.Weapons);
            Assert.Equal("Крижаний меч", sword.Name!.String);
            Assert.Equal(0x800u, sword.FormKey.ID);
        }
        Assert.Equal(original, await File.ReadAllBytesAsync(created.Value));
        Assert.Single(Directory.GetFiles(DataPath));
    }

    [Fact]
    public async Task WorkspacePath_PersistsThroughProfileEditingAndDuplication()
    {
        var repo = new JsonProfileRepository(Path.Combine(_root, "appdata"));
        var path = Path.Combine(OutputPath, "Swords.esp");
        await repo.SaveAsync(new ModProfile { Name = "Swords", PluginFileName = "Swords.esp", PluginSourcePath = path });
        var vm = new ProfilesViewModel(repo);
        await vm.LoadProfilesAsync();
        Assert.Equal(path, vm.PluginSourcePath);
        await vm.SaveProfileCommand.ExecuteAsync(null);
        await vm.DuplicateProfileCommand.ExecuteAsync(null);
        Assert.All(await repo.LoadAllAsync(), p => Assert.Equal(path, p.PluginSourcePath));
    }

    [Fact]
    public async Task Clone_PreservesStatisticsByDefault_AndAddsSkyrimMaster()
    {
        var service = new PluginBuilderService();
        var created = await service.CreateAsync(CreateRequest(masters: []));
        Assert.True(created.IsSuccess, created.Error);
        var result = await service.CloneSwordAsync(new CloneSwordRequest
        {
            PluginPath = created.Value, GameDataPath = DataPath, TemplateFormId = SwordId,
            EditorId = "UnchangedStats", Name = "Another Iron Sword"
        });
        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal((ushort)7, result.Value.Damage);
        Assert.Equal(9f, result.Value.Weight);
        Assert.Equal(25u, result.Value.Value);
        using var saved = SkyrimMod.CreateFromBinaryOverlay(created.Value, SkyrimRelease.SkyrimSE);
        Assert.Equal("Skyrim.esm", Assert.Single(saved.ModHeader.MasterReferences).Master.FileName.String);
    }

    [Fact]
    public async Task Clone_LastCompactId_IsAllowed_ThenExhaustionLeavesPluginAndBackupUnchanged()
    {
        var service = new PluginBuilderService();
        var created = await service.CreateAsync(CreateRequest());
        Assert.True(created.IsSuccess, created.Error);
        var mod = SkyrimMod.CreateFromBinary(created.Value, SkyrimRelease.SkyrimSE);
        for (uint id = 0x800; id < 0xFFF; id++)
            mod.Keywords.Add(new Keyword(new FormKey(mod.ModKey, id), SkyrimRelease.SkyrimSE));
        mod.WriteToBinary(created.Value);
        var last = await service.CloneSwordAsync(CloneRequest(created.Value, "LastSword"));
        Assert.True(last.IsSuccess, last.Error);
        Assert.Equal(0xFFFu, last.Value.FormId);
        var before = await File.ReadAllBytesAsync(created.Value);
        var backup = await File.ReadAllBytesAsync(created.Value + ".bak");
        var exhausted = await service.CloneSwordAsync(CloneRequest(created.Value, "ExtraSword"));
        Assert.True(exhausted.IsFailure);
        Assert.Contains("exhausted", exhausted.Error);
        Assert.Equal(before, await File.ReadAllBytesAsync(created.Value));
        Assert.Equal(backup, await File.ReadAllBytesAsync(created.Value + ".bak"));
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public async Task Clone_InvalidWeight_IsRejected(float weight)
    {
        var service = new PluginBuilderService();
        var created = await service.CreateAsync(CreateRequest());
        Assert.True(created.IsSuccess, created.Error);
        var before = await File.ReadAllBytesAsync(created.Value);
        var result = await service.CloneSwordAsync(new CloneSwordRequest
        {
            PluginPath = created.Value, GameDataPath = DataPath, TemplateFormId = SwordId,
            EditorId = "BadWeightSword", Name = "Bad Weight", Weight = weight
        });
        Assert.True(result.IsFailure);
        Assert.Equal(before, await File.ReadAllBytesAsync(created.Value));
        Assert.False(File.Exists(created.Value + ".bak"));
    }

    [Fact]
    public async Task Clone_GameDestinationAndCancellation_DoNotChangeGame()
    {
        var service = new PluginBuilderService();
        var gamePlugin = Path.Combine(DataPath, "GameSword.esp");
        File.Copy(Path.Combine(DataPath, "Skyrim.esm"), gamePlugin);
        var before = await File.ReadAllBytesAsync(gamePlugin);
        Assert.True((await service.CloneSwordAsync(CloneRequest(gamePlugin))).IsFailure);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CreateAsync(CreateRequest(), cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CloneSwordAsync(CloneRequest(gamePlugin), cts.Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(gamePlugin));
        Assert.False(File.Exists(gamePlugin + ".bak"));
        Assert.False(Directory.Exists(OutputPath));
    }

    [Fact]
    public async Task Scanner_MissingWorkspacePlugin_DoesNotFallBackToData()
    {
        var scanner = new SkyrimModPacker.Infrastructure.FileSystem.ModScanner();
        var result = await scanner.ScanAsync(DataPath, new ModProfile
        {
            Name = "Missing", PluginFileName = "Missing.esp", PluginSourcePath = Path.Combine(OutputPath, "Missing.esp")
        });
        Assert.True(result.IsFailure);
        Assert.Contains("Workspace plugin not found", result.Error);
    }

    [Fact]
    public void Profile_RejectsRelativeOrMismatchedWorkspaceSource()
    {
        var profile = new ModProfile { Name = "Sword", PluginFileName = "Swords.esp", PluginSourcePath = "Swords.esp" };
        Assert.True(ModProfileValidator.Validate(profile).IsFailure);
        profile.PluginSourcePath = Path.Combine(OutputPath, "Other.esp");
        Assert.True(ModProfileValidator.Validate(profile).IsFailure);
        profile.PluginSourcePath = Path.Combine(OutputPath, "Swords.esp");
        Assert.True(ModProfileValidator.Validate(profile).IsSuccess);
    }

    [Fact]
    public async Task Clone_ReadsLocalizedVanillaTemplate_AndEmbedsInheritedDescription()
    {
        var masterPath = Path.Combine(DataPath, "Skyrim.esm");
        var master = SkyrimMod.CreateFromBinary(masterPath, SkyrimRelease.SkyrimSE);
        master.UsingLocalization = true;
        master.Weapons.First(w => w.FormKey.ID == SwordId).Description = "Template description";
        var stringsDirectory = Path.Combine(DataPath, "Strings");
        Directory.CreateDirectory(stringsDirectory);
        master.BeginWrite.ToPath(masterPath).WithLoadOrder(Array.Empty<ModKey>()).WithDataFolder(DataPath).Write();
        var originals = Directory.GetFiles(DataPath, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);

        var service = new PluginBuilderService();
        var templates = await service.GetSwordTemplatesAsync(DataPath);
        Assert.True(templates.IsSuccess, templates.Error);
        Assert.Equal("Iron Sword", Assert.Single(templates.Value).Name);
        var created = await service.CreateAsync(CreateRequest());
        Assert.True(created.IsSuccess, created.Error);
        var cloned = await service.CloneSwordAsync(CloneRequest(created.Value));
        Assert.True(cloned.IsSuccess, cloned.Error);
        using var saved = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(created.Value).WithUtf8Encoding().Construct();
        Assert.False(saved.UsingLocalization);
        Assert.Equal("Template description", Assert.Single(saved.Weapons).Description!.String);
        foreach (var (path, bytes) in originals)
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(originals.Count, Directory.GetFiles(DataPath, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task Create_ValidatesMasterDependenciesAndPreservesSelectedOrder()
    {
        var update = new SkyrimMod(ModKey.FromNameAndExtension("Update.esm"), SkyrimRelease.SkyrimSE);
        update.Keywords.Add(new Keyword(new FormKey(ModKey.FromNameAndExtension("Skyrim.esm"), 0x1E711), SkyrimRelease.SkyrimSE));
        update.WriteToBinary(Path.Combine(DataPath, "Update.esm"));
        var service = new PluginBuilderService();
        var invalid = await service.CreateAsync(CreateRequest("WrongOrder.esp", ["Update.esm", "Skyrim.esm"]));
        Assert.True(invalid.IsFailure);
        Assert.Contains("parent masters", invalid.Error);
        Assert.False(File.Exists(Path.Combine(OutputPath, "WrongOrder.esp")));
        var valid = await service.CreateAsync(CreateRequest(masters: ["Skyrim.esm", "Update.esm"]));
        Assert.True(valid.IsSuccess, valid.Error);
        Assert.True((await service.CloneSwordAsync(CloneRequest(valid.Value))).IsSuccess);
        using var saved = SkyrimMod.CreateFromBinaryOverlay(valid.Value, SkyrimRelease.SkyrimSE);
        Assert.Equal(new[] { "Skyrim.esm", "Update.esm" }, saved.ModHeader.MasterReferences.Select(m => m.Master.FileName.String));
    }

    [Fact]
    public async Task DeleteSword_ReferencedRecord_IsRejectedWithoutChangingPluginOrBackup()
    {
        var service = new PluginBuilderService();
        var created = await service.CreateAsync(CreateRequest());
        Assert.True(created.IsSuccess, created.Error);
        Assert.True((await service.CloneSwordAsync(CloneRequest(created.Value))).IsSuccess);
        var mod = SkyrimMod.CreateFromBinary(created.Value, SkyrimRelease.SkyrimSE);
        var recipe = new ConstructibleObject(new FormKey(mod.ModKey, 0x801), SkyrimRelease.SkyrimSE);
        recipe.CreatedObject.SetTo(new FormKey(mod.ModKey, 0x800));
        mod.ConstructibleObjects.Add(recipe);
        mod.WriteToBinary(created.Value);
        var original = await File.ReadAllBytesAsync(created.Value);
        var backup = await File.ReadAllBytesAsync(created.Value + ".bak");
        var deleted = await service.DeleteSwordAsync(created.Value, DataPath, 0x800);
        Assert.True(deleted.IsFailure);
        Assert.Contains("referenced", deleted.Error);
        Assert.Equal(original, await File.ReadAllBytesAsync(created.Value));
        Assert.Equal(backup, await File.ReadAllBytesAsync(created.Value + ".bak"));
    }

    [Fact]
    public async Task UpdateSword_DuplicateEditorIdAndGameDestination_AreRejected()
    {
        var service = new PluginBuilderService();
        var created = await service.CreateAsync(CreateRequest());
        Assert.True(created.IsSuccess, created.Error);
        Assert.True((await service.CloneSwordAsync(CloneRequest(created.Value, "FirstSword"))).IsSuccess);
        Assert.True((await service.CloneSwordAsync(CloneRequest(created.Value, "SecondSword"))).IsSuccess);
        var original = await File.ReadAllBytesAsync(created.Value);
        var result = await service.UpdateSwordAsync(new UpdateSwordRequest
        {
            PluginPath = created.Value, GameDataPath = DataPath, FormId = 0x800,
            EditorId = "SecondSword", Name = "Duplicate", Damage = 1, Weight = 1, Value = 1
        });
        Assert.True(result.IsFailure);
        Assert.Equal(original, await File.ReadAllBytesAsync(created.Value));
        var gamePath = Path.Combine(DataPath, "InGame.esp");
        File.Copy(created.Value, gamePath);
        Assert.True((await service.DeleteSwordAsync(gamePath, DataPath, 0x800)).IsFailure);
        Assert.True((await service.UpdateSwordAsync(new UpdateSwordRequest
        {
            PluginPath = gamePath, GameDataPath = DataPath, FormId = 0x800, EditorId = "NewId", Name = "Blocked"
        })).IsFailure);
        Assert.Equal(original, await File.ReadAllBytesAsync(gamePath));
        Assert.False(File.Exists(gamePath + ".bak"));
    }
}
