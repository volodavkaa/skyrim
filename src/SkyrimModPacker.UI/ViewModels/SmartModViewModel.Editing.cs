using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.UI.ViewModels;

public sealed partial class SmartModViewModel
{
    private static WeaponDraftFields Fields(string editorId, string name, ushort damage, float weight, uint value, WeaponDetails details) => new()
    {
        EditorId = editorId, Name = name, Damage = damage.ToString(), Weight = weight.ToString(CultureInfo.InvariantCulture), Value = value.ToString(),
        Description = details.Description, Speed = details.Speed.ToString(CultureInfo.InvariantCulture), Reach = details.Reach.ToString(CultureInfo.InvariantCulture),
        ModelPath = details.ModelPath, FirstPersonPath = details.FirstPersonModelPath ?? "", FirstPersonReference = details.FirstPersonModel,
        UseSharedModel = !string.IsNullOrWhiteSpace(details.ModelPath) && details.ModelPath.Equals(details.FirstPersonModelPath, StringComparison.OrdinalIgnoreCase),
        Keywords = details.Keywords.ToArray(), Sound = details.AttackSound, Sounds = details.Sounds,
        Enchantment = details.Enchantment, EnchantmentCharge = details.EnchantmentCharge.ToString()
    };
    private SmartModItemRow AddItemCore(WeaponKind kind)
    {
        var template = Templates.Where(t => t.Kind == kind && !string.IsNullOrWhiteSpace(t.Details.ModelPath))
            .OrderBy(t => t.EditorId.StartsWith("Iron", StringComparison.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault()
            ?? Templates.FirstOrDefault(t => t.Kind == kind) ?? throw new InvalidOperationException(Label("Smart_SelectTemplate"));
        var draft = new SmartModItemDraft { Kind = kind, Template = new(template.PluginName, template.FormId) };
        var ordinal = Items.Count(i => i.Kind == kind) + 1;
        var fields = Fields("SMP_" + draft.Id.ToString("N"), Label("WeaponType_" + kind) + " " + ordinal,
            template.Damage, template.Weight, template.Value, template.Details);
        var row = new SmartModItemRow(draft with { Weapon = fields }, Label);
        Attach(row); Changed(); return row;
    }
    public Task AddItemAsync(WeaponKind kind) => RunAsync(async token =>
    {
        if (!HasProject) return;
        SelectedItem = AddItemCore(kind); Section = SmartModSection.Weapons;
        await ResolveFirstPersonAsync(SelectedItem, token); await PersistAsync(token);
    });
    private async Task ResolveFirstPersonAsync(SmartModItemRow item, CancellationToken token)
    {
        if (item.Fields.FirstPersonEdited || item.Fields.RestoreFirstPerson || !string.IsNullOrWhiteSpace(item.FirstPersonPath) || item.Template == null) return;
        var reference = item.Template;
        var models = Require(await _builder.GetWeaponModelsAsync(item.ConfirmedFormId == null ? Path.Combine(_gameData, reference.PluginName) : PluginPath,
            _gameData, item.ConfirmedFormId ?? reference.FormId, token));
        if (models.Error != null) { item.PreviewNotice = models.Error; return; }
        item.Replace(item.Capture() with { Weapon = item.Fields with { FirstPersonPath = models.FirstPersonPath ?? "", FirstPersonReference = models.FirstPersonReference } });
    }

    public Task ApplyTemplateAsync(bool keepCustom) => RunAsync(async token =>
    {
        if (SelectedItem is not { } item || SelectedTemplate is not { } template || item.Kind != template.Kind) return;
        var previousTemplate = Templates.FirstOrDefault(t => t.FormId == item.Template?.FormId && t.PluginName == item.Template.PluginName);
        var fresh = Fields(item.EditorId, item.Name, template.Damage, template.Weight, template.Value, template.Details);
        if (keepCustom)
        {
            var old = previousTemplate == null ? null : Fields(item.EditorId, item.Name, previousTemplate.Damage, previousTemplate.Weight, previousTemplate.Value, previousTemplate.Details);
            var current = item.Fields;
            // Replace unchanged baseline fields only; preserve explicit user removals as well as added values.
            fresh = fresh with
            {
                Damage = old == null || current.Damage != old.Damage ? current.Damage : fresh.Damage,
                Weight = old == null || current.Weight != old.Weight ? current.Weight : fresh.Weight,
                Value = old == null || current.Value != old.Value ? current.Value : fresh.Value,
                Speed = old == null || current.Speed != old.Speed ? current.Speed : fresh.Speed,
                Reach = old == null || current.Reach != old.Reach ? current.Reach : fresh.Reach,
                Description = old == null || current.Description != old.Description ? current.Description : fresh.Description,
                ModelPath = old == null || current.ModelPath != old.ModelPath ? current.ModelPath : fresh.ModelPath,
                UseSharedModel = current.UseSharedModel,
                FirstPersonPath = current.FirstPersonEdited || current.RestoreFirstPerson ? current.FirstPersonPath : "", FirstPersonEdited = current.FirstPersonEdited,
                FirstPersonReference = current.FirstPersonEdited || current.RestoreFirstPerson ? current.FirstPersonReference : fresh.FirstPersonReference, RestoreFirstPerson = current.RestoreFirstPerson,
                Sound = old == null || current.Sound != old.Sound ? current.Sound : fresh.Sound,
                Sounds = old == null || !SameSounds(current.Sounds, old.Sounds) ? current.Sounds : fresh.Sounds,
                Keywords = old == null || !current.Keywords.ToHashSet().SetEquals(old.Keywords) ? current.Keywords : fresh.Keywords,
                Enchantment = old == null || current.Enchantment != old.Enchantment ? current.Enchantment : fresh.Enchantment,
                EnchantmentCharge = old == null || current.EnchantmentCharge != old.EnchantmentCharge ? current.EnchantmentCharge : fresh.EnchantmentCharge
            };
        }
        if (fresh.UseSharedModel) fresh = fresh with { FirstPersonPath = fresh.ModelPath, FirstPersonEdited = true, RestoreFirstPerson = false };
        item.Replace(item.Capture() with { Template = new(template.PluginName, template.FormId), Weapon = fresh, State = SmartModItemState.InProgress });
        await ResolveFirstPersonAsync(item, token); await PersistAsync(token);
    });
    private static bool SameSounds(IReadOnlyDictionary<string, PluginRecordReference?>? a, IReadOnlyDictionary<string, PluginRecordReference?>? b) =>
        (a?.Count ?? 0) == (b?.Count ?? 0) && (a == null || a.All(p => b?.GetValueOrDefault(p.Key) == p.Value));

    [RelayCommand]
    private Task RestoreTemplateAsync(string? part) => RunAsync(async token =>
    {
        if (SelectedItem is not { } item) return;
        var template = Templates.FirstOrDefault(t => t.FormId == item.Template?.FormId && t.PluginName == item.Template.PluginName);
        if (template == null) { Error = Label("Smart_UnknownTemplate"); return; }
        var source = Fields(item.EditorId, item.Name, template.Damage, template.Weight, template.Value, template.Details);
        if (part == "models")
        {
            var models = Require(await _builder.GetWeaponModelsAsync(Path.Combine(_gameData, template.PluginName), _gameData, template.FormId, token));
            if (models.Error != null) throw new IOException(models.Error);
            source = source with { ModelPath = models.ThirdPersonPath, FirstPersonPath = models.FirstPersonPath ?? "", FirstPersonReference = models.FirstPersonReference };
        }
        var current = item.Fields;
        item.Replace(item.Capture() with { State = SmartModItemState.InProgress, Weapon = part switch
        {
            "sounds" => current with { Sound = source.Sound, Sounds = source.Sounds },
            "keywords" => current with { Keywords = source.Keywords },
            "models" => current with { ModelPath = source.ModelPath, FirstPersonPath = source.FirstPersonPath, FirstPersonEdited = false, UseSharedModel = false,
                RestoreFirstPerson = true, FirstPersonReference = source.FirstPersonReference },
            _ => current with { Damage = source.Damage, Weight = source.Weight, Value = source.Value, Speed = source.Speed,
                Reach = source.Reach, Description = source.Description, Enchantment = source.Enchantment, EnchantmentCharge = source.EnchantmentCharge }
        } });
        await PersistAsync(token);
    });
    [RelayCommand] private void AddKeyword() { if (SelectedItem != null && KeywordToAdd != null) SelectedItem.SetKeywords(SelectedItem.Fields.Keywords.Append(KeywordToAdd.Reference).Distinct().ToArray()); }
    [RelayCommand] private void RemoveKeyword() { if (SelectedItem != null && SelectedKeyword != null) SelectedItem.SetKeywords(SelectedItem.Fields.Keywords.Where(k => k != SelectedKeyword).ToArray()); }
    [RelayCommand] private void ClearSound() { if (SelectedItem != null) SelectedItem.Sound = null; }
    [RelayCommand] private void ClearEnchantment() { if (SelectedItem != null) { SelectedItem.Enchantment = null; SelectedItem.EnchantmentCharge = "0"; } }

    public Task DuplicateSelectedAsync(bool copyRecipes) => RunAsync(async token =>
    {
        if (SelectedItem is not { } item) return;
        if (copyRecipes && item.Recipes.GroupBy(r => r.IsTempering).Any(g => g.Count() > 1))
            throw new InvalidOperationException(Label("Smart_RecipeConflict"));
        var copy = item.Capture() with { Id = Guid.NewGuid(), ConfirmedFormId = null, State = SmartModItemState.InProgress };
        copy = copy with
        {
            Weapon = copy.Weapon with { EditorId = "SMP_" + copy.Id.ToString("N"), Name = item.Name + " " + Label("Smart_Copy"),
                // A copied custom first-person model needs its own STAT when confirmed.
                FirstPersonEdited = !string.IsNullOrWhiteSpace(copy.Weapon.FirstPersonPath), RestoreFirstPerson = false },
            Recipes = copyRecipes ? copy.Recipes.Select(r => { var id = Guid.NewGuid(); return r with { Id = id, ConfirmedFormId = null, EditorId = "SMP_Recipe_" + id.ToString("N") }; }).ToArray() : []
        };
        var row = new SmartModItemRow(copy, Label); Attach(row); SelectedItem = row;
        await PersistAsync(token);
    });

    public Task DeleteSelectedAsync() => RunAsync(async token =>
    {
        if (SelectedItem is not { } item) return;
        if (item.ConfirmedFormId is { } form)
        {
            var records = Require(await _builder.GetWeaponsAsync(PluginPath, token));
            // A missing/replaced record can be removed from the draft without deleting another writer's record.
            if (records.Any(r => r.FormId == form && r.Kind == item.Kind))
            {
                var revision = await CheckWorkspaceAsync(token); using var expectation = revision == null ? null : new WorkspaceWriteExpectation(revision);
                var result = await _builder.DeleteSwordAsync(PluginPath, _gameData, form, token, ProjectPath, deleteRecipes: true);
                if (result.IsFailure) throw new InvalidOperationException(result.Error);
                await RefreshBaselineAsync(token);
            }
        }
        Items.Remove(item); SelectedItem = Items.FirstOrDefault(); if (SelectedItem == null) Section = SmartModSection.Overview;
        await PersistAsync(token); await RefreshResourcesAsync(token);
    });

    [RelayCommand]
    private void AddRecipe(string? kind)
    {
        if (IsBusy || SelectedItem is not { } item) return;
        var tempering = kind == "tempering";
        if (item.Recipes.FirstOrDefault(r => r.IsTempering == tempering) is { } existing)
        {
            SelectedRecipe = existing; item.Step = SmartModItemStep.Acquisition; return;
        }
        var id = Guid.NewGuid();
        var bench = Options.Keywords.FirstOrDefault(k => k.EditorId == (tempering ? "CraftingSmithingSharpeningWheel" : "CraftingSmithingForge"));
        if (bench == null) { Error = Label("Smart_NoWorkbench"); return; }
        var recipe = new SmartModRecipeRow(new() { Id = id, EditorId = "SMP_Recipe_" + id.ToString("N"), IsTempering = tempering, Workbench = bench.Reference }, Label);
        item.Recipes.Add(recipe); SelectedRecipe = recipe; item.Step = SmartModItemStep.Acquisition;
    }
    [RelayCommand] private void AddIngredient() { if (!IsBusy && SelectedRecipe != null) SelectedRecipe.Ingredients.Add(new(new(null, "1"))); }
    [RelayCommand] private void RemoveIngredient(SmartModIngredientRow? ingredient) { if (!IsBusy && ingredient != null) SelectedRecipe?.Ingredients.Remove(ingredient); }
    public Task DeleteRecipeAsync() => RunAsync(async token =>
    {
        if (SelectedRecipe is not { } recipe || SelectedItem is not { } item) return;
        if (item.Recipes.Count(r => r.IsTempering == recipe.IsTempering) > 1) throw new InvalidOperationException(Label("Smart_RecipeConflict"));
        if (recipe.ConfirmedFormId is { } form)
        {
            IReadOnlyList<CraftingRecipe> recipes = item.ConfirmedFormId == null ? [] : Require(await _builder.GetRecipesAsync(PluginPath, item.ConfirmedFormId.Value, token));
            if (recipes.Any(r => r.FormId == form))
            {
                var revision = await CheckWorkspaceAsync(token); using var expectation = revision == null ? null : new WorkspaceWriteExpectation(revision);
                var result = await _builder.DeleteRecipeAsync(PluginPath, _gameData, form, token);
                if (result.IsFailure) throw new InvalidOperationException(result.Error);
                await RefreshBaselineAsync(token);
            }
        }
        item.Recipes.Remove(recipe); SelectedRecipe = item.Recipes.FirstOrDefault(); await PersistAsync(token);
    });

    [RelayCommand]
    private void Previous()
    {
        if (IsBusy) return;
        if (SelectedItem is { Step: > SmartModItemStep.NameAndTemplate } item) item.Step--;
        else if (SelectedItem != null && Items.IndexOf(SelectedItem) > 0) { SelectedItem = Items[Items.IndexOf(SelectedItem) - 1]; SelectedItem.Step = SmartModItemStep.Summary; }
        else Section = SmartModSection.Overview;
        NotifyStep();
    }
    [RelayCommand]
    private void Next()
    {
        if (IsBusy) return;
        if (!HasProject) return;
        if (!IsEditing) { SelectedItem ??= Items.FirstOrDefault(); Section = SmartModSection.Weapons; }
        else if (SelectedItem is { Step: < SmartModItemStep.Summary } item) item.Step++;
        else if (SelectedItem != null && Items.IndexOf(SelectedItem) + 1 < Items.Count) SelectedItem = Items[Items.IndexOf(SelectedItem) + 1];
        else Section = SmartModSection.Checks;
        NotifyStep();
    }
    [RelayCommand] private void Navigate(string? section) { if (!IsBusy && Enum.TryParse<SmartModSection>(section, out var value)) Section = value; }
    [RelayCommand] private void NavigateRecipe(SmartModRecipeLink? link) { if (IsBusy || link == null) return; SelectedItem = link.Item; SelectedRecipe = link.Recipe; link.Item.Step = SmartModItemStep.Acquisition; Section = SmartModSection.Recipes; NotifyStep(); }
    [RelayCommand]
    private void NavigateIssue(SmartModIssue? issue)
    {
        if (IsBusy || issue == null) return;
        if (issue.ItemId is { } id && Items.FirstOrDefault(i => i.Id == id) is { } item)
        {
            SelectedItem = item; item.Step = issue.Step ?? (issue.RecipeId == null ? SmartModItemStep.Properties : SmartModItemStep.Acquisition);
            SelectedRecipe = item.Recipes.FirstOrDefault(r => r.Id == issue.RecipeId); Section = SmartModSection.Weapons;
        }
        else if (issue.ResourcePath != null) { SelectedResource = Resources.FirstOrDefault(r => r.RelativePath == issue.ResourcePath); Section = SmartModSection.Resources; }
        NotifyStep();
    }

    private string DescribeInheritance()
    {
        if (SelectedItem is not { } item) return "";
        var template = Templates.FirstOrDefault(t => t.FormId == item.Template?.FormId && t.PluginName == item.Template.PluginName);
        if (template == null) return Label("Smart_UnknownTemplate");
        string Ref(PluginRecordReference? reference) => reference == null ? "—" :
            Options.Sounds.Concat(Options.Keywords).FirstOrDefault(o => o.Reference == reference)?.DisplayName + " [" + reference + "]";
        var source = template.Details;
        var lines = new List<string> { template.Name + " [" + template.PluginName + ":" + template.FormId.ToString("X6") + "]" };
        foreach (var slot in (source.Sounds?.Keys ?? []).Concat(item.Fields.Sounds?.Keys ?? []).Append("AttackSound").Distinct())
        {
            var original = slot == "AttackSound" ? source.AttackSound : source.Sounds?.GetValueOrDefault(slot);
            var current = slot == "AttackSound" ? item.Sound : item.Fields.Sounds?.GetValueOrDefault(slot);
            lines.Add(Label("Items_Sound_" + slot) + ": " + Ref(original) + " → " + Ref(current) + " — " + Label(original == current ? "Items_Inherited" : "Items_Changed"));
        }
        lines.Add(Label("Weapons_Keywords") + ": " + string.Join(", ", source.Keywords.Select(Ref)) + " → " + string.Join(", ", item.Fields.Keywords.Select(Ref)));
        lines.Add(Label("Weapons_Damage") + ": " + template.Damage + " → " + item.Damage);
        lines.Add(Label("Weapons_Weight") + ": " + template.Weight + " → " + item.Weight);
        lines.Add(Label("Weapons_Value") + ": " + template.Value + " → " + item.Value);
        return string.Join(Environment.NewLine, lines);
    }
}
