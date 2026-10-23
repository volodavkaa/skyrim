using System.Windows;

namespace SkyrimModPacker.UI.Services;

public interface IWorkspaceConflictPrompt
{
    Task<bool> ConfirmAsync(string details);
}

public sealed class WorkspaceConflictPrompt(ILocalizationService localization) : IWorkspaceConflictPrompt
{
    public Task<bool> ConfirmAsync(string details) => Task.FromResult(MessageBox.Show(System.Windows.Application.Current.MainWindow,
        localization.GetString("Recovery_ConflictConfirm") + "\n\n" + details, localization.GetString("Recovery_Title"),
        MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
}
