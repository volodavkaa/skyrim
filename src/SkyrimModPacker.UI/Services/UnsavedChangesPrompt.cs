using System.Windows;
using System.Windows.Controls;

namespace SkyrimModPacker.UI.Services;

public enum UnsavedChangesChoice { ContinueEditing, Save, Discard }

public interface IUnsavedChangesPrompt
{
    Task<UnsavedChangesChoice> ShowAsync();
}

/// <summary>Closing the dialog or pressing Escape always keeps the editor open.</summary>
public sealed class UnsavedChangesPrompt(ILocalizationService localization) : IUnsavedChangesPrompt
{
    public Task<UnsavedChangesChoice> ShowAsync()
    {
        var choice = UnsavedChangesChoice.ContinueEditing;
        var dialog = new Window
        {
            Title = localization.GetString("Unsaved_Title"), Width = 580, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = System.Windows.Application.Current?.MainWindow, ShowInTaskbar = false
        };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = localization.GetString("Unsaved_Message"), TextWrapping = TextWrapping.Wrap });
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        foreach (var (key, result) in new[] { ("Unsaved_Save", UnsavedChangesChoice.Save), ("Unsaved_Discard", UnsavedChangesChoice.Discard),
                     ("Unsaved_Continue", UnsavedChangesChoice.ContinueEditing) })
        {
            var button = new Button { Content = localization.GetString(key), Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(4), IsCancel = result == UnsavedChangesChoice.ContinueEditing,
                IsDefault = result == UnsavedChangesChoice.ContinueEditing };
            button.Click += (_, _) => { choice = result; dialog.Close(); };
            buttons.Children.Add(button);
        }
        panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.ShowDialog();
        return Task.FromResult(choice);
    }
}
