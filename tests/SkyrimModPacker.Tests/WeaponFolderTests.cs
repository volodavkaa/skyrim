using SkyrimModPacker.Core.Models.PluginBuilder;
using Xunit;

namespace SkyrimModPacker.Tests;

public partial class WeaponProjectTests
{
    [Fact]
    public async Task WeaponFolderImportsRealLeModelAndBc7TexturesWithoutChangingSources()
    {
        var source = Environment.GetEnvironmentVariable("SKYRIM_LE_PROJECT"); if (string.IsNullOrWhiteSpace(source)) return;
        var project = (await Service.CreateAsync(Request())).Value;
        var model = Path.Combine(source, "meshes", "Stage21", "third.nif");
        var scan = (await Service.ScanWeaponFolderAsync(source)).Value;
        Assert.Contains(scan.Files, f => f.Format.Contains("BC7", StringComparison.OrdinalIgnoreCase));
        var planResult = await Service.PreviewWeaponFolderAsync(new(Guid.NewGuid(), project.ProjectFilePath, source, model, null, true), Data);
        Assert.True(planResult.IsSuccess, planResult.Error); var plan = planResult.Value;
        Assert.False(plan.CanImport); Assert.True(plan.Incomplete); Assert.Contains(plan.Dependencies, d => d.Status == "Import"); // Hidden vanilla layers need game BSAs, absent from this synthetic fixture.
        plan = (await Service.PreviewWeaponFolderAsync(plan.Request with { AllowIncomplete = true }, Data)).Value;
        Assert.True(plan.CanImport); Assert.Contains(plan.Dependencies, d => d.Status is "Missing" or "Unknown" or "MappingRequired");
        Assert.Single(plan.Files, f => f.RelativePath.EndsWith(".nif")); Assert.NotEmpty(plan.Dependencies);
        Assert.True((await Service.ImportWeaponFolderAsync(plan, Data)).IsSuccess);
        Assert.NotEmpty((await Service.ValidateWeaponFolderAssetsAsync(project.ProjectFilePath, [plan.ThirdPersonPath], Data)).Value); // Synthetic game has no vanilla BSA dependencies.
        foreach (var file in plan.Files)
        {
            Assert.Equal(file.Sha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file.SourcePath))));
            Assert.Equal(File.ReadAllBytes(file.SourcePath), File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(project.ProjectFilePath)!, file.RelativePath)));
        }
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/audit")); Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "smart-real-folder-import.json"), System.Text.Json.JsonSerializer.Serialize(plan, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
    private async Task<(WeaponProject Project, WeaponFolderRequest Request)> FolderFixtureAsync(string diffuse = "textures/Custom/blade.dds", bool loose = false)
    {
        var project = (await Service.CreateAsync(Request())).Value;
        var root = Path.Combine(_root, "folder-assets");
        var model = Path.Combine(root, loose ? "blade.nif" : "meshes/Custom/blade.nif"); Directory.CreateDirectory(Path.GetDirectoryName(model)!); File.WriteAllBytes(model, PreviewFixtures.Nif(diffuse: diffuse));
        var texture = Path.Combine(root, loose ? "blade.dds" : "textures/Custom/blade.dds"); Directory.CreateDirectory(Path.GetDirectoryName(texture)!); File.WriteAllBytes(texture, PreviewFixtures.Bc7());
        return (project, new(Guid.NewGuid(), project.ProjectFilePath, root, model, null, true));
    }
    [Fact]
    public async Task WeaponFolderPlanPreservesPathsSharesOneModelAndFindsAllTextures()
    {
        var (project, request) = await FolderFixtureAsync(); var before = File.ReadAllBytes(project.ProjectFilePath);
        var scan = await Service.ScanWeaponFolderAsync(request.SourceFolder); Assert.True(scan.IsSuccess, scan.Error); Assert.Equal(2, scan.Value.Files.Count);
        var preview = await Service.PreviewWeaponFolderAsync(request, Data); Assert.True(preview.IsSuccess, preview.Error); var plan = preview.Value;
        Assert.True(plan.CanImport); Assert.Equal(plan.ThirdPersonPath, plan.FirstPersonPath); Assert.Equal(2, plan.Files.Count); Assert.Equal(before, File.ReadAllBytes(project.ProjectFilePath));
        Assert.Contains(plan.Files, f => f.RelativePath == "meshes/Custom/blade.nif"); Assert.Contains(plan.Files, f => f.RelativePath == "textures/Custom/blade.dds");
        var result = await Service.ImportWeaponFolderAsync(plan, Data); Assert.True(result.IsSuccess, result.Error);
        foreach (var file in plan.Files) Assert.Equal(File.ReadAllBytes(file.SourcePath), File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(project.ProjectFilePath)!, file.RelativePath)));
        Assert.Empty((await Service.ValidateWeaponFolderAssetsAsync(project.ProjectFilePath, [result.Value.ThirdPersonPath], Data)).Value);
        Assert.True((await Service.ImportWeaponFolderAsync(plan, Data)).IsFailure); // Service rejects a stale project; the VM handles completed-plan replay.
    }
    [Fact]
    public async Task WeaponFolderExplicitRootAndFileMappingResolveAmbiguityWithoutRenamingNif()
    {
        var (project, request) = await FolderFixtureAsync(loose: true);
        var plan = (await Service.PreviewWeaponFolderAsync(request, Data)).Value; Assert.False(plan.CanImport);
        Assert.Equal("MappingRequired", Assert.Single(plan.Dependencies).Status);
        var rootPlan = (await Service.PreviewWeaponFolderAsync(request with { TextureRoot = "Custom" }, Data)).Value; Assert.True(rootPlan.CanImport);
        Assert.Contains(rootPlan.Files, f => f.RelativePath == "textures/Custom/blade.dds");
        File.WriteAllBytes(request.ThirdPersonSource, PreviewFixtures.Nif(diffuse: "textures/a.dds", extraTextures: ["textures/b.dds"]));
        var texture = Path.Combine(request.SourceFolder, "blade.dds");
        var mapped = (await Service.PreviewWeaponFolderAsync(request with { TextureMappings = [new(texture, "textures/a.dds"), new(texture, "textures/b.dds")] }, Data)).Value;
        Assert.True(mapped.CanImport); Assert.Equal(3, mapped.Files.Count); Assert.True((await Service.ImportWeaponFolderAsync(mapped, Data)).IsSuccess);
    }
    [Fact]
    public async Task WeaponFolderFailedDraftCommitRestoresManifestBackupAndOnlyCreatedFiles()
    {
        var (project, request) = await FolderFixtureAsync(); var before = File.ReadAllBytes(project.ProjectFilePath);
        var backup = project.ProjectFilePath + ".bak"; File.WriteAllText(backup, "previous backup bytes"); var oldBackup = File.ReadAllBytes(backup);
        var folder = Path.GetDirectoryName(project.ProjectFilePath)!; var plan = (await Service.PreviewWeaponFolderAsync(request, Data)).Value;
        var result = await Service.ImportWeaponFolderAsync(plan, Data, (_, _) => Task.FromException(new IOException("Injected draft failure")));
        Assert.True(result.IsFailure); Assert.Equal(before, File.ReadAllBytes(project.ProjectFilePath)); Assert.Equal(oldBackup, File.ReadAllBytes(backup));
        Assert.True(Directory.Exists(Path.Combine(folder, "meshes"))); Assert.True(Directory.Exists(Path.Combine(folder, "textures")));
        Assert.Empty(Directory.GetFiles(Path.Combine(folder, "meshes"), "*", SearchOption.AllDirectories)); Assert.Empty(Directory.GetFiles(Path.Combine(folder, "textures"), "*", SearchOption.AllDirectories));
        Assert.True((await Service.ImportWeaponFolderAsync(plan, Data)).IsSuccess);
    }
    [Fact]
    public async Task WeaponFolderRejectsChangedSourcesConflictsAndUnsafeMappings()
    {
        var (project, request) = await FolderFixtureAsync(); var before = File.ReadAllBytes(project.ProjectFilePath);
        var plan = (await Service.PreviewWeaponFolderAsync(request, Data)).Value;
        File.WriteAllBytes(Path.Combine(request.SourceFolder, "textures/Custom/blade.dds"), PreviewFixtures.ResourceDds("changed source"));
        Assert.True((await Service.ImportWeaponFolderAsync(plan, Data)).IsFailure); Assert.Equal(before, File.ReadAllBytes(project.ProjectFilePath));
        var invalid = await Service.PreviewWeaponFolderAsync(request with { TextureRoot = "../escape" }, Data); Assert.True(invalid.IsFailure);
        plan = (await Service.PreviewWeaponFolderAsync(request, Data)).Value; Assert.True((await Service.ImportWeaponFolderAsync(plan, Data)).IsSuccess);
        File.WriteAllBytes(Path.Combine(request.SourceFolder, "textures/Custom/blade.dds"), PreviewFixtures.Bc7());
        plan = (await Service.PreviewWeaponFolderAsync(request, Data)).Value; Assert.False(plan.CanImport); Assert.Contains(plan.Files, f => f.Conflict != null);
    }
    [Fact]
    public async Task WeaponFolderMissingDependencyIsExplicitAndCancellationLeavesNoWrites()
    {
        var (project, request) = await FolderFixtureAsync("textures/not-present.dds");
        var plan = (await Service.PreviewWeaponFolderAsync(request, Data)).Value; Assert.False(plan.CanImport); Assert.True(plan.Incomplete);
        var incomplete = (await Service.PreviewWeaponFolderAsync(request with { AllowIncomplete = true }, Data)).Value; Assert.True(incomplete.CanImport);
        var before = File.ReadAllBytes(project.ProjectFilePath); using var token = new CancellationTokenSource(); token.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.ImportWeaponFolderAsync(incomplete, Data, cancellationToken: token.Token)); Assert.Equal(before, File.ReadAllBytes(project.ProjectFilePath));
    }
}

