using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models.PluginBuilder;
using Microsoft.Extensions.Logging;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace SkyrimModPacker.Infrastructure.PluginBuilder;

/// <summary>Builds workspace plugins and duplicates vanilla one-handed swords without changing the game files.</summary>
public sealed partial class PluginBuilderService : IPluginBuilderService
{
    private readonly ILogger<PluginBuilderService>? _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public PluginBuilderService(ILogger<PluginBuilderService>? logger = null)
    {
        _logger = logger;
    }

    public Task<Result<string>> CreateAsync(CreatePluginRequest request, CancellationToken cancellationToken = default)
    {
        return RunWriteAsync(transaction =>
        {
            ValidateDataPath(request.GameDataPath);
            ValidateFileName(request.PluginFileName, allowMaster: false);
            if (string.IsNullOrWhiteSpace(request.OutputDirectory) || !Path.IsPathFullyQualified(request.OutputDirectory))
                throw new InvalidOperationException("Select an absolute workspace output directory.");
            var path = Path.GetFullPath(Path.Combine(request.OutputDirectory, request.PluginFileName));
            PluginWriteGuard.EnsureOutsideGame(path, request.GameDataPath);
            if (File.Exists(path))
                throw new InvalidOperationException("The plugin already exists. Choose another name.");

            var key = ModKey.FromNameAndExtension(request.PluginFileName);
            var masters = new List<ModKey>();
            foreach (var name in request.Masters)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateFileName(name, allowMaster: true);
                var masterKey = ModKey.FromNameAndExtension(name);
                if (masterKey == key || masters.Contains(masterKey))
                    throw new InvalidOperationException("Masters must be unique and must not include the output plugin.");
                using var master = ReadMaster(request.GameDataPath, name);
                if (master.ModHeader.MasterReferences.Any(parent => !masters.Contains(parent.Master)))
                    throw new InvalidOperationException($"Include the parent masters of {name} first.");
                masters.Add(masterKey);
            }

            var isEsl = Path.GetExtension(path).Equals(".esl", StringComparison.OrdinalIgnoreCase);
            var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE)
            {
                IsSmallMaster = isEsl,
                IsMaster = isEsl
            };
            mod.ModHeader.Stats.Version = 1.70f;
            mod.ModHeader.Stats.NextFormID = 0x800;
            foreach (var master in masters)
                mod.ModHeader.MasterReferences.Add(new MasterReference { Master = master });
            WritePlugin(transaction, mod, path, request.GameDataPath, replace: false, cancellationToken);
            return path;
        }, () => CreatePluginPath(request), request.GameDataPath, cancellationToken);
    }

    public Task<Result<IReadOnlyList<SwordTemplate>>> GetSwordTemplatesAsync(string gameDataPath, CancellationToken cancellationToken = default)
        => GetTemplatesAsync(gameDataPath, true, cancellationToken);

    public Task<Result<IReadOnlyList<SwordTemplate>>> GetWeaponTemplatesAsync(string gameDataPath, CancellationToken cancellationToken = default)
        => GetTemplatesAsync(gameDataPath, false, cancellationToken);

    private Task<Result<IReadOnlyList<SwordTemplate>>> GetTemplatesAsync(string gameDataPath, bool legacySwords, CancellationToken cancellationToken)
    {
        return RunAsync<IReadOnlyList<SwordTemplate>>(() =>
        {
            ValidateDataPath(gameDataPath);
            if (!File.Exists(Path.Combine(gameDataPath, "Skyrim.esm"))) throw new FileNotFoundException("Skyrim.esm is missing.");
            var templates = new List<SwordTemplate>();
            foreach (var name in legacySwords ? new[] { "Skyrim.esm" } : WeaponTypes.VanillaSources.Where(n => File.Exists(Path.Combine(gameDataPath, n))))
            {
            using var source = ReadMaster(gameDataPath, name);
            foreach (var weapon in source.Weapons)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (weapon.FormKey.ModKey != source.ModKey || !WeaponTypes.Supported(weapon.Data?.AnimationType) || (legacySwords && weapon.Data?.AnimationType != WeaponAnimationType.OneHandSword) || weapon.IsDeleted || (!legacySwords && !WeaponTypes.PlayerTemplate(weapon)))
                    continue;
                templates.Add(new SwordTemplate
                {
                    FormId = weapon.FormKey.ID,
                    PluginName = name,
                    Kind = WeaponTypes.Kind(weapon),
                    EditorId = weapon.EditorID ?? string.Empty,
                    Name = weapon.Name?.String ?? weapon.EditorID ?? string.Empty,
                    Damage = weapon.BasicStats?.Damage ?? 0,
                    Weight = weapon.BasicStats?.Weight ?? 0,
                    Value = weapon.BasicStats?.Value ?? 0,
                    Details = ToDetails(weapon, source)
                });
            }
            }
            return templates.OrderBy(t => t.EditorId, StringComparer.OrdinalIgnoreCase).ToList();
        }, cancellationToken);
    }

    public Task<Result<WeaponRecord>> CloneSwordAsync(CloneSwordRequest request, CancellationToken cancellationToken = default)
    {
        return RunWriteAsync(transaction =>
        {
            ValidateDataPath(request.GameDataPath);
            if (!Path.IsPathFullyQualified(request.PluginPath))
                throw new InvalidOperationException("Select an absolute workspace plugin path.");
            ValidateFileName(Path.GetFileName(request.PluginPath), allowMaster: false);
            PluginWriteGuard.EnsureOutsideGame(request.PluginPath, request.GameDataPath);
            if (string.IsNullOrWhiteSpace(request.EditorId) || request.EditorId.Length > 128 ||
                !char.IsAsciiLetter(request.EditorId[0]) || request.EditorId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
                throw new InvalidOperationException("EditorID must start with an ASCII letter and contain only letters, digits or underscores (up to 128 characters).");
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Contains('\0'))
                throw new InvalidOperationException("A weapon name is required and must not contain a null character.");
            if (request.Weight is { } weight && (!float.IsFinite(weight) || weight < 0))
                throw new InvalidOperationException("Weight must be a finite non-negative number.");

            var mod = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(request.PluginPath)
                .WithUtf8Encoding().Mutable().Construct();
            if (mod.UsingLocalization)
                throw new InvalidOperationException("Editing localized output plugins is not supported in this stage.");
            var records = mod.EnumerateMajorRecords().ToList();
            if (request.VariantParentFormId is { } parentId)
            {
                _ = FindOwnedSword(mod, parentId);
                if (string.IsNullOrWhiteSpace(request.ProjectFilePath) || string.IsNullOrWhiteSpace(request.VariantLabel) || request.VariantLabel.Length > 256 ||
                    request.VariantMaterialNotes.Length > 4096 || request.VariantLabel.Contains('\0') || request.VariantMaterialNotes.Contains('\0'))
                    throw new InvalidOperationException("A variant requires project metadata and a valid label/material description.");
            }
            if (records.Any(r => string.Equals(r.EditorID, request.EditorId, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The EditorID already exists in the output plugin.");
            var ownedIds = records.Where(r => r.FormKey.ModKey == mod.ModKey).Select(r => r.FormKey.ID).ToList();
            var minimumId = mod.ModHeader.Stats.Version >= 1.71f ? 1u : 0x800u;
            if (ownedIds.Any(id => id < minimumId || id > 0xFFF))
                throw new InvalidOperationException("The output plugin contains non-compact FormIDs. Compaction is not performed.");
            var nextId = Math.Max(0x800u, Math.Max(mod.ModHeader.Stats.NextFormID, ownedIds.DefaultIfEmpty(0x7FFu).Max() + 1));
            if (nextId > 0xFFF)
                throw new InvalidOperationException("The compact FormID range is exhausted.");

            if (!WeaponTypes.IsVanillaSource(request.TemplatePluginName)) throw new InvalidOperationException("Select a supported vanilla master template.");
            using var source = ReadMaster(request.GameDataPath, request.TemplatePluginName);
            var template = source.Weapons.FirstOrDefault(w => w.FormKey.ModKey == source.ModKey && w.FormKey.ID == request.TemplateFormId);
            if (template == null || template.IsDeleted || !WeaponTypes.Supported(template.Data?.AnimationType) || !WeaponTypes.PlayerTemplate(template) || template.BasicStats == null || string.IsNullOrWhiteSpace(template.Model?.File.GivenPath))
                throw new InvalidOperationException("Select a supported vanilla weapon template with basic statistics and a model.");

            var skyrimKey = ModKey.FromNameAndExtension("Skyrim.esm");
            if (mod.ModHeader.MasterReferences.All(m => m.Master != skyrimKey))
                mod.ModHeader.MasterReferences.Insert(0, new MasterReference { Master = skyrimKey });
            AddTemplateMasters(mod, source, request.GameDataPath, new HashSet<ModKey>());
            var sword = mod.Weapons.DuplicateInAsNewRecord(template, new FormKey(mod.ModKey, nextId));
            mod.ModHeader.Stats.NextFormID = nextId + 1;
            sword.EditorID = request.EditorId;
            sword.Name = request.Name;
            if (request.Damage.HasValue)
                sword.BasicStats!.Damage = request.Damage.Value;
            if (request.Weight.HasValue)
                sword.BasicStats!.Weight = request.Weight.Value;
            if (request.Value.HasValue)
                sword.BasicStats!.Value = request.Value.Value;
            if (request.Details != null)
                ApplyDetails(mod, sword, request.Details, request.GameDataPath);
            var result = new WeaponRecord
            {
                FormId = nextId,
                Kind = WeaponTypes.Kind(sword),
                EditorId = sword.EditorID,
                Name = request.Name,
                Damage = sword.BasicStats?.Damage ?? 0,
                Weight = sword.BasicStats?.Weight ?? 0,
                Value = sword.BasicStats?.Value ?? 0,
                Details = ToDetails(sword, mod)
            };
            WeaponProjectService.WriteWithTemplateOrigin(request.ProjectFilePath, request.PluginPath, request.GameDataPath,
                nextId, request.TemplateFormId, () => WritePlugin(transaction, mod, request.PluginPath, request.GameDataPath,
                    replace: true, cancellationToken, mod.ModHeader.Stats.NextFormID), cancellationToken, request.TemplatePluginName,
                    request.VariantParentFormId is { } variantParent ? new WeaponVariant(nextId, variantParent, request.VariantLabel, request.VariantMaterialNotes) : null);
            return result;
        }, () => request.PluginPath, request.GameDataPath, cancellationToken);
    }

    public Task<Result<IReadOnlyList<WeaponRecord>>> GetWeaponsAsync(string pluginPath, CancellationToken cancellationToken = default)
    {
        return RunAsync<IReadOnlyList<WeaponRecord>>(() =>
        {
            ValidateFileName(Path.GetFileName(pluginPath), allowMaster: false);
            using var mod = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(pluginPath).WithUtf8Encoding().Construct();
            if (mod.UsingLocalization)
                throw new InvalidOperationException("Editing localized output plugins is not supported in this stage.");
            var weapons = new List<WeaponRecord>();
            foreach (var weapon in mod.Weapons)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (weapon.FormKey.ModKey == mod.ModKey && !weapon.IsDeleted && WeaponTypes.Supported(weapon.Data?.AnimationType))
                    weapons.Add(ToWeaponRecord(weapon, mod));
            }
            return weapons.OrderBy(w => w.EditorId, StringComparer.OrdinalIgnoreCase).ToList();
        }, cancellationToken);
    }

    public Task<Result<WeaponRecord>> UpdateSwordAsync(UpdateSwordRequest request, CancellationToken cancellationToken = default)
    {
        return RunWriteAsync(transaction =>
        {
            var mod = ReadWorkspaceForEditing(request.PluginPath, request.GameDataPath);
            var sword = FindOwnedSword(mod, request.FormId);
            if (string.IsNullOrWhiteSpace(request.EditorId) || request.EditorId.Length > 128 ||
                !char.IsAsciiLetter(request.EditorId[0]) || request.EditorId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
                throw new InvalidOperationException("EditorID must start with an ASCII letter and contain only letters, digits or underscores (up to 128 characters).");
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Contains('\0'))
                throw new InvalidOperationException("A weapon name is required and must not contain a null character.");
            if (!float.IsFinite(request.Weight) || request.Weight < 0)
                throw new InvalidOperationException("Weight must be a finite non-negative number.");
            if (mod.EnumerateMajorRecords().Any(r => r.FormKey != sword.FormKey && string.Equals(r.EditorID, request.EditorId, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The EditorID already exists in the output plugin.");
            sword.EditorID = request.EditorId;
            sword.Name = request.Name;
            sword.BasicStats!.Damage = request.Damage;
            sword.BasicStats.Weight = request.Weight;
            sword.BasicStats.Value = request.Value;
            if (request.Details != null)
                ApplyDetails(mod, sword, request.Details, request.GameDataPath);
            // Preserve the allocation high-water mark, including IDs of deleted records.
            var nextId = mod.ModHeader.Stats.NextFormID;
            if (request.VanillaTemplateFormId is { } templateId)
                WeaponProjectService.WriteWithTemplateOrigin(request.ProjectFilePath, request.PluginPath, request.GameDataPath,
                    sword.FormKey.ID, templateId, () => WritePlugin(transaction, mod, request.PluginPath, request.GameDataPath,
                        replace: true, cancellationToken, nextId), cancellationToken, request.VanillaTemplatePluginName);
            else WritePlugin(transaction, mod, request.PluginPath, request.GameDataPath, replace: true, cancellationToken, nextId);
            return ToWeaponRecord(sword, mod);
        }, () => request.PluginPath, request.GameDataPath, cancellationToken);
    }

    public async Task<Result> DeleteSwordAsync(string pluginPath, string gameDataPath, uint formId, CancellationToken cancellationToken = default, string? projectFilePath = null)
    {
        return await RunWriteAsync(transaction =>
        {
            var mod = ReadWorkspaceForEditing(pluginPath, gameDataPath);
            var sword = FindOwnedSword(mod, formId);
            if (mod.EnumerateMajorRecords().Any(r => r.FormKey != sword.FormKey && r.EnumerateFormLinks().Any(link => link.FormKey == sword.FormKey)))
                throw new InvalidOperationException("This sword is referenced by another record in the plugin. Remove those references first.");
            var nextId = mod.ModHeader.Stats.NextFormID;
            mod.Weapons.Remove(sword.FormKey);
            WeaponProjectService.WriteWithVariantDeletion(projectFilePath, pluginPath, gameDataPath, formId,
                () => WritePlugin(transaction, mod, pluginPath, gameDataPath, replace: true, cancellationToken, nextId), cancellationToken);
            return true;
        }, () => pluginPath, gameDataPath, cancellationToken);
    }

    private static ISkyrimMod ReadWorkspaceForEditing(string pluginPath, string gameDataPath)
    {
        ValidateDataPath(gameDataPath);
        if (!Path.IsPathFullyQualified(pluginPath))
            throw new InvalidOperationException("Select an absolute workspace plugin path.");
        ValidateFileName(Path.GetFileName(pluginPath), allowMaster: false);
        PluginWriteGuard.EnsureOutsideGame(pluginPath, gameDataPath);
        var mod = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(pluginPath).WithUtf8Encoding().Mutable().Construct();
        if (mod.UsingLocalization)
            throw new InvalidOperationException("Editing localized output plugins is not supported in this stage.");
        return mod;
    }

    private static IWeapon FindOwnedSword(ISkyrimMod mod, uint formId)
    {
        var sword = mod.Weapons.FirstOrDefault(w => w.FormKey.ModKey == mod.ModKey && w.FormKey.ID == formId);
        if (sword == null || sword.IsDeleted || !WeaponTypes.Supported(sword.Data?.AnimationType) || sword.BasicStats == null)
            throw new InvalidOperationException("Select an owned supported weapon in this plugin.");
        return sword;
    }

    private static WeaponRecord ToWeaponRecord(IWeaponGetter sword, ISkyrimModGetter? mod = null) => new()
    {
        FormId = sword.FormKey.ID,
        Kind = WeaponTypes.Kind(sword),
        EditorId = sword.EditorID ?? string.Empty,
        Name = sword.Name?.String ?? string.Empty,
        Damage = sword.BasicStats?.Damage ?? 0,
        Weight = sword.BasicStats?.Weight ?? 0,
        Value = sword.BasicStats?.Value ?? 0,
        Details = ToDetails(sword, mod)
    };

    private static ISkyrimModDisposableGetter ReadMaster(string gameDataPath, string fileName)
    {
        var path = Path.Combine(gameDataPath, fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Master not found: {fileName}");
        return SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(path)
            .WithStringsFolder(Path.Combine(gameDataPath, "Strings"))
            .WithBsaFolder(gameDataPath).Construct();
    }

    private static void AddTemplateMasters(ISkyrimMod output, ISkyrimModGetter source, string dataPath, HashSet<ModKey> visiting)
    {
        if (!visiting.Add(source.ModKey)) throw new InvalidOperationException("Cyclic template master dependencies.");
        foreach (var parent in source.ModHeader.MasterReferences)
        {
            if (output.ModHeader.MasterReferences.Any(m => m.Master == parent.Master)) continue;
            ValidateFileName(parent.Master.FileName.String, true);
            using var dependency = ReadMaster(dataPath, parent.Master.FileName.String);
            AddTemplateMasters(output, dependency, dataPath, visiting);
        }
        if (source.ModKey == output.ModKey) throw new InvalidOperationException("A template cannot depend on the output plugin.");
        if (output.ModHeader.MasterReferences.All(m => m.Master != source.ModKey)) output.ModHeader.MasterReferences.Add(new MasterReference { Master = source.ModKey });
        visiting.Remove(source.ModKey);
    }

    private static string CreatePluginPath(CreatePluginRequest request)
    {
        ValidateFileName(request.PluginFileName, allowMaster: false);
        if (string.IsNullOrWhiteSpace(request.OutputDirectory) || !Path.IsPathFullyQualified(request.OutputDirectory))
            throw new InvalidOperationException("Select an absolute workspace output directory.");
        return Path.Combine(request.OutputDirectory, request.PluginFileName);
    }

    private static void ValidateDataPath(string gameDataPath)
    {
        if (string.IsNullOrWhiteSpace(gameDataPath) || !Directory.Exists(gameDataPath))
            throw new InvalidOperationException("Configure an existing game Data directory first.");
    }

    private static void ValidateFileName(string fileName, bool allowMaster)
    {
        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !(extension.Equals(".esp", StringComparison.OrdinalIgnoreCase) || extension.Equals(".esl", StringComparison.OrdinalIgnoreCase) ||
              (allowMaster && extension.Equals(".esm", StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException(allowMaster ? "Select a master filename (ESP, ESM or ESL)." : "Use a plugin filename with an ESP or ESL extension.");
    }

    private void WritePlugin(PluginFileTransaction transaction, ISkyrimMod mod, string pluginPath, string gameDataPath, bool replace, CancellationToken cancellationToken, uint? preserveNextId = null)
    {
        pluginPath = Path.GetFullPath(pluginPath);
        PluginWriteGuard.EnsureOutsideGame(pluginPath, gameDataPath);
        var backupPath = pluginPath + ".bak";
        PluginWriteGuard.EnsureOutsideGame(backupPath, gameDataPath);
        transaction.Write(mod, temporaryPath =>
        {
            var writer = mod.BeginWrite.ToPath(temporaryPath).WithLoadOrderFromHeaderMasters()
                .WithDataFolder(gameDataPath).NoMastersListContentCheck().WithUtf8Encoding();
            if (preserveNextId.HasValue)
            {
                mod.ModHeader.Stats.NextFormID = preserveNextId.Value;
                writer = writer.NoNextFormIDProcessing();
            }
            writer.Write();
        }, replace ? backupPath : null, replace, replaceBackup: true, cancellationToken, _logger);
        _logger?.LogInformation("Plugin saved: {Plugin}; backup: {Backup}", pluginPath, replace ? backupPath : "not needed for a new file");
    }

    private Task<Result<T>> RunWriteAsync<T>(Func<PluginFileTransaction, T> action, Func<string> path, string gameDataPath, CancellationToken token) =>
        RunOperationAsync(async () =>
        {
            ValidateFileName(Path.GetFileName(path()), allowMaster: false);
            using var transaction = await PluginFileTransaction.AcquireAsync(path(), gameDataPath, token).ConfigureAwait(false);
            return await Task.Run(() => action(transaction), token).ConfigureAwait(false);
        }, token);

    private Task<Result<T>> RunAsync<T>(Func<T> action, CancellationToken cancellationToken) =>
        RunOperationAsync(() => Task.Run(action, cancellationToken), cancellationToken);

    private async Task<Result<T>> RunOperationAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            return Result<T>.Success(await action().ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "Plugin builder operation failed");
            return Result<T>.Failure(ex.Message);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
