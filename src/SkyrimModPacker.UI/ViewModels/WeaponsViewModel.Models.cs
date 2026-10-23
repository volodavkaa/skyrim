using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.UI.ViewModels;

public sealed partial class WeaponsViewModel
{
    [ObservableProperty] private string _firstPersonModelPath = string.Empty;
    [ObservableProperty] private string? _thirdPersonModelStatus;
    [ObservableProperty] private string? _firstPersonModelStatus;
    [ObservableProperty] private SwordTemplate? _restorationTemplate;
    private uint? _restorationOrigin;
    private string _restorationPluginName = "Skyrim.esm";
    private PluginRecordReference? _firstPersonReference;
    private bool _firstPersonEdited, _restoreFirstPerson, _fillingModel;

    partial void OnFirstPersonModelPathChanged(string value)
    {
        FirstPersonModelStatus = null;
        if (_fillingModel) return;
        _firstPersonEdited = _savedWeaponDraft == null || value != _savedWeaponDraft.FirstPath;
        _restoreFirstPerson = false;
        if (!_firstPersonEdited) _firstPersonReference = _savedWeaponDraft?.FirstReference;
        IsWizardSaved = false;
    }

    private void ResetFirstPersonDraft(WeaponDetails details)
    {
        _fillingModel = true;
        FirstPersonModelPath = details.FirstPersonModelPath ?? "";
        _fillingModel = false;
        _firstPersonReference = details.FirstPersonModel;
        _restorationOrigin = null;
        _restorationPluginName = "Skyrim.esm";
        _firstPersonEdited = _restoreFirstPerson = false;
        FirstPersonModelStatus = ThirdPersonModelStatus = null;
    }

    private async Task LoadFirstPersonPathAsync(CancellationToken token)
    {
        if (_firstPersonEdited || _restoreFirstPerson) return;
        var form = SelectedWeapon?.FormId ?? SelectedTemplate?.FormId;
        if (form == null) return;
        var plugin = SelectedWeapon == null ? Path.Combine(GameDataPath, SelectedTemplate?.PluginName ?? "Skyrim.esm") : PluginPath;
        var result = await _builder.GetWeaponModelsAsync(plugin, GameDataPath, form.Value, token);
        if (result.IsFailure) throw new InvalidOperationException(result.Error);
        if (result.Value.Error != null) throw new InvalidOperationException(result.Value.Error);
        _firstPersonReference = result.Value.FirstPersonReference;
        _fillingModel = true;
        FirstPersonModelPath = result.Value.FirstPersonPath ?? "";
        _fillingModel = false;
        if (_savedWeaponDraft != null) _savedWeaponDraft = _savedWeaponDraft with { FirstPath = FirstPersonModelPath, FirstReference = _firstPersonReference };
        UpdateUnsavedChanges();
    }

    [RelayCommand]
    private async Task ImportFirstPersonModelAsync(string? path)
    {
        if (IsLoading) return;
        if (string.IsNullOrWhiteSpace(path))
        {
            var dialog = new OpenFileDialog { Title = Text("Models_First", "First-person model"), Filter = "NIF (*.nif)|*.nif" };
            if (dialog.ShowDialog() != true) return;
            path = dialog.FileName;
        }
        var source = path;
        await RunOperationAsync(async token =>
        {
            RequireProject();
            var result = await _projects.ImportModelAsync(ProjectFilePath, source, GameDataPath, token);
            if (result.IsFailure) throw new InvalidOperationException(result.Error);
            FirstPersonModelPath = result.Value;
            // Importing an identical already assigned path is still an explicit assignment.
            _firstPersonEdited = true;
            _restoreFirstPerson = false;
            IsWizardSaved = false;
            UpdateUnsavedChanges();
            await RefreshProjectResourcesAsync(token);
            await InspectDraftModelsAsync(token);
        }, write: true, resourceWrite: true);
    }