public partial class WeaponsViewModelTests
{
    [Fact]
    public async Task SmartWeaponFolderCreatesOneDraftReplayIsSafeAndReopensWithSharedModel()
    {
        await using var vm = await PrepareSmartAsync(); var root = Path.Combine(_root, "smart-folder"); Directory.CreateDirectory(Path.Combine(root, "meshes"));
        var model = Path.Combine(root, "meshes/one.nif"); File.WriteAllBytes(model, PreviewFixtures.Nif(diffuse: ""));
        var request = new WeaponFolderRequest(Guid.NewGuid(), vm.ProjectPath, root, model, null, true);
        var plan = await vm.PreviewWeaponFolderAsync(request); var count = vm.Items.Count;
        await vm.ImportWeaponFolderAsync(plan, true, WeaponKind.OneHandSword); Assert.Equal("", vm.Error); Assert.Equal(count + 1, vm.Items.Count);
        Assert.Equal(vm.SelectedItem!.ModelPath, vm.SelectedItem.FirstPersonPath); Assert.True(vm.SelectedItem.Fields.UseSharedModel);
        await vm.ImportWeaponFolderAsync(plan, true, WeaponKind.OneHandSword); Assert.Equal("", vm.Error); Assert.Equal(count + 1, vm.Items.Count);
        await using var reopened = SmartEditor(); await reopened.InitializeAsync(vm.ProjectPath); Assert.Equal(count + 1, reopened.Items.Count); Assert.Equal(plan.Request.Id, reopened.SelectedItem!.Capture().LastFolderPlanId);
        await reopened.SaveItemCommand.ExecuteAsync(null); Assert.Equal("", reopened.Error); Assert.NotNull(reopened.SelectedItem.ConfirmedFormId);
    }
}
