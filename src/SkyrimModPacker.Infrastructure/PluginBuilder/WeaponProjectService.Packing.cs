using SkyrimModPacker.Core.Common;
using Microsoft.Extensions.Logging;
using SkyrimModPacker.Core.Models;

namespace SkyrimModPacker.Infrastructure.PluginBuilder;

public sealed partial class WeaponProjectService
{
    public Task<Result<List<ScannedFile>>> GetPackingFilesAsync(string projectFilePath, string gameDataPath, CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var (folder, document) = await ReadResourceProjectAsync(projectFilePath, gameDataPath, cancellationToken);
            var pluginPath = Path.Combine(folder, document.PluginFileName);
            var weapons = await builder.GetWeaponsAsync(pluginPath, cancellationToken);
            if (weapons.IsFailure) throw new InvalidOperationException(weapons.Error);
            if (weapons.Value.Count == 0) throw new InvalidOperationException("Save at least one sword to the project plugin before packing.");

            // Rebuild from disk rather than trusting an earlier UI preview or its selected rows.
            var catalog = await Task.Run(() => BuildResourceCatalog(folder, document, gameDataPath, cancellationToken, inspectVanilla: false), cancellationToken);
            if (!catalog.CanPack)
                throw new InvalidOperationException("Project resources block packing: " + string.Join("; ",
                    catalog.Entries.Where(e => e.BlocksPacking).Select(e => e.RelativePath + " (" + string.Join(", ", e.Issues) + ")")
                        .Concat(catalog.Diagnostics.Select(d => d.Record + ": " + d.Detail))));

            var resources = document.Resources.Select(NormalizeResource).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var files = new List<ScannedFile> { DescribeFile(pluginPath, document.PluginFileName, FileCategory.Plugin) };
            foreach (var relative in resources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = ResourceTarget(folder, relative, gameDataPath);
                if (!File.Exists(target) || new FileInfo(target).Length == 0)
                    throw new FileNotFoundException("Project resource is missing or empty: " + relative);
                files.Add(DescribeFile(target, relative, relative.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase) ? FileCategory.Mesh : FileCategory.Texture));
            }

            // Unregistered custom models must not silently disappear from the archive.
            HashSet<string>? vanillaModels = null;
            foreach (var sword in weapons.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var models = await builder.GetWeaponModelsAsync(pluginPath, gameDataPath, sword.FormId, cancellationToken);
                if (models.IsFailure) throw new InvalidOperationException(models.Error);
                if (models.Value.Error != null) throw new InvalidOperationException(models.Value.Error);
                if (models.Value.FirstPersonReference != null)
                {
                    var first = models.Value.FirstPersonPath ?? throw new InvalidOperationException("First-person STAT has no NIF model.");
                    var firstResource = NormalizeResource("meshes/" + first.Replace('\\', '/'));
                    if (!resources.Contains(firstResource, StringComparer.OrdinalIgnoreCase))
                    {
                        if (File.Exists(ResourceTarget(folder, firstResource, gameDataPath)) ||
                            !models.Value.FirstPersonReference.PluginName.Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Import and register the first-person NIF before packing: " + first);
                        // A Skyrim-owned FormKey alone does not establish a vanilla model: an override may change it.
                        using var originalMaster = Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinaryOverlay(
                            Path.Combine(gameDataPath, "Skyrim.esm"), Mutagen.Bethesda.Skyrim.SkyrimRelease.SkyrimSE);
                        var originalStatic = originalMaster.Statics.FirstOrDefault(s => s.FormKey.ID == models.Value.FirstPersonReference.FormId && !s.IsDeleted);
                        if (!string.Equals(originalStatic?.Model?.File.GivenPath.Replace('\\', '/'), first.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Unregistered custom first-person model: " + first);
                    }
                }
                var model = sword.Details.ModelPath.Replace('\\', '/');
                if (model.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase)) model = model[7..];
                ValidateResourcePath("meshes/" + model);
                if (resources.Contains("meshes/" + model, StringComparer.OrdinalIgnoreCase)) continue;
                // A loose project file takes priority over inherited game resources and must be registered.
                if (File.Exists(ResourceTarget(folder, "meshes/" + model, gameDataPath)))
                    throw new InvalidOperationException("Register the project's model using Import NIF before packing: " + model);
                if (vanillaModels == null)
                {
                    var templates = await builder.GetSwordTemplatesAsync(gameDataPath, cancellationToken);
                    if (templates.IsFailure) throw new InvalidOperationException(templates.Error);
                    vanillaModels = templates.Value.Select(t => t.Details.ModelPath.Replace('\\', '/'))
                        .Select(p => p.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase) ? p[7..] : p).ToHashSet(StringComparer.OrdinalIgnoreCase);
                }
                if (!vanillaModels.Contains(model))
                    throw new InvalidOperationException("Import the custom model referenced by '" + sword.EditorId + "' before packing: " + model);
            }
            logger?.LogInformation("Project packing includes plugin {Plugin} and {ResourceCount} registered resources", document.PluginFileName, resources.Count);
            return files;
        }, cancellationToken);

    private static ScannedFile DescribeFile(string absolute, string relative, FileCategory category) => new()
    {
        AbsolutePath = absolute, RelativePath = relative.Replace('/', '\\'), SizeBytes = new FileInfo(absolute).Length,
        Category = category, IsSelected = true
    };
}
