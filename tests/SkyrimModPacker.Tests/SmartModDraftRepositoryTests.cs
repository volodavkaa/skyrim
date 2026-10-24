using System.Text.Json.Nodes;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure.Persistence;
using Xunit;

namespace SkyrimModPacker.Tests;

public sealed class SmartModDraftRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SmartModDraftRepositoryTests", Guid.NewGuid().ToString("N"));
    private string Folder => Path.Combine(_root, "project");
    private string Project => Path.Combine(Folder, "mod-project.json");
    private string Plugin => Path.Combine(Folder, "Weapons.esp");
    private string Data => Path.Combine(_root, "Game", "Data");
    private string Sidecar => Path.Combine(Folder, JsonSmartModDraftRepository.FileName);
    private readonly JsonSmartModDraftRepository _repository = new();

    public SmartModDraftRepositoryTests()
    {
        Directory.CreateDirectory(Folder);
        Directory.CreateDirectory(Data);
        File.WriteAllText(Project, "{\"Version\":1,\"Name\":\"Legacy\"}");
        File.WriteAllText(Plugin, "confirmed plugin bytes");
        File.WriteAllText(Path.Combine(Folder, "legacy-draft.json"), "old editor draft");
    }

    public void Dispose()
    {
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "SmartModDraftRepositoryTests") + Path.DirectorySeparatorChar,
            Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase);
        Directory.Delete(_root, true);
    }

    private SmartModDraft Draft()
    {
        var recipe = new SmartModRecipeDraft
        {
            IsTempering = true, OutputCount = "not yet a number",
            Ingredients = [new(new("Skyrim.esm", 0x5ACE5), "1,")]
        };
        var first = new SmartModItemDraft
        {
            State = SmartModItemState.InProgress, Step = SmartModItemStep.Acquisition,
            Template = new("Skyrim.esm", 0x12EB7),
            Weapon = new() { Name = "Меч 1", Damage = "поки що текст", FirstPersonEdited = true,
                ModelPath = "Custom/shared.nif", Sounds = new Dictionary<string, PluginRecordReference?> { ["EquipSound"] = new("Skyrim.esm", 1) } },
            Recipes = [recipe]
        };
        return new()
        {
            ProjectFilePath = Project, PluginPath = Plugin, Name = "Мій мод", Section = SmartModSection.Recipes,
            SelectedItemId = first.Id, SelectedRecipeId = recipe.Id,
            Items = [first, new() { Weapon = new() { Name = "Меч 2", ModelPath = "Custom/shared.nif" } },
                new() { Kind = WeaponKind.Dagger, Weapon = new() { Name = "Кинджал 1" } }]
        };
    }

    [Fact]
    public async Task SeveralItems_RawFieldsAndNavigationSurviveReopening_WithoutLegacyWrites()
    {
        var originals = Directory.GetFiles(Folder).ToDictionary(p => p, File.ReadAllBytes);
        var draft = Draft();
        var receipt = await _repository.SaveAsync(draft, Data, 0);
        Assert.True(receipt.IsSuccess, receipt.Error);
        var loaded = await new JsonSmartModDraftRepository().LoadAsync(Project, Data);
        Assert.True(loaded.IsSuccess, loaded.Error);
        var actual = loaded.Value!;
        Assert.Equal(1, actual.Revision);
        Assert.NotEqual(default, actual.SavedAtUtc);
        Assert.Equal(draft.Id, actual.Id);
        Assert.Equal(draft.Items.Select(i => i.Id), actual.Items.Select(i => i.Id));
        Assert.Equal(new[] { "Меч 1", "Меч 2", "Кинджал 1" }, actual.Items.Select(i => i.Weapon.Name));
        Assert.Equal(draft.Section, actual.Section);
        Assert.Equal(draft.SelectedItemId, actual.SelectedItemId);
        Assert.Equal(draft.SelectedRecipeId, actual.SelectedRecipeId);
        Assert.Equal("поки що текст", actual.Items[0].Weapon.Damage);
        Assert.Equal("not yet a number", actual.Items[0].Recipes[0].OutputCount);
        Assert.Equal("1,", actual.Items[0].Recipes[0].Ingredients[0].Count);
        Assert.Equal(draft.Items[0].Weapon.Sounds!["EquipSound"], actual.Items[0].Weapon.Sounds!["EquipSound"]);
        Assert.True(actual.Items[0].Weapon.FirstPersonEdited);
        Assert.Equal(SmartModItemStep.Acquisition, actual.Items[0].Step);
        Assert.Null(actual.Items[0].ConfirmedFormId);
        foreach (var (path, bytes) in originals) Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(Folder, "*.tmp"));
    }

    [Fact]
    public async Task MissingSidecar_OldProjectRemainsReadableWithoutCreatingDraft()
    {
        var loaded = await _repository.LoadAsync(Project, Data);
        Assert.True(loaded.IsSuccess, loaded.Error);
        Assert.Null(loaded.Value);
        Assert.Null((await _repository.LoadBackupAsync(Project, Data)).Value);
        Assert.False(File.Exists(Sidecar));
    }

    [Fact]
    public async Task AtomicReplacement_KeepsPreviousGenerationAsReadableBackup()
    {
        var draft = Draft();
        Assert.True((await _repository.SaveAsync(draft, Data, 0)).IsSuccess);
        var before = File.ReadAllBytes(Sidecar);
        Assert.True((await _repository.SaveAsync(draft with { Name = "Новий стан" }, Data, 1)).IsSuccess);
        Assert.Equal(before, File.ReadAllBytes(Sidecar + ".bak"));
        var backup = await _repository.LoadBackupAsync(Project, Data);
        Assert.True(backup.IsSuccess, backup.Error);
        Assert.Equal(1, backup.Value!.Revision);
        Assert.Equal(draft.Name, backup.Value.Name);
        Assert.Equal("Новий стан", (await _repository.LoadAsync(Project, Data)).Value!.Name);
    }

    [Fact]
    public async Task ConcurrentWriters_OnlyOneExpectedRevisionWins()
    {
        var draft = Draft();
        Assert.True((await _repository.SaveAsync(draft, Data, 0)).IsSuccess);
        var results = await Task.WhenAll(_repository.SaveAsync(draft with { Name = "A" }, Data, 1),
            new JsonSmartModDraftRepository().SaveAsync(draft with { Name = "B" }, Data, 1));
        Assert.Single(results, r => r.IsSuccess);
        Assert.Single(results, r => r.IsFailure);
        Assert.Equal(2, (await _repository.LoadAsync(Project, Data)).Value!.Revision);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"Version\":2}")]
    public async Task CorruptPrimary_IsNotOverwritten_BackupCanBeInspected(string corrupt)
    {
        var draft = Draft();
        await _repository.SaveAsync(draft, Data, 0);
        await _repository.SaveAsync(draft, Data, 1);
        File.WriteAllText(Sidecar, corrupt);
        Assert.True((await _repository.LoadAsync(Project, Data)).IsFailure);
        Assert.True((await _repository.SaveAsync(draft, Data, 2)).IsFailure);
        var backup = await _repository.LoadBackupAsync(Project, Data);
        Assert.True(backup.IsSuccess, backup.Error);
        Assert.Equal(1, backup.Value!.Revision);
        Assert.Equal(corrupt, File.ReadAllText(Sidecar));
    }

    [Theory]
    [InlineData("item-id")]
    [InlineData("recipe-id")]
    [InlineData("selection")]
    [InlineData("kind")]
    [InlineData("version")]
    [InlineData("other-project")]
    public async Task InvalidIdentityOrNavigation_IsRejectedWithoutWrites(string scenario)
    {
        var draft = Draft();
        draft = scenario switch
        {
            "item-id" => draft with { Items = [draft.Items[0], draft.Items[0]] },
            "recipe-id" => draft with { Items = [draft.Items[0] with { Recipes = [draft.Items[0].Recipes[0], draft.Items[0].Recipes[0]] }] },
            "selection" => draft with { SelectedRecipeId = Guid.NewGuid() },
            "kind" => draft with { Items = [draft.Items[0] with { Kind = (WeaponKind)8 }] },
            "version" => draft with { Version = 2 },
            _ => draft with { PluginPath = Path.Combine(_root, "other", "Other.esp") }
        };
        Assert.True((await _repository.SaveAsync(draft, Data, 0)).IsFailure);
        Assert.False(File.Exists(Sidecar));
    }

    [Fact]
    public async Task GamePathsAndProjectAliasing_AreRejected()
    {
        var draft = Draft();
        Assert.True((await _repository.SaveAsync(draft with
        {
            ProjectFilePath = Path.Combine(Data, "mod-project.json"), PluginPath = Path.Combine(Data, "Mod.esp")
        }, Data, 0)).IsFailure);
        Assert.True((await _repository.SaveAsync(draft with { ProjectFilePath = Sidecar }, Data, 0)).IsFailure);
        Assert.Empty(Directory.GetFiles(Data));
        Assert.False(File.Exists(Sidecar));
    }

    [Fact]
    public async Task CancelledSaveAndFailedReplacement_LeavePreviousGenerationAndCanRetry()
    {
        var draft = Draft();
        await _repository.SaveAsync(draft, Data, 0);
        var before = File.ReadAllBytes(Sidecar);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _repository.SaveAsync(draft, Data, 1, cancelled.Token));
        using (var locked = new FileStream(Sidecar, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.True((await _repository.SaveAsync(draft with { Name = "failed" }, Data, 1)).IsFailure);
        Assert.Equal(before, File.ReadAllBytes(Sidecar));
        Assert.Empty(Directory.GetFiles(Folder, "*.tmp"));
        Assert.True((await _repository.SaveAsync(draft with { Name = "retry" }, Data, 1)).IsSuccess);
    }

    [Fact]
    public async Task MissingOrReplacedDraft_IsNeverSilentlyRecreatedOverUnknownState()
    {
        var draft = Draft();
        await _repository.SaveAsync(draft, Data, 0);
        Assert.True((await _repository.SaveAsync(draft with { Id = Guid.NewGuid() }, Data, 1)).IsFailure);
        File.Delete(Sidecar);
        Assert.True((await _repository.SaveAsync(draft, Data, 1)).IsFailure);
        Assert.False(File.Exists(Sidecar));
    }

    [Theory]
    [InlineData("document")]
    [InlineData("item")]
    [InlineData("recipe")]
    public async Task MissingPersistedIdentity_IsRejectedInsteadOfGeneratingAnotherGuid(string target)
    {
        var draft = Draft();
        await _repository.SaveAsync(draft, Data, 0);
        var json = JsonNode.Parse(File.ReadAllText(Sidecar))!;
        var node = target switch
        {
            "item" => json["Items"]![0]!,
            "recipe" => json["Items"]![0]!["Recipes"]![0]!,
            _ => json
        };
        node.AsObject().Remove("Id");
        File.WriteAllText(Sidecar, json.ToJsonString());
        var before = File.ReadAllBytes(Sidecar);
        Assert.True((await _repository.LoadAsync(Project, Data)).IsFailure);
        Assert.True((await _repository.SaveAsync(draft, Data, 1)).IsFailure);
        Assert.Equal(before, File.ReadAllBytes(Sidecar));
    }

    [Fact]
    public async Task ExplicitRecoveryPreservesCorruptFile_AndInvalidatesOldWriterIdentity()
    {
        var draft = Draft();
        await _repository.SaveAsync(draft, Data, 0); await _repository.SaveAsync(draft, Data, 1);
        File.WriteAllText(Sidecar, "broken primary");
        var restored = await _repository.RestoreBackupAsync(Project, Data);
        Assert.True(restored.IsSuccess, restored.Error);
        Assert.NotEqual(draft.Id, restored.Value.Id);
        Assert.Equal(draft.Items.Select(i => i.Id), restored.Value.Items.Select(i => i.Id));
        Assert.Equal("broken primary", File.ReadAllText(Assert.Single(Directory.GetFiles(Folder, "*.recovery-*"))));
        Assert.True((await _repository.SaveAsync(draft, Data, restored.Value.Revision)).IsFailure);
        Assert.True((await _repository.SaveAsync(restored.Value, Data, restored.Value.Revision)).IsSuccess);
        Assert.Equal("confirmed plugin bytes", File.ReadAllText(Plugin));
    }
}
