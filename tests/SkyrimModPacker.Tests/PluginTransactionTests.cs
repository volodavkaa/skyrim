using System.IO.Compression;
using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkyrimModPacker.Application.Steps;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure.Archivers;
using SkyrimModPacker.Infrastructure.Esl;
using SkyrimModPacker.Infrastructure.Persistence;
using SkyrimModPacker.Infrastructure.PluginBuilder;
using Xunit;

namespace SkyrimModPacker.Tests;

public sealed class PluginTransactionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), nameof(PluginTransactionTests), Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(_root, "Game", "Data");
    private string Output => Path.Combine(_root, "work");
    private string Plugin => Path.Combine(Output, "Sword.esp");
    private static readonly ModKey Skyrim = ModKey.FromNameAndExtension("Skyrim.esm");

    public PluginTransactionTests()
    {
        Directory.CreateDirectory(Data);
        var master = new SkyrimMod(Skyrim, SkyrimRelease.SkyrimSE);
        master.Weapons.Add(new Weapon(new FormKey(Skyrim, 0x900), SkyrimRelease.SkyrimSE)
        {
            EditorID = "IronSword", Name = "Iron Sword", BasicStats = new WeaponBasicStats { Damage = 7, Weight = 9, Value = 25 },
            Data = new WeaponData { AnimationType = WeaponAnimationType.OneHandSword, Speed = 1, Reach = 1 },
            Model = new Model { File = @"Weapons\Iron\Sword.nif" }
        });
        master.Keywords.Add(new Keyword(new FormKey(Skyrim, 0x901), SkyrimRelease.SkyrimSE) { EditorID = "Forge" });
        master.MiscItems.Add(new MiscItem(new FormKey(Skyrim, 0x902), SkyrimRelease.SkyrimSE) { EditorID = "IronIngot" });
        master.LeveledItems.Add(new LeveledItem(new FormKey(Skyrim, 0x903), SkyrimRelease.SkyrimSE) { EditorID = "SwordList", Entries = [] });
        master.WriteToBinary(Path.Combine(Data, "Skyrim.esm"));
    }

    public void Dispose()
    {
        var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), nameof(PluginTransactionTests))) + Path.DirectorySeparatorChar;
        if (!_root.StartsWith(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test cleanup path.");
        Directory.Delete(_root, recursive: true);
    }

    private CreatePluginRequest CreateRequest() => new() { PluginFileName = "Sword.esp", OutputDirectory = Output, GameDataPath = Data, Masters = ["Skyrim.esm"] };
    private CloneSwordRequest Clone(string id = "CustomSword", string? path = null) => new()
    {
        PluginPath = path ?? Plugin, GameDataPath = Data, TemplateFormId = 0x900, EditorId = id, Name = "Власний меч"
    };
    private UpdateSwordRequest Update() => new()
    {
        PluginPath = Plugin, GameDataPath = Data, FormId = 0x800, EditorId = "UpdatedSword", Name = "Оновлений меч", Damage = 20, Weight = 6, Value = 90
    };
    private SaveRecipeRequest Recipe(string id = "RecipeSword") => new()
    {
        PluginPath = Plugin, GameDataPath = Data, Recipe = new CraftingRecipe
        {
            EditorId = id, WeaponFormId = 0x800, Workbench = new("Skyrim.esm", 0x901),
            Ingredients = [new RecipeIngredient { Reference = new("Skyrim.esm", 0x902) }]
        }
    };
    private LeveledListRequest ListRequest() => new() { PluginPath = Plugin, GameDataPath = Data, WeaponFormId = 0x800, List = new("Skyrim.esm", 0x903) };

    private async Task Prepare(bool sword = true)
    {
        var created = await new PluginBuilderService().CreateAsync(CreateRequest());
        Assert.True(created.IsSuccess, created.Error);
        if (sword)
        {
            var cloned = await new PluginBuilderService().CloneSwordAsync(Clone());
            Assert.True(cloned.IsSuccess, cloned.Error);
        }
    }

    private void AssertNoTemporaryFiles(string? folder = null)
    {
        folder ??= Output;
        Assert.Empty(Directory.GetDirectories(folder, ".plugin-*"));
        Assert.Empty(Directory.GetDirectories(folder, ".assets-*"));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
    }

    [Fact]
    public async Task ConcurrentClonesAndEsl_AcrossServiceInstances_KeepAllRecordsAndFlag()
    {
        await Prepare(sword: false);
        var gameBefore = await File.ReadAllBytesAsync(Path.Combine(Data, "Skyrim.esm"));
        var tasks = Enumerable.Range(0, 20).Select(async i =>
        {
            var result = await new PluginBuilderService().CloneSwordAsync(Clone("Sword" + i));
            Assert.True(result.IsSuccess, result.Error);
            return result.Value.FormId;
        }).ToList();
        var flag = new EslService().SetFlagAsync(Plugin, Data, Path.Combine(_root, "esl-backup", "Sword.bak"));
        var ids = await Task.WhenAll(tasks);
        var flagged = await flag;
        Assert.True(flagged.IsSuccess, flagged.Error);
        Assert.Equal(20, ids.Distinct().Count());
        Assert.Equal(Enumerable.Range(0x800, 20).Select(i => (uint)i), ids.Order());
        using var saved = SkyrimMod.CreateFromBinaryOverlay(Plugin, SkyrimRelease.SkyrimSE);
        Assert.Equal(20, saved.Weapons.Count);
        // Read UTF8 explicitly, as used by the editor and plugin validation.
        using var utf8 = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(Plugin).WithUtf8Encoding().Construct();
        Assert.All(utf8.Weapons, sword => Assert.Equal("Власний меч", sword.Name!.String));
        Assert.True(saved.IsSmallMaster);
        Assert.Equal(0x814u, saved.ModHeader.Stats.NextFormID);
        Assert.Equal(gameBefore, await File.ReadAllBytesAsync(Path.Combine(Data, "Skyrim.esm")));
        Assert.Single(Directory.GetFiles(Data));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task MixedRecipeListUpdateAndCloneWrites_DoNotLoseOtherRecordTypes()
    {
        await Prepare();
        var changes = Enumerable.Range(0, 8).Select(async i =>
        {
            var result = await new PluginBuilderService().SaveRecipeAsync(Recipe("Recipe" + i));
            Assert.True(result.IsSuccess, result.Error);
        }).ToList();
        changes.AddRange(Enumerable.Range(0, 8).Select(async i =>
        {
            var result = await new PluginBuilderService().CloneSwordAsync(Clone("OtherSword" + i));
            Assert.True(result.IsSuccess, result.Error);
        }));
        changes.Add(Task.Run(async () => Assert.True((await new PluginBuilderService().AddToLeveledListAsync(ListRequest())).IsSuccess)));
        changes.Add(Task.Run(async () => Assert.True((await new PluginBuilderService().UpdateSwordAsync(Update())).IsSuccess)));
        await Task.WhenAll(changes);
        using var saved = SkyrimMod.CreateFromBinaryOverlay(Plugin, SkyrimRelease.SkyrimSE);
        Assert.Equal(9, saved.Weapons.Count);
        Assert.Equal(8, saved.ConstructibleObjects.Count);
        Assert.Equal("UpdatedSword", saved.Weapons.First(w => w.FormKey.ID == 0x800).EditorID);
        Assert.Single(Assert.Single(saved.LeveledItems).Entries!);
        Assert.Equal(17, saved.EnumerateMajorRecords().Where(r => r.FormKey.ModKey == saved.ModKey).Select(r => r.FormKey).Distinct().Count());
        AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData("clone")]
    [InlineData("update")]
    [InlineData("deleteSword")]
    [InlineData("recipe")]
    [InlineData("deleteRecipe")]
    [InlineData("addList")]
    [InlineData("removeList")]
    [InlineData("esl")]
    public async Task AllMutations_WaitForTheSamePathLock_AndCancellationDoesNotWrite(string operation)
    {
        await Prepare();
        var before = await File.ReadAllBytesAsync(Plugin);
        var backup = await File.ReadAllBytesAsync(Plugin + ".bak");
        using var cancellation = new CancellationTokenSource();
        Task pending;
        using (await WorkspaceFileGate.AcquireAsync(Plugin, CancellationToken.None))
        {
            var builder = new PluginBuilderService();
            pending = operation switch
            {
                "clone" => builder.CloneSwordAsync(Clone(), cancellation.Token),
                "update" => builder.UpdateSwordAsync(Update(), cancellation.Token),
                "deleteSword" => builder.DeleteSwordAsync(Plugin, Data, 0x800, cancellation.Token),
                "recipe" => builder.SaveRecipeAsync(Recipe(), cancellation.Token),
                "deleteRecipe" => builder.DeleteRecipeAsync(Plugin, Data, 0x801, cancellation.Token),
                "addList" => builder.AddToLeveledListAsync(ListRequest(), cancellation.Token),
                "removeList" => builder.RemoveFromLeveledListAsync(ListRequest(), cancellation.Token),
                _ => new EslService().SetFlagAsync(Plugin, Data, cancellationToken: cancellation.Token)
            };
            await Task.Delay(50);
            Assert.False(pending.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        Assert.Equal(before, await File.ReadAllBytesAsync(Plugin));
        Assert.Equal(backup, await File.ReadAllBytesAsync(Plugin + ".bak"));
        Assert.True((await new PluginBuilderService().CloneSwordAsync(Clone("RetrySword"))).IsSuccess);
        AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("missingRecord")]
    [InlineData("editorId")]
    [InlineData("flag")]
    [InlineData("nextId")]
    [InlineData("link")]
    [InlineData("damage")]
    [InlineData("description")]
    [InlineData("recordType")]
    public async Task InvalidTemporaryPlugin_NeverReplacesMainOrBackup(string fault)
    {
        await Prepare();
        var before = await File.ReadAllBytesAsync(Plugin);
        var backup = await File.ReadAllBytesAsync(Plugin + ".bak");
        using (var transaction = await PluginFileTransaction.AcquireAsync(Plugin, Data, CancellationToken.None))
        {
            var expected = SkyrimMod.CreateFromBinary(Plugin, SkyrimRelease.SkyrimSE);
            var wrong = SkyrimMod.CreateFromBinary(Plugin, SkyrimRelease.SkyrimSE);
            switch (fault)
            {
                case "missingRecord": wrong.Weapons.Clear(); break;
                case "editorId": wrong.Weapons.First().EditorID = "WrongId"; break;
                case "flag": wrong.IsSmallMaster = true; break;
                case "nextId": wrong.ModHeader.Stats.NextFormID += 10; break;
                case "link": wrong.Weapons.First().AttackSound.SetTo(new FormKey(Skyrim, 0x999)); break;
                case "damage": wrong.Weapons.First().BasicStats!.Damage += 1; break;
                case "description": wrong.Weapons.First().Description = "Пошкоджений опис"; break;
                case "recordType":
                    var key = wrong.Weapons.First().FormKey;
                    var editorId = wrong.Weapons.First().EditorID;
                    wrong.Weapons.Clear();
                    wrong.Keywords.Add(new Keyword(key, SkyrimRelease.SkyrimSE) { EditorID = editorId });
                    break;
            }
            Assert.ThrowsAny<Exception>(() => transaction.Write(expected, path =>
            {
                if (fault == "corrupt") File.WriteAllText(path, "broken plugin");
                else wrong.BeginWrite.ToPath(path).WithLoadOrderFromHeaderMasters().WithDataFolder(Data)
                    .NoMastersListContentCheck().NoNextFormIDProcessing().WithUtf8Encoding().Write();
            }, Plugin + ".bak", replace: true, replaceBackup: true, CancellationToken.None));
        }
        Assert.Equal(before, await File.ReadAllBytesAsync(Plugin));
        Assert.Equal(backup, await File.ReadAllBytesAsync(Plugin + ".bak"));
        AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterWritingTemporaryPlugin_PreservesDestination(bool existing)
    {
        await Prepare();
        var path = existing ? Plugin : Path.Combine(Output, "New.esp");
        var before = existing ? await File.ReadAllBytesAsync(path) : null;
        var backup = await File.ReadAllBytesAsync(Plugin + ".bak");
        using var token = new CancellationTokenSource();
        using (var transaction = await PluginFileTransaction.AcquireAsync(path, Data, token.Token))
        {
            var mod = new SkyrimMod(ModKey.FromNameAndExtension(Path.GetFileName(path)), SkyrimRelease.SkyrimSE);
            Assert.ThrowsAny<OperationCanceledException>(() => transaction.Write(mod, temporary =>
            {
                mod.WriteToBinary(temporary);
                token.Cancel();
            }, existing ? path + ".bak" : null, existing, true, token.Token));
        }
        if (existing) Assert.Equal(before, await File.ReadAllBytesAsync(path));
        else Assert.False(File.Exists(path));
        Assert.Equal(backup, await File.ReadAllBytesAsync(Plugin + ".bak"));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task ExternalDestinationChange_IsDetectedBeforeReplacement()
    {
        await Prepare();
        var backup = await File.ReadAllBytesAsync(Plugin + ".bak");
        var external = SkyrimMod.CreateFromBinary(Plugin, SkyrimRelease.SkyrimSE);
        external.Weapons.First().Name = "External editor";
        byte[] externalBytes;
        using (var transaction = await PluginFileTransaction.AcquireAsync(Plugin, Data, CancellationToken.None))
        {
            var expected = SkyrimMod.CreateFromBinary(Plugin, SkyrimRelease.SkyrimSE);
            var error = Assert.Throws<IOException>(() => transaction.Write(expected, temporary =>
            {
                expected.BeginWrite.ToPath(temporary).WithLoadOrderFromHeaderMasters().WithDataFolder(Data)
                    .NoMastersListContentCheck().NoNextFormIDProcessing().WithUtf8Encoding().Write();
                external.WriteToBinary(Plugin);
            }, Plugin + ".bak", true, true, CancellationToken.None));
            Assert.Contains("changed during", error.Message);
            externalBytes = await File.ReadAllBytesAsync(Plugin);
        }
        using var saved = SkyrimMod.CreateFromBinaryOverlay(Plugin, SkyrimRelease.SkyrimSE);
        Assert.Equal("External editor", saved.Weapons.First().Name!.String);
        Assert.Equal(externalBytes, await File.ReadAllBytesAsync(Plugin));
        Assert.Equal(backup, await File.ReadAllBytesAsync(Plugin + ".bak"));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task ReplacementDenied_PreservesMainAndPreviousBackup_ThenRetryWorks()
    {
        await Prepare();
        var before = await File.ReadAllBytesAsync(Plugin);
        var backup = await File.ReadAllBytesAsync(Plugin + ".bak");
        using (var held = new FileStream(Plugin, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await new PluginBuilderService().UpdateSwordAsync(Update());
            Assert.True(result.IsFailure);
            Assert.Equal(before, await File.ReadAllBytesAsync(Plugin));
            Assert.Equal(backup, await File.ReadAllBytesAsync(Plugin + ".bak"));
        }
        Assert.True((await new PluginBuilderService().UpdateSwordAsync(Update())).IsSuccess);
        Assert.Equal(before, await File.ReadAllBytesAsync(Plugin + ".bak"));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task ConcurrentCreates_OnlyOneWins_WithoutOverwritingOrCreatingBackup()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => new PluginBuilderService().CreateAsync(CreateRequest())));
        Assert.Single(results, r => r.IsSuccess);
        Assert.Equal(7, results.Count(r => r.IsFailure));
        Assert.False(File.Exists(Plugin + ".bak"));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task StagingArchive_ExcludesLocksAndUnselectedTemporaryFiles()
    {
        await Prepare();
        var archive = Path.Combine(_root, "result.zip");
        var context = new PackingContext
        {
            Profile = new ModProfile { Name = "Sword", PluginFileName = "Sword.esp", PluginSourcePath = Plugin },
            Settings = new AppSettings { SkyrimDataPath = Data, UseTempDirectory = true }, OutputArchivePath = archive
        };
        context.SelectedFiles.Add(new ScannedFile { AbsolutePath = Plugin, RelativePath = "Sword.esp", Category = FileCategory.Plugin });
        Assert.True(await new CopyStep().ExecuteAsync(context));
        var stage = context.StagingDirectory!;
        await File.WriteAllTextAsync(Path.Combine(stage, "Sword.esp.lock"), "");
        await File.WriteAllTextAsync(Path.Combine(stage, "unselected.tmp"), "service data");
        Assert.True(await new ArchiveStep(new ZipArchiver()).ExecuteAsync(context));
        using var zip = ZipFile.OpenRead(archive);
        Assert.Equal("Sword.esp", Assert.Single(zip.Entries).FullName);
        Assert.False(Directory.Exists(stage));
    }

    [Fact]
    public async Task GameDestination_IsRejectedBeforeAnyLockOrTemporaryFileIsWritten()
    {
        await Prepare();
        var installed = Path.Combine(Data, "Sword.esp");
        File.Copy(Plugin, installed);
        var before = await File.ReadAllBytesAsync(installed);
        Assert.True((await new PluginBuilderService().CloneSwordAsync(Clone(path: installed))).IsFailure);
        Assert.True((await new EslService().SetFlagAsync(installed, Data)).IsFailure);
        Assert.Equal(before, await File.ReadAllBytesAsync(installed));
        Assert.False(File.Exists(installed + ".lock"));
        Assert.False(File.Exists(installed + ".bak"));
        Assert.Empty(Directory.GetDirectories(Data));
    }

    [Fact]
    public async Task ConcurrentResourceImports_AcrossServices_KeepEveryRegistrationAndResource()
    {
        var project = await new WeaponProjectService(new PluginBuilderService()).CreateAsync(CreateRequest());
        Assert.True(project.IsSuccess, project.Error);
        var source = Path.Combine(_root, "sources");
        Directory.CreateDirectory(source);
        var files = Enumerable.Range(0, 15).Select(i => Path.Combine(source, "blade" + i + ".dds")).ToArray();
        foreach (var file in files) await File.WriteAllTextAsync(file, Path.GetFileName(file));
        var results = await Task.WhenAll(files.Select(file => new WeaponProjectService(new PluginBuilderService())
            .ImportTexturesAsync(project.Value.ProjectFilePath, [file], Data)));
        Assert.All(results, result => Assert.True(result.IsSuccess, result.Error));
        var registered = await new WeaponProjectService(new PluginBuilderService()).ValidateResourcesAsync(project.Value.ProjectFilePath, Data);
        Assert.True(registered.IsSuccess, registered.Error);
        Assert.Equal(15, registered.Value.Count);
        var folder = Path.GetDirectoryName(project.Value.PluginPath)!;
        foreach (var resource in registered.Value)
            Assert.Equal(Path.GetFileName(resource), await File.ReadAllTextAsync(Path.Combine(folder, resource.Replace('/', '\\'))));
        using var prior = JsonDocument.Parse(await File.ReadAllTextAsync(project.Value.ProjectFilePath + ".bak"));
        Assert.Equal(14, prior.RootElement.GetProperty("Resources").GetArrayLength());
        AssertNoTemporaryFiles(folder);
    }

    [Fact]
    public async Task ResourceMetadataReplacementDenied_RollsBackOnlyNewFilesAndPreservesBackup()
    {
        var service = new WeaponProjectService(new PluginBuilderService());
        var project = (await service.CreateAsync(CreateRequest())).Value;
        var first = Path.Combine(_root, "first.dds");
        var second = Path.Combine(_root, "second.dds");
        await File.WriteAllTextAsync(first, "first");
        await File.WriteAllTextAsync(second, "second");
        Assert.True((await service.ImportTexturesAsync(project.ProjectFilePath, [first], Data)).IsSuccess);
        var before = await File.ReadAllBytesAsync(project.ProjectFilePath);
        var backup = await File.ReadAllBytesAsync(project.ProjectFilePath + ".bak");
        using (var held = new FileStream(project.ProjectFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await service.ImportTexturesAsync(project.ProjectFilePath, [second], Data, "new/nested");
            Assert.True(result.IsFailure);
        }
        Assert.Equal(before, await File.ReadAllBytesAsync(project.ProjectFilePath));
        Assert.Equal(backup, await File.ReadAllBytesAsync(project.ProjectFilePath + ".bak"));
        var folder = Path.GetDirectoryName(project.PluginPath)!;
        Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(folder, "textures", "first.dds")));
        Assert.False(Directory.Exists(Path.Combine(folder, "textures", "new")));
        AssertNoTemporaryFiles(folder);
        Assert.True((await service.ImportTexturesAsync(project.ProjectFilePath, [second], Data, "new/nested")).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EslFlag_ChangesOnlyHeaderBit_WithoutRewritingStringsOrAllocation(bool localized)
    {
        await Prepare();
        var mod = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(Plugin).WithUtf8Encoding().Mutable().Construct();
        mod.UsingLocalization = localized;
        mod.ModHeader.Stats.NextFormID = 0xF00;
        mod.Weapons.First().Description = "Опис власного меча";
        mod.BeginWrite.ToPath(Plugin).WithLoadOrderFromHeaderMasters().WithDataFolder(Data)
            .NoMastersListContentCheck().NoNextFormIDProcessing().WithUtf8Encoding().Write();
        var original = await File.ReadAllBytesAsync(Plugin);
        var filesBefore = Directory.GetFiles(Output, "*", SearchOption.AllDirectories)
            .Where(p => p != Plugin).ToDictionary(p => p, File.ReadAllBytes);
        var backup = Path.Combine(_root, "flag-only.bak");
        var result = await new EslService().SetFlagAsync(Plugin, Data, backup);
        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(original, await File.ReadAllBytesAsync(backup));
        var expected = original.ToArray();
        expected[9] |= 0x02; // TES4 flags at byte 8: Small master = 0x200.
        Assert.Equal(expected, await File.ReadAllBytesAsync(Plugin));
        foreach (var (path, bytes) in filesBefore) Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(filesBefore.Count + 1, Directory.GetFiles(Output, "*", SearchOption.AllDirectories).Length);
        AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResourceImports_WaitForMetadataLock_AndCancellationLeavesProjectUnchanged(bool model)
    {
        var service = new WeaponProjectService(new PluginBuilderService());
        var project = (await service.CreateAsync(CreateRequest())).Value;
        var source = Path.Combine(_root, model ? "new.nif" : "new.dds");
        await File.WriteAllTextAsync(source, "resource bytes");
        var before = await File.ReadAllBytesAsync(project.ProjectFilePath);
        using var cancellation = new CancellationTokenSource();
        using (await WorkspaceFileGate.AcquireAsync(project.ProjectFilePath, CancellationToken.None))
        {
            Task pending = model ? service.ImportModelAsync(project.ProjectFilePath, source, Data, cancellation.Token) :
                service.ImportTexturesAsync(project.ProjectFilePath, [source], Data, cancellationToken: cancellation.Token);
            await Task.Delay(50);
            Assert.False(pending.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        Assert.Equal(before, await File.ReadAllBytesAsync(project.ProjectFilePath));
        Assert.False(File.Exists(project.ProjectFilePath + ".bak"));
        var folder = Path.GetDirectoryName(project.PluginPath)!;
        Assert.Empty(Directory.GetFiles(Path.Combine(folder, "meshes"), "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(Path.Combine(folder, "textures"), "*", SearchOption.AllDirectories));
        AssertNoTemporaryFiles(folder);
        if (model) Assert.True((await service.ImportModelAsync(project.ProjectFilePath, source, Data)).IsSuccess);
        else Assert.True((await service.ImportTexturesAsync(project.ProjectFilePath, [source], Data)).IsSuccess);
    }
}
