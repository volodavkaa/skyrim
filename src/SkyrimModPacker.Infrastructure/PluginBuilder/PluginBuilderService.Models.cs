using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.Infrastructure.PluginBuilder;

public sealed partial class PluginBuilderService
{
    public Task<Result<WeaponModels>> GetWeaponModelsAsync(string pluginPath, string gameDataPath, uint formId, CancellationToken cancellationToken = default) =>
        RunAsync(() =>
        {
            using var mod = SkyrimMod.CreateFromBinaryOverlay(pluginPath, SkyrimRelease.SkyrimSE);
            var sword = mod.Weapons.FirstOrDefault(w => w.FormKey.ModKey == mod.ModKey && w.FormKey.ID == formId && !w.IsDeleted)
                ?? throw new InvalidOperationException("Weapon not found.");
            var reference = Reference(sword.FirstPersonModel.FormKey);
            if (reference == null) return new WeaponModels(sword.Model?.File.GivenPath ?? "", null, null);
            try
            {
                var key = sword.FirstPersonModel.FormKey;
                var local = mod.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == key);
                if (local != null || key.ModKey == mod.ModKey)
                {
                    var target = local as IStaticGetter;
                    if (target == null || target.IsDeleted)
                        throw new InvalidOperationException("WNAM does not resolve to a non-deleted STAT.");
                    return new WeaponModels(sword.Model?.File.GivenPath ?? "", reference, target.Model?.File.GivenPath);
                }
                if (!mod.ModHeader.MasterReferences.Any(m => m.Master == key.ModKey))
                    throw new InvalidOperationException("WNAM refers to an undeclared master.");
                // Later declared masters may override the originating STAT.
                foreach (var master in mod.ModHeader.MasterReferences.Reverse())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var source = ReadMaster(gameDataPath, master.Master.FileName.String);
                    var target = source.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == key);
                    if (target == null) continue;
                    if (target is not IStaticGetter stat || stat.IsDeleted)
                        throw new InvalidOperationException("WNAM target is deleted or is not a STAT.");
                    return new WeaponModels(sword.Model?.File.GivenPath ?? "", reference, stat.Model?.File.GivenPath);
                }
                throw new InvalidOperationException("WNAM target is missing.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new WeaponModels(sword.Model?.File.GivenPath ?? "", reference, null, ex.Message);
            }
        }, cancellationToken);

    private static string NifPath(string path)
    {
        path = path.Trim().Replace('/', '\\');
        if (path.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase)) path = path[7..];
        if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".nif", StringComparison.OrdinalIgnoreCase) ||
            path.Split('\\').Any(p => string.IsNullOrWhiteSpace(p) || p is "." or ".." ||
                p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || p.EndsWith('.') || p.EndsWith(' ')))
            throw new InvalidOperationException("Use a safe relative NIF path inside meshes.");
        return path;
    }

    private static void ApplyFirstPersonModel(ISkyrimMod mod, IWeapon sword, WeaponDetails details, string gameDataPath)
    {
        if (details.ClearFirstPersonModel) { sword.FirstPersonModel.SetTo((FormKey?)null); return; }
        if (details.FirstPersonModelPath == null && details.FirstPersonModel == null) return;
        using var resolver = new RecordResolver(mod, gameDataPath);
        if (details.FirstPersonModelPath == null)
        {
            var original = resolver.Resolve<IStaticGetter>(details.FirstPersonModel!);
            if (original.FormKey.ModKey != mod.ModKey && !WeaponTypes.IsVanillaSource(original.FormKey.ModKey.FileName.String))
                throw new InvalidOperationException("Select a first-person STAT owned by this plugin or a supported vanilla master.");
            _ = NifPath(original.Model?.File.GivenPath ?? "");
            sword.FirstPersonModel.SetTo(original.FormKey);
            return;
        }
        var path = NifPath(details.FirstPersonModelPath);
        IStaticGetter? source = null;
        if (sword.FirstPersonModel.FormKey != FormKey.Null)
            source = mod.Statics.FirstOrDefault(s => s.FormKey == sword.FirstPersonModel.FormKey && !s.IsDeleted)
                ?? resolver.Resolve<IStaticGetter>(Reference(sword.FirstPersonModel.FormKey)!);
        // Same path needs no write, even if the current STAT is shared.
        if (source?.Model?.File.GivenPath.Equals(path, StringComparison.OrdinalIgnoreCase) == true) return;
        var shared = source != null && mod.EnumerateMajorRecords().Any(r => r.FormKey != sword.FormKey &&
            r.EnumerateFormLinks().Any(l => l.FormKey == source.FormKey));
        IStatic target;
        if (source is IStatic owned && owned.FormKey.ModKey == mod.ModKey && !shared)
            target = owned;
        else
        {
            var prefix = "SMP_FirstPerson_" + sword.FormKey.ID.ToString("X6") + "_";
            var reusable = mod.Statics.FirstOrDefault(s => s.FormKey.ModKey == mod.ModKey && !s.IsDeleted &&
                s.EditorID?.StartsWith(prefix, StringComparison.Ordinal) == true &&
                !mod.EnumerateMajorRecords().Any(r => r.FormKey != sword.FormKey && r.EnumerateFormLinks().Any(l => l.FormKey == s.FormKey)));
            if (reusable != null)
            {
                reusable.Model = new Model { File = path };
                sword.FirstPersonModel.SetTo(reusable.FormKey);
                return;
            }
            var id = AllocateCompactId(mod);
            target = source == null ? new Static(new FormKey(mod.ModKey, id), SkyrimRelease.SkyrimSE) :
                mod.Statics.DuplicateInAsNewRecord(source, new FormKey(mod.ModKey, id));
            if (source == null) mod.Statics.Add((Static)target);
            target.EditorID = prefix + id.ToString("X6");
            if (mod.EnumerateMajorRecords().Any(r => r.FormKey != target.FormKey &&
                string.Equals(r.EditorID, target.EditorID, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Generated first-person STAT EditorID already exists.");
        }
        // Keep bounds and other inherited STAT fields, but drop old model texture substitutions.
        target.Model = new Model { File = path };
        sword.FirstPersonModel.SetTo(target.FormKey);
    }
}
