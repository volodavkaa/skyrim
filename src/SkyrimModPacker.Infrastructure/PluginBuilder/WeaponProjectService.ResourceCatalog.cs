using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure.Persistence;

namespace SkyrimModPacker.Infrastructure.PluginBuilder;

public sealed partial class WeaponProjectService
{
    public Task<Result<ProjectResourceCatalog>> GetResourceCatalogAsync(string projectFilePath, string gameDataPath, CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            PluginWriteGuard.EnsureOutsideGame(projectFilePath, gameDataPath);
            var metadata = new WorkspaceFileSnapshot(projectFilePath);
            var (folder, document) = await ReadResourceProjectAsync(projectFilePath, gameDataPath, cancellationToken, validateResourcePaths: false);
            var catalog = await Task.Run(() => BuildResourceCatalog(folder, document, gameDataPath, cancellationToken), cancellationToken);
            metadata.EnsureUnchanged();
            return catalog;
        }, cancellationToken);

    private sealed class ResourceCatalogRow(string path)
    {
        public string Path { get; } = path;
        public int Registrations;
        public readonly List<ResourceUsage> Uses = [];
        public readonly List<ResourceIssue> Issues = [];
        public string? Error;
    }

    private static ProjectResourceCatalog BuildResourceCatalog(string folder, ProjectDocument document, string gameDataPath,
        CancellationToken token, bool inspectVanilla = true)
    {
        var pluginPath = Path.Combine(folder, document.PluginFileName);
        var pluginSnapshot = new WorkspaceFileSnapshot(pluginPath);
        using var plugin = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(pluginPath).WithUtf8Encoding().Construct();
        var rows = new Dictionary<string, ResourceCatalogRow>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<ResourceCatalogDiagnostic>();
        var sources = new Dictionary<ModKey, ISkyrimModDisposableGetter>();
        try
        {
            foreach (var path in document.Resources)
            {
                token.ThrowIfCancellationRequested();
                var row = Row(path);
                row.Registrations++;
                if (row.Registrations > 1) AddIssue(row, ResourceIssue.DuplicateRegistration);
            }

            // Resource provenance is established from actual Skyrim.esm model paths, not just a WNAM owner.
            var vanillaPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? vanillaError = null;
            try
            {
                foreach (var sourceName in WeaponTypes.VanillaSources.Where(n => File.Exists(Path.Combine(gameDataPath, n))))
                {
                var master = Source(ModKey.FromNameAndExtension(sourceName));
                foreach (var path in master.Weapons.Where(w => !w.IsDeleted).Select(w => w.Model?.File.GivenPath)
                    .Concat(master.Statics.Where(s => !s.IsDeleted).Select(s => s.Model?.File.GivenPath)))
                {
                    token.ThrowIfCancellationRequested();
                    if (!string.IsNullOrWhiteSpace(path))
                        try { vanillaPaths.Add(MeshPath(path)); }
                        catch (InvalidOperationException) { /* An unrelated malformed vanilla record is not a project assignment. */ }
                }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { vanillaError = ex.Message; }

            var localRecords = plugin.EnumerateMajorRecords().ToDictionary(r => r.FormKey);
            var statCache = new Dictionary<FormKey, IStaticGetter>();
            foreach (var stat in plugin.Statics.Where(s => !s.IsDeleted))
                if (!string.IsNullOrWhiteSpace(stat.Model?.File.GivenPath))
                    Use(stat.Model.File.GivenPath, new ResourceUsage(Reference(stat.FormKey), stat.EditorID ?? "", ResourceUseKind.Static));
            foreach (var weapon in plugin.Weapons.Where(w => !w.IsDeleted))
            {
                token.ThrowIfCancellationRequested();
                var reference = Reference(weapon.FormKey);
                if (!string.IsNullOrWhiteSpace(weapon.Model?.File.GivenPath))
                    Use(weapon.Model.File.GivenPath, new ResourceUsage(reference, weapon.EditorID ?? "", ResourceUseKind.ThirdPerson));
                else diagnostics.Add(new(reference + " / MODL", "The saved WEAP has no model path."));
                if (weapon.FirstPersonModel.FormKey == FormKey.Null)
                {
                    if (weapon.FirstPersonModel.FormKeyNullable.HasValue)
                        diagnostics.Add(new(reference + " / WNAM", "The first-person assignment is explicitly NULL rather than absent."));
                    continue;
                }
                try
                {
                    var stat = ResolveStatic(weapon.FirstPersonModel.FormKey);
                    if (string.IsNullOrWhiteSpace(stat.Model?.File.GivenPath)) throw new InvalidOperationException("The first-person STAT has no model path.");
                    Use(stat.Model.File.GivenPath, new ResourceUsage(reference, weapon.EditorID ?? "", ResourceUseKind.FirstPerson, Reference(stat.FormKey)));
                    // Also show the external STAT which directly refers to this NIF.
                    Use(stat.Model.File.GivenPath, new ResourceUsage(Reference(stat.FormKey), stat.EditorID ?? "", ResourceUseKind.Static));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { diagnostics.Add(new(reference + " / WNAM", ex.Message)); }
            }

            var validRows = rows.Values.Where(r => !r.Issues.Contains(ResourceIssue.InvalidPath)).ToArray();
            foreach (var row in validRows)
            {
                // A registered filename may also be used as a directory by another registered entry.
                var segments = row.Path.Split('/');
                for (var count = 1; count < segments.Length; count++)
                    if (rows.TryGetValue(string.Join('/', segments.Take(count)), out var parent))
                    { AddIssue(parent, ResourceIssue.ArchivePathConflict); AddIssue(row, ResourceIssue.ArchivePathConflict); }
            }

            var entries = new List<ProjectResourceEntry>();
            foreach (var row in rows.Values.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                var mesh = row.Path.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase);
                var origin = row.Registrations > 0 ? ModelOrigin.Project : ModelOrigin.Unknown;
                var availability = ModelAvailability.CheckFailed;
                var detail = row.Error;
                var registered = row.Registrations > 0;
                if (!row.Issues.Contains(ResourceIssue.InvalidPath))
                {
                    try
                    {
                        var target = ResourceTarget(folder, row.Path, gameDataPath);
                        var exists = File.Exists(target);
                        if (registered || exists || Directory.Exists(target))
                        {
                            origin = ModelOrigin.Project;
                            try
                            {
                                using var stream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                                availability = stream.Length > 0 ? ModelAvailability.Found : ModelAvailability.Missing;
                            }
                            catch (FileNotFoundException) { availability = ModelAvailability.Missing; }
                            catch (DirectoryNotFoundException) { availability = ModelAvailability.Missing; }
                            if (availability == ModelAvailability.Missing) AddIssue(row, ResourceIssue.MissingOrEmpty);
                            if (!registered) AddIssue(row, ResourceIssue.UnregisteredCustom);
                            if (vanillaPaths.Contains(row.Path)) AddIssue(row, ResourceIssue.VanillaShadow);
                        }
                        else if (vanillaPaths.Contains(row.Path))
                        {
                            origin = ModelOrigin.Vanilla;
                            if (inspectVanilla)
                            {
                                var info = InspectVanilla(row.Path, gameDataPath, token);
                                availability = info.Availability; detail = info.Detail;
                                if (availability == ModelAvailability.Missing) AddIssue(row, ResourceIssue.MissingOrEmpty);
                                if (availability == ModelAvailability.CheckFailed) AddIssue(row, ResourceIssue.ReadFailed);
                            }
                        }
                        else
                        {
                            availability = vanillaError == null ? ModelAvailability.Missing : ModelAvailability.CheckFailed;
                            detail = vanillaError;
                            AddIssue(row, ResourceIssue.UnregisteredCustom);
                            if (vanillaError != null) AddIssue(row, ResourceIssue.ReadFailed);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        availability = ModelAvailability.CheckFailed; detail = ex.Message;
                        AddIssue(row, ResourceIssue.ReadFailed);
                    }
                }
                var archive = origin == ModelOrigin.Vanilla ? ResourceArchiveStatus.VanillaExcluded :
                    !registered ? ResourceArchiveStatus.NotRegistered : ResourceArchiveStatus.Included;
                if (row.Issues.Any(i => i is ResourceIssue.InvalidPath or ResourceIssue.ArchivePathConflict) ||
                    (origin != ModelOrigin.Vanilla && availability != ModelAvailability.Found)) archive = ResourceArchiveStatus.Blocked;
                entries.Add(new(row.Path, mesh ? FileCategory.Mesh : FileCategory.Texture, origin, availability, registered,
                    archive, row.Uses.Distinct().OrderBy(u => u.Record.PluginName).ThenBy(u => u.Record.FormId).ThenBy(u => u.Kind).ToArray(),
                    row.Issues.ToArray(), detail));
            }
            pluginSnapshot.EnsureUnchanged();
            return new(entries, diagnostics);

            ResourceCatalogRow Row(string path)
            {
                var key = (path ?? string.Empty).Replace('\\', '/');
                if (!rows.TryGetValue(key, out var row)) rows.Add(key, row = new(key));
                try { ValidateResourcePath(key); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { AddIssue(row, ResourceIssue.InvalidPath); row.Error = ex.Message; }
                return row;
            }
            void Use(string model, ResourceUsage use)
            {
                token.ThrowIfCancellationRequested();
                var resource = model.Replace('\\', '/');
                if (!resource.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase)) resource = "meshes/" + resource;
                Row(resource).Uses.Add(use);
            }
            ISkyrimModDisposableGetter Source(ModKey key)
            {
                token.ThrowIfCancellationRequested();
                if (!sources.TryGetValue(key, out var source))
                {
                    var path = PackingPathGuard.ResolveUnderRoot(gameDataPath, key.FileName.String);
                    source = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
                    sources.Add(key, source);
                }
                return source;
            }
            IStaticGetter ResolveStatic(FormKey key)
            {
                if (statCache.TryGetValue(key, out var cached)) return cached;
                if (localRecords.TryGetValue(key, out var local))
                {
                    if (local is not IStaticGetter stat || stat.IsDeleted) throw new InvalidOperationException("WNAM targets a deleted record or a record other than STAT.");
                    return statCache[key] = stat;
                }
                if (!plugin.ModHeader.MasterReferences.Any(m => m.Master == key.ModKey))
                    throw new InvalidOperationException("WNAM refers to an undeclared master or a missing local STAT.");
                foreach (var master in plugin.ModHeader.MasterReferences.Reverse())
                {
                    var source = Source(master.Master);
                    var target = source.Statics.FirstOrDefault(s => s.FormKey == key);
                    if (target == null)
                    {
                        if (source.EnumerateMajorRecords().Any(r => r.FormKey == key))
                            throw new InvalidOperationException("The winning WNAM target is not a STAT.");
                        continue;
                    }
                    if (target.IsDeleted) throw new InvalidOperationException("The winning WNAM target is deleted.");
                    return statCache[key] = target;
                }
                throw new InvalidOperationException("WNAM target is missing from the declared masters.");
            }
        }
        finally { foreach (var source in sources.Values) source.Dispose(); }
    }

    private static string MeshPath(string path)
    {
        path = path.Replace('\\', '/');
        return NormalizeResource(path.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase) ? path : "meshes/" + path);
    }
    private static PluginRecordReference Reference(FormKey key) => new(key.ModKey.FileName.String, key.ID);
    private static void AddIssue(ResourceCatalogRow row, ResourceIssue issue)
    { if (!row.Issues.Contains(issue)) row.Issues.Add(issue); }
}
