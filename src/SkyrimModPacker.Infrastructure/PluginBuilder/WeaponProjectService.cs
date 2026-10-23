using System.Text.Json;
using Microsoft.Extensions.Logging;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure.Persistence;

namespace SkyrimModPacker.Infrastructure.PluginBuilder;

/// <summary>Creates complete workspaces without replacing existing projects or game files.</summary>
public sealed partial class WeaponProjectService(IPluginBuilderService builder, ILogger<WeaponProjectService>? logger = null) : IWeaponProjectService
{
    public const string ProjectFileName = "mod-project.json";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed class ProjectDocument
    {
        public int Version { get; set; } = 1;
        public string Name { get; set; } = string.Empty;
        public string PluginFileName { get; set; } = string.Empty;
        public List<string> Resources { get; set; } = [];
    }

    public Task<Result<WeaponProject>> CreateAsync(CreatePluginRequest request, CancellationToken cancellationToken = default) =>
        RunAsync(() => BuildProjectAsync(request.PluginFileName, request.OutputDirectory, request.GameDataPath, async (stage, token) =>
        {
            var result = await builder.CreateAsync(new CreatePluginRequest
            {
                PluginFileName = request.PluginFileName, OutputDirectory = stage, GameDataPath = request.GameDataPath,
                Masters = request.Masters.Prepend("Skyrim.esm").Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            }, token);
            if (result.IsFailure) throw new InvalidOperationException(result.Error);
        }, cancellationToken), cancellationToken);

    public Task<Result<WeaponProject>> ImportAsync(string sourcePluginPath, string outputDirectory, string gameDataPath, CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            if (!Path.IsPathFullyQualified(sourcePluginPath)) throw new InvalidOperationException("Select an absolute source plugin path.");
            var pluginName = Path.GetFileName(sourcePluginPath);
            ValidatePluginName(pluginName);
            // Validate the editable subset before creating any output. Skyrim.esm is never imported.
            var weapons = await builder.GetWeaponsAsync(sourcePluginPath, cancellationToken);
            if (weapons.IsFailure) throw new InvalidOperationException(weapons.Error);
            if (weapons.Value.Count == 0) throw new InvalidOperationException("The plugin has no owned one-handed swords supported by this editor.");
            return await BuildProjectAsync(pluginName, outputDirectory, gameDataPath, async (stage, token) =>
            {
                var copyPath = Path.Combine(stage, pluginName);
                await using (var source = File.OpenRead(sourcePluginPath))
                await using (var destination = new FileStream(copyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await source.CopyToAsync(destination, token);
                var copied = await builder.GetWeaponsAsync(copyPath, token);
                if (copied.IsFailure) throw new InvalidOperationException(copied.Error);
                if (copied.Value.Count == 0) throw new InvalidOperationException("The copied plugin has no supported swords.");
            }, cancellationToken);
        }, cancellationToken);

    public Task<Result<WeaponProject>> OpenAsync(string projectFilePath, string gameDataPath, CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            if (!Path.IsPathFullyQualified(projectFilePath)) throw new InvalidOperationException("Select an absolute project path.");
            PluginWriteGuard.EnsureOutsideGame(projectFilePath, gameDataPath);
            await using var stream = File.OpenRead(projectFilePath);
            var document = await JsonSerializer.DeserializeAsync<ProjectDocument>(stream, cancellationToken: cancellationToken);
            if (document == null || document.Version != 1 || string.IsNullOrWhiteSpace(document.Name) || document.Resources == null)
                throw new InvalidOperationException("Invalid or unsupported weapon project.");
            ValidatePluginName(document.PluginFileName);
            var folder = Path.GetDirectoryName(projectFilePath)!;
            foreach (var resource in document.Resources) ResourceTarget(folder, resource, gameDataPath);
            var project = Describe(folder, document.Name, document.PluginFileName, projectFilePath);
            PluginWriteGuard.EnsureOutsideGame(project.PluginPath, gameDataPath);
            var weapons = await builder.GetWeaponsAsync(project.PluginPath, cancellationToken);
            if (weapons.IsFailure) throw new InvalidOperationException(weapons.Error);
            return project;
        }, cancellationToken);

    private static void ValidatePluginName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !(Path.GetExtension(name).Equals(".esp", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(name).Equals(".esl", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A project requires a plain ESP or ESL filename.");
    }

    private static WeaponProject Describe(string folder, string name, string pluginName, string? projectFilePath = null) => new()
    {
        Name = name, ProjectFilePath = projectFilePath ?? Path.Combine(folder, ProjectFileName), PluginPath = Path.Combine(folder, pluginName)
    };

    private static async Task<WeaponProject> BuildProjectAsync(string pluginName, string outputDirectory, string gameDataPath,
        Func<string, CancellationToken, Task> writePlugin, CancellationToken token)
    {
        ValidatePluginName(pluginName);
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Path.IsPathFullyQualified(outputDirectory))
            throw new InvalidOperationException("Select an absolute parent folder for the project.");
        var name = Path.GetFileNameWithoutExtension(pluginName);
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        var folder = Path.GetFullPath(Path.Combine(parent, name));
        var stage = Path.Combine(parent, ".project-" + Guid.NewGuid().ToString("N"));
        PluginWriteGuard.EnsureOutsideGame(folder, gameDataPath);
        PluginWriteGuard.EnsureOutsideGame(stage, gameDataPath);
        if (Directory.Exists(folder) || File.Exists(folder)) throw new IOException("A project with this name already exists. Choose another parent folder or name.");
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(stage);
        try
        {
            await writePlugin(stage, token);
            Directory.CreateDirectory(Path.Combine(stage, "meshes"));
            Directory.CreateDirectory(Path.Combine(stage, "textures"));
            var metadata = new AtomicJsonFile<ProjectDocument>(Path.Combine(stage, ProjectFileName), () => new ProjectDocument(), JsonOptions);
            await metadata.WriteUnderLeaseAsync(new ProjectDocument
            {
                Name = name, PluginFileName = pluginName
            }, token);
            token.ThrowIfCancellationRequested();
            PluginWriteGuard.EnsureOutsideGame(folder, gameDataPath);
            Directory.Move(stage, folder);
            return Describe(folder, name, pluginName);
        }
        finally
        {
            // Only the unique staging directory owned by this operation may be removed.
            var fullStage = Path.GetFullPath(stage);
            if (Path.GetDirectoryName(fullStage) == parent && Path.GetFileName(fullStage).StartsWith(".project-", StringComparison.Ordinal) && Directory.Exists(fullStage))
            {
                PluginWriteGuard.EnsureOutsideGame(fullStage, gameDataPath);
                Directory.Delete(fullStage, recursive: true);
            }
        }
    }

    private async Task<Result<T>> RunAsync<T>(Func<Task<T>> action, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { return Result<T>.Success(await action()); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Weapon project operation failed");
            return Result<T>.Failure(ex.Message);
        }
        finally { _gate.Release(); }
    }
}
