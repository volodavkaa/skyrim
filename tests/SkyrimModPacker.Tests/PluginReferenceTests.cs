using System.Windows;
using System.Windows.Controls;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Infrastructure.PluginBuilder;
using SkyrimModPacker.UI.Controls;
using SkyrimModPacker.UI.ViewModels;
using SkyrimModPacker.Application.Services;
using SkyrimModPacker.Application.Steps;
using SkyrimModPacker.Infrastructure.FileSystem;
using SkyrimModPacker.Infrastructure.Archivers;
using SkyrimModPacker.Core.Interfaces;
using NSubstitute;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace SkyrimModPacker.Tests;

public sealed class PluginReferenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), nameof(PluginReferenceTests), Guid.NewGuid().ToString("N"));
    private static readonly ModKey Master = ModKey.FromNameAndExtension("Base.esm");
    private static readonly ModKey Plugin = ModKey.FromNameAndExtension("Test.esp");
    private string PathTo(string name) => Path.Combine(_root, name);
    public PluginReferenceTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    public static IEnumerable<object[]> Cases() =>
        from field in new[] { "keyword", "sound", "enchantment", "output", "ingredient", "workbench", "perk", "list",
            "AttackSound2D", "AttackLoopSound", "AttackFailSound", "IdleSound", "EquipSound", "UnequipSound", "PickUpSound", "PutDownSound" }
        from state in new[] { "valid", "missing", "deleted", "wrong", "null" }
        select new object[] { field, state };

    private SkyrimMod Fixture(string field, string state)
    {
        var master = new SkyrimMod(Master, SkyrimRelease.SkyrimSE);
        var key = new FormKey(Master, 0x900);
        IMajorRecord target = field switch
        {
            "keyword" or "workbench" => new Keyword(key, SkyrimRelease.SkyrimSE),
            "sound" or "AttackSound2D" or "AttackLoopSound" or "AttackFailSound" or "IdleSound" or "EquipSound" or "UnequipSound" or "PickUpSound" or "PutDownSound" => new SoundDescriptor(key, SkyrimRelease.SkyrimSE),
            "enchantment" => new ObjectEffect(key, SkyrimRelease.SkyrimSE),
            "perk" => new Perk(key, SkyrimRelease.SkyrimSE),
            _ => new MiscItem(key, SkyrimRelease.SkyrimSE)
        };
        if (state == "wrong") target = target is Keyword ? new MiscItem(key, SkyrimRelease.SkyrimSE) : new Keyword(key, SkyrimRelease.SkyrimSE);
        target.IsDeleted = state == "deleted";
        if (state != "missing")
        {
            switch (target)
            {
                case Keyword value: master.Keywords.Add(value); break;
                case SoundDescriptor value: master.SoundDescriptors.Add(value); break;
                case ObjectEffect value: master.ObjectEffects.Add(value); break;
                case Perk value: master.Perks.Add(value); break;
                case MiscItem value: master.MiscItems.Add(value); break;
            }
        }
        master.WriteToBinary(PathTo("Base.esm"));
        var mod = new SkyrimMod(Plugin, SkyrimRelease.SkyrimSE);
        mod.ModHeader.MasterReferences.Add(new MasterReference { Master = Master });
        var weapon = new Weapon(new FormKey(Plugin, 0x800), SkyrimRelease.SkyrimSE) { EditorID = "Sword" };
        mod.Weapons.Add(weapon);
        var reference = state == "null" ? FormKey.Null : key;
        if (field.EndsWith("Sound", StringComparison.Ordinal) || field == "AttackSound2D")
        {
            ((IFormLinkNullable<ISoundDescriptorGetter>)typeof(Weapon).GetProperty(field)!.GetValue(weapon)!).SetTo(reference);
            return mod;
        }
        switch (field)
        {
            case "keyword": weapon.Keywords = [new FormLink<IKeywordGetter>(reference)]; break;
            case "sound": weapon.AttackSound.SetTo(reference); break;
            case "enchantment": weapon.ObjectEffect.SetTo(reference); break;
            case "list":
                var entry = new LeveledItemEntry { Data = new LeveledItemEntryData { Level = 1, Count = 1 } };
                entry.Data.Reference.SetTo(reference);
                mod.LeveledItems.Add(new LeveledItem(new FormKey(Plugin, 0x801), SkyrimRelease.SkyrimSE) { Entries = [entry] });
                break;
            default:
                var recipe = new ConstructibleObject(new FormKey(Plugin, 0x801), SkyrimRelease.SkyrimSE);
                recipe.CreatedObject.SetTo(weapon.FormKey);
                if (field == "output") recipe.CreatedObject.SetTo(reference);
                if (field == "workbench") recipe.WorkbenchKeyword.SetTo(reference);
                if (field == "ingredient")
                {
                    var ingredient = new ContainerEntry { Item = new ContainerItem { Count = 1 } };
                    ingredient.Item.Item.SetTo(reference);
                    recipe.Items = [ingredient];
                }
                if (field == "perk")
                {
                    var condition = new HasPerkConditionData();
                    condition.Perk.Link.SetTo(reference);
                    recipe.Conditions.Add(new ConditionFloat { Data = condition });
                }
                mod.ConstructibleObjects.Add(recipe);
                break;
        }
        return mod;
    }

    private void Write(SkyrimMod mod) => mod.BeginWrite.ToPath(PathTo("Test.esp")).WithLoadOrderFromHeaderMasters()
        .WithDataFolder(_root).NoMastersListContentCheck().Write();

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task SupportedReferences_ReportFieldTargetAndAction(string field, string state)
    {
        Write(Fixture(field, state));
        var originals = Directory.GetFiles(_root).ToDictionary(p => p, File.ReadAllBytes);
        var diagnostics = await new PluginValidationService().ValidateAsync(PathTo("Test.esp"), _root);
        var expectedCode = state switch
        {
            "missing" => "REFERENCE_MISSING", "deleted" => "REFERENCE_DELETED", "wrong" => "REFERENCE_TYPE",
            "null" => "REFERENCE_NULL", _ => null
        };
        var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (expectedCode == null) Assert.Empty(errors);
        else
        {
            var issue = Assert.Single(errors);
            Assert.Equal(expectedCode, issue.Code);
            Assert.Contains("/", issue.Record);
            Assert.False(string.IsNullOrWhiteSpace(issue.RecommendedAction));
            Assert.Equal(PathTo("Test.esp"), issue.FilePath);
        }
        foreach (var (path, bytes) in originals) Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(originals.Count, Directory.GetFiles(_root).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PluginOverride_WinsOverMaster_EvenWhenDeleted(bool deleted)
    {
        var mod = Fixture("keyword", "valid");
        mod.Keywords.Add(new Keyword(new FormKey(Master, 0x900), SkyrimRelease.SkyrimSE) { IsDeleted = deleted });
        Write(mod);
        var diagnostics = await new PluginValidationService().ValidateAsync(PathTo("Test.esp"), _root);
        Assert.Equal(deleted, diagnostics.Any(d => d.Code == "REFERENCE_DELETED"));
        Assert.DoesNotContain(diagnostics, d => d.Code == "REFERENCE_MISSING");
    }

    [Fact]
    public async Task LaterDeclaredMasterOverride_WinsOverEarlierTarget()
    {
        var mod = Fixture("keyword", "valid");
        var later = new SkyrimMod(ModKey.FromNameAndExtension("Later.esm"), SkyrimRelease.SkyrimSE);
        later.ModHeader.MasterReferences.Add(new MasterReference { Master = Master });
        later.Keywords.Add(new Keyword(new FormKey(Master, 0x900), SkyrimRelease.SkyrimSE) { IsDeleted = true });
        later.BeginWrite.ToPath(PathTo("Later.esm")).WithLoadOrderFromHeaderMasters().WithDataFolder(_root).NoMastersListContentCheck().Write();
        mod.ModHeader.MasterReferences.Add(new MasterReference { Master = later.ModKey });
        Write(mod);
        Assert.Contains(await new PluginValidationService().ValidateAsync(PathTo("Test.esp"), _root), d => d.Code == "REFERENCE_DELETED");
    }

    [Fact]
    public async Task LocalOwnedTarget_ResolvesWithoutFallbackToMasters()
    {
        var mod = Fixture("keyword", "missing");
        var local = new FormKey(Plugin, 0x902);
        mod.Keywords.Add(new Keyword(local, SkyrimRelease.SkyrimSE));
        mod.Weapons.First().Keywords = [new FormLink<IKeywordGetter>(local)];
        Write(mod);
        Assert.DoesNotContain(await new PluginValidationService().ValidateAsync(PathTo("Test.esp"), _root), d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task DeletedOwner_DoesNotRequireItsObsoleteReferences()
    {
        var mod = Fixture("keyword", "missing");
        mod.Weapons.First().IsDeleted = true;
        Write(mod);
        Assert.DoesNotContain(await new PluginValidationService().ValidateAsync(PathTo("Test.esp"), _root), d => d.Code == "REFERENCE_MISSING");
    }

    [Fact]
    public async Task BuildCommand_PublishesDiagnosticsOnFailure_ThenReplacesThemOnRetry()
    {
        Write(Fixture("keyword", "missing"));
        var data = PathTo("Game/Data");
        Directory.CreateDirectory(data);
        File.Copy(PathTo("Base.esm"), Path.Combine(data, "Base.esm"));
        var profile = new ModProfile { Name = "Test", PluginFileName = "Test.esp", PluginSourcePath = PathTo("Test.esp") };
        var profiles = Substitute.For<IProfileRepository>();
        profiles.LoadAllAsync(Arg.Any<CancellationToken>()).Returns(new List<ModProfile> { profile });
        var settings = Substitute.For<ISettingsRepository>();
        settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new AppSettings { SkyrimDataPath = data, OutputDirectory = PathTo("Archives") });
        var scanner = new ModScanner();
        var archiver = new ZipArchiver();
        var pipeline = new PackingPipeline([new ScanStep(scanner), new FilterStep(), new ValidateStep(archiver),
            new PluginValidationStep(new PluginValidationService()), new CopyStep(), new ArchiveStep(archiver)], NullLogger<PackingPipeline>.Instance);
        var vm = new BuildViewModel(profiles, settings, scanner, pipeline);
        await vm.ScanCommand.ExecuteAsync(null);
        await vm.PackCommand.ExecuteAsync(null);
        Assert.Contains(vm.PluginDiagnostics, d => d.Code == "REFERENCE_MISSING");
        Assert.False(vm.HasSuccessNotification);
        Assert.Null(vm.LastArchiveCreatedPath);
        Write(Fixture("keyword", "valid"));
        File.Copy(PathTo("Base.esm"), Path.Combine(data, "Base.esm"), true);
        await vm.PackCommand.ExecuteAsync(null);
        Assert.True(vm.HasSuccessNotification, vm.ErrorMessage);
        Assert.DoesNotContain(vm.PluginDiagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Contains(vm.PluginDiagnostics, d => d.Severity == DiagnosticSeverity.Unsupported);
        vm.SelectedProfile = null;
        Assert.Empty(vm.PluginDiagnostics);
    }

    [Fact]
    public void WeaponDiagnostics_AreClearedWhenSwitchingPlugin()
    {
        var vm = new WeaponsViewModel(Substitute.For<IPluginBuilderService>(), Substitute.For<ISettingsRepository>(),
            Substitute.For<IProfileRepository>(), Substitute.For<IWeaponProjectService>());
        vm.PluginDiagnostics.Add(new("OLD", DiagnosticSeverity.Error, "Old.esp", null, "Old result", "Retry"));
        vm.PluginPath = PathTo("Test.esp");
        Assert.Empty(vm.PluginDiagnostics);
    }

    [Fact]
    public void DiagnosticPanel_GroupsSeveritiesAndClearsOldResults()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = new PluginDiagnosticsView { Diagnostics = new[]
                {
                    new PluginDiagnostic("E", DiagnosticSeverity.Error, "Test.esp", "WEAP", "Missing target", "Repair target"),
                    new PluginDiagnostic("W", DiagnosticSeverity.Warning, "Test.esp", null, "Unknown runtime", "Check runtime"),
                    new PluginDiagnostic("U", DiagnosticSeverity.Unsupported, "Test.esp", null, "Other fields", "Use xEdit")
                } };
                view.Measure(new System.Windows.Size(420, 600));
                view.Arrange(new Rect(0, 0, 420, 600));
                view.UpdateLayout();
                foreach (var name in new[] { "Errors", "Warnings", "Unsupported" })
                    Assert.Single(((ItemsControl)view.FindName(name)).Items.Cast<object>());
                Assert.Equal(Visibility.Visible, ((Expander)view.FindName("Panel")).Visibility);
                view.Diagnostics = [];
                Assert.Equal(Visibility.Collapsed, ((Expander)view.FindName("Panel")).Visibility);
                Assert.Empty(((ItemsControl)view.FindName("Errors")).Items.Cast<object>());
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
