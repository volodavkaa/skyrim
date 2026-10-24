using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.UI.ViewModels;
using SkyrimModPacker.UI.Views;

namespace SkyrimModPacker.UI.Services;

public interface ISmartModWindowLauncher { void Show(Window? owner, string? projectPath); }
public sealed class SmartModWindowLauncher(IServiceProvider services) : ISmartModWindowLauncher
{
    public void Show(Window? owner, string? projectPath)
    {
        var window = new SmartModWindow(services.GetRequiredService<SmartModViewModel>(), services.GetRequiredService<IModelPreviewService>(),
            string.IsNullOrWhiteSpace(projectPath) ? null : projectPath) { Owner = owner };
        window.ShowDialog();
    }
}
