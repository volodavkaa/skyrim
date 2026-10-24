using System.IO;
using CommunityToolkit.Mvvm.Input;
using SkyrimModPacker.UI.Services;

namespace SkyrimModPacker.UI.ViewModels;

public sealed partial class WeaponsViewModel
{
    private readonly IModelPreviewPresenter? _previewPresenter;
    [RelayCommand]
    private async Task PreviewModelAsync()
    {
        if (IsEditorBusy) return;
        if (_previewPresenter == null) { ErrorMessage = Text("Preview_Unavailable", "3D preview service is unavailable."); return; }
        ModelPreviewRequest? request = null;
        await RunOperationAsync(async token =>
        {
            await LoadFirstPersonPathAsync(token);
            request = new(string.IsNullOrEmpty(ProjectFilePath) ? "" : Path.GetDirectoryName(ProjectFilePath)!, ModelPath, FirstPersonModelPath, GameDataPath);
        });
        if (request != null && ErrorMessage == null)
        {
            try { _previewPresenter.Show(request); }
            catch (Exception ex) { ErrorMessage = Text("Preview_Failed", "Preview unavailable:") + " " + ex.Message; }
        }
    }
}
