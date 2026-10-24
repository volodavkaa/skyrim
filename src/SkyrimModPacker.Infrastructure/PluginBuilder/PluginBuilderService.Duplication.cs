using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.Infrastructure.PluginBuilder;

public sealed partial class PluginBuilderService
{
    public Task<Result<WeaponRecord>> DuplicateWeaponAsync(DuplicateWeaponRequest request, CancellationToken cancellationToken = default) =>
        RunWriteAsync(transaction =>
        {
            var mod = ReadWorkspaceForEditing(request.PluginPath, request.GameDataPath);
            var source = FindOwnedSword(mod, request.FormId);
            var recipes = mod.ConstructibleObjects.Where(r => !r.IsDeleted && r.CreatedObject.FormKey == source.FormKey).ToArray();
            if (recipes.Any(r => r.FormKey.ModKey != mod.ModKey))
                throw new InvalidOperationException("Recipes overridden from another plugin cannot be duplicated in this workspace.");
            var usedNames = mod.EnumerateMajorRecords().Select(r => r.EditorID ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
            string Unique(string? name, string fallback)
            {
                var stem = new string((name ?? fallback).Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').Take(105).ToArray());
                if (stem.Length == 0 || !char.IsAsciiLetter(stem[0])) stem = fallback;
                for (var suffix = 1; ; suffix++)
                {
                    var candidate = stem + "Copy" + suffix;
                    if (usedNames.Add(candidate)) return candidate;
                }
            }
            var copy = mod.Weapons.DuplicateInAsNewRecord(source, new FormKey(mod.ModKey, AllocateCompactId(mod)));
            copy.EditorID = Unique(source.EditorID, "Weapon");
            copy.Name = source.Name?.String ?? source.EditorID ?? "Weapon";
            // Own STAT copies are independent records; NIF bytes remain safely shared.
            var stat = mod.Statics.FirstOrDefault(s => s.FormKey == source.FirstPersonModel.FormKey && !s.IsDeleted && s.FormKey.ModKey == mod.ModKey);
            if (stat != null)
            {
                var copiedStat = mod.Statics.DuplicateInAsNewRecord(stat, new FormKey(mod.ModKey, AllocateCompactId(mod)));
                copiedStat.EditorID = Unique("SMP_FirstPerson_" + copy.FormKey.ID.ToString("X6"), "FirstPerson");
                copy.FirstPersonModel.SetTo(copiedStat.FormKey);
            }
            foreach (var recipe in recipes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cloned = mod.ConstructibleObjects.DuplicateInAsNewRecord(recipe, new FormKey(mod.ModKey, AllocateCompactId(mod)));
                cloned.EditorID = Unique(recipe.EditorID, "Recipe");
                cloned.CreatedObject.SetTo(copy.FormKey);
            }
            WeaponProjectService.WriteWithWeaponDuplication(request.ProjectFilePath, request.PluginPath, request.GameDataPath,
                source.FormKey.ID, copy.FormKey.ID, () => WritePlugin(transaction, mod, request.PluginPath, request.GameDataPath,
                    replace: true, cancellationToken, mod.ModHeader.Stats.NextFormID), cancellationToken);
            return ToWeaponRecord(copy, mod);
        }, () => request.PluginPath, request.GameDataPath, cancellationToken);
}
