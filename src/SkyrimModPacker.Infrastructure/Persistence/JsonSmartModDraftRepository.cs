using System.Text.Json;
using System.Text.Json.Serialization;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.Infrastructure.Persistence;

/// <summary>Revision-checked wizard sidecar. Never writes project metadata, legacy drafts or plugins.</summary>
public sealed class JsonSmartModDraftRepository : ISmartModDraftRepository
{
    public const string FileName = "smart-mod-draft.json";
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() }
    };

    public Task<Result<SmartModDraft?>> LoadAsync(string projectFilePath, string gameDataPath, CancellationToken cancellationToken = default) =>
        LoadCoreAsync(projectFilePath, gameDataPath, false, cancellationToken);

    public Task<Result<SmartModDraft?>> LoadBackupAsync(string projectFilePath, string gameDataPath, CancellationToken cancellationToken = default) =>
        LoadCoreAsync(projectFilePath, gameDataPath, true, cancellationToken);

    public async Task<Result<SmartModDraft>> RestoreBackupAsync(string projectFilePath, string gameDataPath, CancellationToken cancellationToken = default)
    {
        try
        {
            var path = GetPath(projectFilePath, gameDataPath);
            using var lease = await WorkspaceFileGate.AcquireAsync(path, cancellationToken).ConfigureAwait(false);
            var snapshot = new WorkspaceFileSnapshot(path);
            var backup = await ReadAsync(path + ".bak", projectFilePath, gameDataPath, cancellationToken).ConfigureAwait(false)
                ?? throw new IOException("Резервної копії стану майстра немає.");
            if (backup.Revision == long.MaxValue) throw new IOException("Вичерпано діапазон ревізій чернетки.");
            // Preserve even a corrupt primary for inspection; the normal .bak publication may replace it.
            if (File.Exists(path))
            {
                var preserved = path + ".recovery-" + Guid.NewGuid().ToString("N");
                PluginWriteGuard.EnsureOutsideGame(preserved, gameDataPath);
                File.Copy(path, preserved, false);
            }
            var restored = backup with { Id = Guid.NewGuid(), Revision = backup.Revision + 1, SavedAtUtc = DateTimeOffset.UtcNow };
            await new AtomicJsonFile<SmartModDraft>(path, () => new(), Options)
                .WriteUnderLeaseAsync(restored, cancellationToken, snapshot.EnsureUnchanged).ConfigureAwait(false);
            return Result<SmartModDraft>.Success(restored);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<SmartModDraft>.Failure(ex.Message); }
    }

    private static string GetPath(string projectFilePath, string gameDataPath)
    {
        var project = PackingPathGuard.GetAbsolutePath(projectFilePath);
        PluginWriteGuard.EnsureOutsideGame(project, gameDataPath);
        // A fixed sidecar name must never alias the project document itself.
        if (!Path.GetFileName(project).Equals("mod-project.json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Виберіть файл робочого проєкту mod-project.json.");
        var path = Path.Combine(Path.GetDirectoryName(project)!, FileName);
        PluginWriteGuard.EnsureOutsideGame(path, gameDataPath);
        PluginWriteGuard.EnsureOutsideGame(path + ".bak", gameDataPath);
        return path;
    }

    private static void Validate(SmartModDraft draft, string projectFilePath, string gameDataPath)
    {
        if (draft.Version != 1 || draft.Id == Guid.Empty || draft.Revision < 0 || draft.Items == null || !Enum.IsDefined(draft.Section))
            throw new InvalidDataException("Непідтримувана версія або некоректний стан майстра.");
        var project = PackingPathGuard.GetAbsolutePath(projectFilePath);
        if (!project.Equals(PackingPathGuard.GetAbsolutePath(draft.ProjectFilePath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Чернетка належить іншому проєкту.");
        var plugin = PackingPathGuard.GetAbsolutePath(draft.PluginPath);
        PluginWriteGuard.EnsureOutsideGame(plugin, gameDataPath);
        if (!Path.GetDirectoryName(plugin)!.Equals(Path.GetDirectoryName(project), StringComparison.OrdinalIgnoreCase) ||
            !new[] { ".esp", ".esl" }.Contains(Path.GetExtension(plugin), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Плагін чернетки має бути ESP або ESL поруч із файлом проєкту.");
        var ids = new HashSet<Guid>();
        foreach (var item in draft.Items)
        {
            if (item == null || item.Id == Guid.Empty || !ids.Add(item.Id) || !Enum.IsDefined(item.Kind) ||
                !Enum.IsDefined(item.Step) || !Enum.IsDefined(item.State) || item.Weapon == null || item.Recipes == null)
                throw new InvalidDataException("Некоректний або повторний ідентифікатор предмета чернетки.");
            var w = item.Weapon;
            if (item.Tuning is { } tuning)
            {
                if (tuning.Version != 1 || tuning.PresetVersion != 1 || !Enum.IsDefined(tuning.Mode) || !Enum.IsDefined(tuning.Handling) || !Enum.IsDefined(tuning.CraftingIntent) || !Enum.IsDefined(tuning.TemperingIntent) ||
                    tuning.ManualFields == null || tuning.ManualFields.Any(f => !SmartWeaponBalance.Fields.Concat(new[] { "Crafting", "Tempering" }).Contains(f)) ||
                    tuning.SourceFingerprint == null || tuning.ReferenceEditorId == null || tuning.PendingRecipeRemovals == null || tuning.PendingRecipeRemovals.Count > 128 ||
                    tuning.PendingRecipeRemovals.Any(id => id == 0 || id > 0xFFFFFF) || tuning.PendingRecipeRemovals.Distinct().Count() != tuning.PendingRecipeRemovals.Count)
                    throw new InvalidDataException("Invalid wizard material/balance metadata.");
                tuning.Base?.Validate();
            }
            if (item.LastFolderPlanId == Guid.Empty) throw new InvalidDataException("Invalid folder import identity.");
            if (w.Keywords == null || w.Keywords.Any(k => k == null) || new[] { w.EditorId, w.Name, w.Damage, w.Weight, w.Value, w.Description,
                w.Speed, w.Reach, w.ModelPath, w.FirstPersonPath, w.EnchantmentCharge }.Any(s => s == null))
                throw new InvalidDataException("Некоректна структура полів предмета чернетки.");
            foreach (var recipe in item.Recipes)
                if (recipe == null || recipe.Id == Guid.Empty || !ids.Add(recipe.Id) || recipe.EditorId == null || recipe.OutputCount == null ||
                    recipe.Ingredients == null || recipe.Ingredients.Any(i => i == null || i.Count == null))
                    throw new InvalidDataException("Некоректний або повторний ідентифікатор рецепта чернетки.");
        }
        var selected = draft.Items.FirstOrDefault(i => i.Id == draft.SelectedItemId);
        if ((draft.SelectedItemId != null && selected == null) ||
            (draft.SelectedRecipeId != null && (selected == null || !selected.Recipes.Any(r => r.Id == draft.SelectedRecipeId))))
            throw new InvalidDataException("Навігація чернетки посилається на відсутній предмет або рецепт.");
    }

    private static async Task<SmartModDraft?> ReadAsync(string path, string project, string game, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        PackingPathGuard.EnsureNoLinks(path);
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        var draft = await JsonSerializer.DeserializeAsync<SmartModDraft>(stream, Options, token).ConfigureAwait(false)
            ?? throw new JsonException("Файл стану майстра містить null; його не перезаписано.");
        Validate(draft, project, game);
        if (draft.Revision < 1) throw new InvalidDataException("Некоректна ревізія збереженої чернетки.");
        return draft;
    }

    private static async Task<Result<SmartModDraft?>> LoadCoreAsync(string project, string game, bool backup, CancellationToken token)
    {
        try
        {
            var path = GetPath(project, game);
            using var lease = await WorkspaceFileGate.AcquireAsync(path, token).ConfigureAwait(false);
            return Result<SmartModDraft?>.Success(await ReadAsync(backup ? path + ".bak" : path, project, game, token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<SmartModDraft?>.Failure(ex.Message); }
    }

    public async Task<Result<DraftWriteReceipt>> SaveAsync(SmartModDraft draft, string gameDataPath, long expectedRevision, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (expectedRevision < 0 || expectedRevision == long.MaxValue)
                throw new InvalidOperationException("Некоректна очікувана ревізія чернетки.");
            var path = GetPath(draft.ProjectFilePath, gameDataPath);
            Validate(draft, draft.ProjectFilePath, gameDataPath);
            // Require an existing workspace folder; do not create projects implicitly.
            using var lease = await WorkspaceFileGate.AcquireAsync(path, cancellationToken).ConfigureAwait(false);
            var snapshot = new WorkspaceFileSnapshot(path);
            var current = await ReadAsync(path, draft.ProjectFilePath, gameDataPath, cancellationToken).ConfigureAwait(false);
            if ((current?.Revision ?? 0) != expectedRevision ||
                (current != null && (current.Id != draft.Id || !current.PluginPath.Equals(draft.PluginPath, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidOperationException("Стан майстра змінено або видалено. Завантажте його повторно перед збереженням.");
            var updated = draft with
            {
                ProjectFilePath = Path.GetFullPath(draft.ProjectFilePath), PluginPath = Path.GetFullPath(draft.PluginPath),
                Revision = expectedRevision + 1, SavedAtUtc = DateTimeOffset.UtcNow
            };
            await new AtomicJsonFile<SmartModDraft>(path, () => new(), Options)
                .WriteUnderLeaseAsync(updated, cancellationToken, snapshot.EnsureUnchanged).ConfigureAwait(false);
            return Result<DraftWriteReceipt>.Success(new(path, updated.Revision, updated.SavedAtUtc));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Result<DraftWriteReceipt>.Failure(ex.Message); }
    }
}
