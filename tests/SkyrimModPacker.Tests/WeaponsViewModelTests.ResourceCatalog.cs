using NSubstitute;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure.PluginBuilder;
using SkyrimModPacker.UI.Services;
using SkyrimModPacker.UI.ViewModels;
using Xunit;

namespace SkyrimModPacker.Tests;

public partial class WeaponsViewModelTests
{
    [Fact]
    public async Task ResourceCatalog_Editor_SeparatesSavedUsageFromDraft_AndRefreshesAfterSaving()
    {
        var vm = await PrepareAsync();
        var nif = Path.Combine(_root, "catalog.nif"); File.WriteAllText(nif, "model");
        var dds = Path.Combine(_root, "catalog.dds"); File.WriteAllBytes(dds, PreviewFixtures.ResourceDds("texture"));
        await vm.ImportModelCommand.ExecuteAsync(nif);
        await vm.ImportTexturesCommand.ExecuteAsync(new[] { dds });
        Assert.Null(vm.ErrorMessage); Assert.Null(vm.ResourceCatalogError);
        Assert.Equal(2, vm.ResourceCatalog.Count);
        Assert.All(vm.ResourceCatalog, row => Assert.Empty(row.Entry.Uses));
        var texture = vm.ResourceCatalog.Single(r => r.Entry.Category == FileCategory.Texture);
        Assert.Contains("not been analyzed", texture.UsageText);
        await vm.CloneSwordCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        var mesh = vm.ResourceCatalog.Single(r => r.Entry.Category == FileCategory.Mesh);
        Assert.Single(mesh.Entry.Uses);
        Assert.Contains(vm.SelectedWeapon!.EditorId, mesh.UsageText);
        var saved = mesh.RelativePath;
        vm.ModelPath = @"Custom\unsaved.nif";
        await vm.RefreshResourceCatalogCommand.ExecuteAsync(null);
        Assert.Null(vm.ResourceCatalogError);
        Assert.Equal(saved, vm.ResourceCatalog.Single(r => r.Entry.Category == FileCategory.Mesh).RelativePath);
        Assert.DoesNotContain(vm.ResourceCatalog, r => r.RelativePath.Contains("unsaved"));
    }

    [Fact]
    public async Task ResourceCatalog_Editor_ReportsReadFailureWithoutLeavingOldSuccessfulRows()
    {
        var vm = await PrepareAsync();
        await vm.CloneSwordCommand.ExecuteAsync(null);
        Assert.NotEmpty(vm.ResourceCatalog);
        File.Delete(vm.PluginPath);
        await vm.RefreshResourceCatalogCommand.ExecuteAsync(null);
        Assert.Empty(vm.ResourceCatalog);
        Assert.NotNull(vm.ResourceCatalogError);
        vm.ProjectFilePath = "";
        Assert.Null(vm.ResourceCatalogError); Assert.Empty(vm.ResourceCatalog);
    }

    [Fact]
    public async Task ResourceCatalog_Editor_ReopeningProjectAndChangingData_RefreshesOrInvalidatesRows()
    {
        var vm = await PrepareAsync();
        await vm.CloneSwordCommand.ExecuteAsync(null);
        var other = CreateViewModel();
        await other.InitializeCommand.ExecuteAsync(null);
        await other.OpenProjectCommand.ExecuteAsync(vm.ProjectFilePath);
        Assert.Null(other.ErrorMessage); Assert.Null(other.ResourceCatalogError);
        Assert.Equal(vm.ResourceCatalog.Count, other.ResourceCatalog.Count);
        Assert.Equal(vm.SelectedWeapon!.EditorId, other.ResourceCatalog.First().Entry.Uses.First().EditorId);
        other.GameDataPath = Path.Combine(_root, "different", "Data");
        Assert.Empty(other.ResourceCatalog);
    }

    [Fact]
    public async Task ResourceCatalog_Editor_LanguageChange_ReprojectsCachedRowsWithoutReadingFiles()
    {
        var localization = Substitute.For<ILocalizationService>();
        localization.GetString(Arg.Any<string>()).Returns(call => call.Arg<string>());
        var vm = new WeaponsViewModel(_builder, _settings, _profiles, new WeaponProjectService(_builder), localization)
        { PluginName = "CatalogLocale", OutputDirectory = Path.Combine(_root, "locale-projects") };
        await vm.InitializeCommand.ExecuteAsync(null); await vm.CreatePluginCommand.ExecuteAsync(null);
        await vm.CloneSwordCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal("Vanilla", vm.ResourceCatalog.First().OriginText);
        // Reprojection must work from the cached snapshot; the on-disk plugin is deliberately absent now.
        File.Delete(vm.PluginPath);
        localization.GetString("Models_Origin_Vanilla").Returns("Ванільна");
        localization.LanguageChanged += Raise.Event<Action>();
        Assert.Equal("Ванільна", vm.ResourceCatalog.First().OriginText);
        Assert.Null(vm.ResourceCatalogError);
    }

    [Fact]
    public void ResourceCatalog_Row_UsesLocalizationForIssuesAndArchivePolicy()
    {
        var entry = new ProjectResourceEntry("meshes/Custom/first.nif", FileCategory.Mesh, ModelOrigin.Project,
            ModelAvailability.Missing, true, ResourceArchiveStatus.Blocked,
            [new ResourceUsage(new PluginRecordReference("Test.esp", 0x800), "CustomSword", ResourceUseKind.FirstPerson,
                new PluginRecordReference("Test.esp", 0x801))], [ResourceIssue.MissingOrEmpty]);
        var row = new ProjectResourceRow(entry, (key, fallback) => "localized:" + key);
        Assert.Equal("localized:Resources_Archive_Blocked", row.ArchiveText);
        Assert.Equal("localized:Resources_Issue_MissingOrEmpty", row.IssueText);
        Assert.Contains("CustomSword", row.UsageText); Assert.Contains("000801", row.UsageText);
        Assert.True(row.HasIssues);
    }
}