    private async Task<WeaponModels> OriginalModelsAsync(CancellationToken token)
    {
        uint? template = null;
        if (SelectedWeapon != null && !string.IsNullOrWhiteSpace(ProjectFilePath))
        {
            var origin = await _projects.GetTemplateOriginAsync(ProjectFilePath, GameDataPath, SelectedWeapon.FormId, token);
            if (origin.IsFailure) throw new InvalidOperationException(origin.Error);
            template = origin.Value;
            if (template != null)
            {
                var baseline = await _projects.GetOriginalModelsAsync(ProjectFilePath, GameDataPath, SelectedWeapon.FormId, token);
                if (baseline.IsFailure) throw new InvalidOperationException(baseline.Error);
                if (baseline.Value != null)
                {
                    var reference = await _projects.GetTemplateReferenceAsync(ProjectFilePath, GameDataPath, SelectedWeapon.FormId, token);
                    if (reference?.IsFailure == true) throw new InvalidOperationException(reference.Error);
                    _restorationPluginName = reference?.Value?.PluginName ?? "Skyrim.esm";
                    _restorationOrigin = template; return baseline.Value;
                }
            }
        }
        // For legacy/imported projects the user explicitly chooses a template in the existing list.
        template ??= SelectedWeapon == null ? SelectedTemplate?.FormId : RestorationTemplate?.FormId;
        if (template == null) throw new InvalidOperationException(Text("Models_SelectOriginal", "Select a vanilla template to restore its model."));
        var source = (SelectedWeapon == null ? SelectedTemplate : RestorationTemplate)?.PluginName ?? "Skyrim.esm";
        var result = await _builder.GetWeaponModelsAsync(Path.Combine(GameDataPath, source), GameDataPath, template.Value, token);
        if (result.IsFailure) throw new InvalidOperationException(result.Error);
        if (result.Value.Error != null) throw new InvalidOperationException(result.Value.Error);
        _restorationOrigin = template;
        _restorationPluginName = source;
        return result.Value;
    }

    [RelayCommand]
    private Task RestoreThirdPersonModelAsync() => RunOperationAsync(async token =>
    {
        ModelPath = (await OriginalModelsAsync(token)).ThirdPersonPath;
        UpdateUnsavedChanges();
        await InspectDraftModelsAsync(token);
    });

    [RelayCommand]
    private Task RestoreFirstPersonModelAsync() => RunOperationAsync(async token =>
    {
        var original = await OriginalModelsAsync(token);
        _firstPersonReference = original.FirstPersonReference;
        _fillingModel = true;
        FirstPersonModelPath = original.FirstPersonPath ?? "";
        _fillingModel = false;
        _firstPersonEdited = false;
        _restoreFirstPerson = true;
        IsWizardSaved = false;
        UpdateUnsavedChanges();
        await InspectDraftModelsAsync(token);
    });

    [RelayCommand]
    private Task CheckModelsAsync() => RunOperationAsync(InspectDraftModelsAsync);

    private async Task InspectDraftModelsAsync(CancellationToken token)
    {
        await LoadFirstPersonPathAsync(token);
        var thirdVanilla = Templates.Any(t => string.Equals(t.Details.ModelPath.Replace('\\', '/'), ModelPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
        ThirdPersonModelStatus = await DescribeModelAsync(ModelPath, thirdVanilla, token);
        FirstPersonModelStatus = string.IsNullOrWhiteSpace(FirstPersonModelPath)
            ? Text("Models_NoFirst", "No separate first-person model")
            : await DescribeModelAsync(FirstPersonModelPath, !_firstPersonEdited &&
                (_firstPersonReference?.PluginName.Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase) == true || _firstPersonReference?.PluginName.Equals("Dawnguard.esm", StringComparison.OrdinalIgnoreCase) == true), token);
    }

    private async Task<string> DescribeModelAsync(string model, bool vanilla, CancellationToken token)
    {
        var result = await _projects.InspectModelAsync(ProjectFilePath, model, GameDataPath, vanilla, token);
        if (result.IsFailure) return result.Error;
        var info = result.Value;
        var origin = Text("Models_Origin_" + info.Origin, info.Origin.ToString());
        var availability = Text("Models_Availability_" + info.Availability, info.Availability.ToString());
        return $"{origin}; {availability}; ZIP: {Text(info.IncludedInZip ? "Models_Yes" : "Models_No", info.IncludedInZip ? "Yes" : "No")}" +
            (info.Detail == null ? "" : "\n" + info.Detail);
    }

    private async Task EnsureFirstPersonAvailableAsync(CancellationToken token)
    {
        if (!_firstPersonEdited) return;
        RequireProject();
        var result = await _projects.ValidateModelAsync(ProjectFilePath, FirstPersonModelPath, GameDataPath, token);
        if (result.IsFailure) throw new InvalidOperationException(result.Error);
        var resources = await _projects.GetResourcesAsync(ProjectFilePath, GameDataPath, token);
        if (resources.IsFailure) throw new InvalidOperationException(resources.Error);
        var relative = FirstPersonModelPath.Replace('\\', '/');
        if (!relative.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase)) relative = "meshes/" + relative;
        if (!resources.Value.Contains(relative, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException(Text("Models_Register", "Import and register the first-person NIF before saving."));
    }
}
