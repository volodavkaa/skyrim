using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.UI.Services;
using SkyrimModPacker.UI.Views;
using Xunit;

namespace SkyrimModPacker.Tests;

[Collection("WPF page")]
public class PreparedModelRecoveryWindowTests
{
    [Fact]
    public async Task OccupiedDestinationAndImportFailureCanBeCorrectedWithoutLosingModels()
    {
        var root = Path.Combine(Path.GetTempPath(), "prepared-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Taken"));
        try
        {
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                dispatcher.BeginInvoke(async () =>
                {
                    PreparedModelImportWindow? window = null;
                    try
                    {
                        var handoff = new PreparedModelHandoff(root, Path.Combine(root, "third.nif"), Path.Combine(root, "first.nif"), "Sword", WeaponKind.OneHandSword, null);
                        var request = handoff.Request(Path.Combine(root, "Taken", "mod-project.json"), "Taken.esp", true);
                        WeaponFolderRequest? last = null;
                        window = new PreparedModelImportWindow(handoff, request, (value, _) =>
                        {
                            last = value;
                            if (Directory.Exists(Path.GetDirectoryName(value.ProjectPath)!)) throw new IOException("occupied");
                            return Task.FromResult(new WeaponFolderPlan(value, "NEW", [new(value.ThirdPersonSource, "meshes/third.nif", "hash", 1, false, null)], "third.nif", "first.nif", [], [], 0, false));
                        }, _ => Task.FromResult((false, "simulated import error")), key => key);
                        T Field<T>(string name) => (T)typeof(PreparedModelImportWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                        window.Show(); await Task.Delay(100);
                        Assert.Contains("occupied", Field<TextBox>("_details").Text);
                        Field<TextBox>("_name").Text = "Taken_2";
                        Field<Button>("_refresh").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(100);
                        Assert.True(Field<Button>("_import").IsEnabled);
                        Assert.Equal(request.Id, last!.Id); Assert.Equal(request.ThirdPersonSource, last.ThirdPersonSource); Assert.Equal(request.FirstPersonSource, last.FirstPersonSource);
                        Assert.Equal("Taken_2.esp", last.NewProjectPluginName);
                        Field<Button>("_import").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(100);
                        Assert.True(window.IsVisible); Assert.Contains("simulated import error", Field<TextBox>("_details").Text);
                        Assert.Equal("Taken_2", Field<TextBox>("_name").Text);
                        Assert.False(Field<Button>("_import").IsEnabled);
                        Field<Button>("_refresh").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(100);
                        Assert.True(Field<Button>("_import").IsEnabled);
                        completed.SetResult();
                    }
                    catch (Exception error) { completed.SetException(error); }
                    finally { window?.Close(); dispatcher.InvokeShutdown(); }
                });
                Dispatcher.Run();
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally { Directory.Delete(root, true); }
    }
}
