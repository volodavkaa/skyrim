using System.Windows;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.UI.Views;

namespace SkyrimModPacker.UI.Services;

public sealed record ModelPreviewRequest(string ProjectDirectory, string ThirdPersonPath, string FirstPersonPath, string GameDataPath);
public interface IModelPreviewPresenter { void Show(ModelPreviewRequest request); }
public sealed class ModelPreviewPresenter(IModelPreviewService service) : IModelPreviewPresenter
{
    public void Show(ModelPreviewRequest request)
    {
        var window = new ModelPreviewWindow(service, request) { Owner = System.Windows.Application.Current?.MainWindow };
        window.ShowDialog();
    }
}
