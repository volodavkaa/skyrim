using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using NSubstitute;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.UI.ViewModels;
using SkyrimModPacker.UI.Views;
using Xunit;

namespace SkyrimModPacker.Tests;

[Collection("WPF page")]
public class ItemOverviewWindowTests
{
    [Fact]
    public async Task MissingModelAndUnsavedListsAreShownAsUncheckedAndNavigationWorks()
    {
        var root = Path.Combine(Path.GetTempPath(), "item-overview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "meshes")); Directory.CreateDirectory(Path.Combine(root, "textures"));
        File.WriteAllBytes(Path.Combine(root, "meshes", "model.nif"), PreviewFixtures.Nif("hiddenUnknown", "textures/test.dds", ["textures/missing.dds"]));
        File.WriteAllBytes(Path.Combine(root, "textures", "test.dds"), PreviewFixtures.Bc7());
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                ItemOverviewWindow? window = null; SmartModViewModel? vm = null;
                try
                {
                    vm = new SmartModViewModel(Substitute.For<IPluginBuilderService>(), Substitute.For<IWeaponProjectService>(), Substitute.For<ISettingsRepository>(), Substitute.For<ISmartModDraftRepository>(), Substitute.For<IPluginValidationService>());
                    var item = new SmartModItemRow(new SmartModItemDraft(), key => key); vm.SelectedItem = item;
                    window = new ItemOverviewWindow(vm, item); window.Show();
                    for (var i = 0; i < 30 && Field<TextBlock>(window, "_status").Text != "Smart_ItemOverviewSnapshot"; i++) await Task.Delay(50);
                    Assert.Contains(Field<StackPanel>(window, "_textures").Children.OfType<TextBlock>(), x => x.Text.Contains("Smart_ItemOverviewModelUnknown"));
                    Assert.Contains(Field<StackPanel>(window, "_lists").Children.OfType<TextBlock>(), x => x.Text == "Smart_ItemOverviewUnsaved");
                    vm.GoToStepCommand.Execute(SmartModItemStep.Appearance);
                    Assert.Equal(SmartModItemStep.Appearance, item.Step);
                    window.Close();
                    typeof(SmartModViewModel).GetField("_document", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, new SmartModDraft { ProjectFilePath = Path.Combine(root, "mod-project.json") });
                    typeof(SmartModViewModel).GetField("_gameData", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, root);
                    var modelItem = new SmartModItemRow(new SmartModItemDraft { Weapon = new() { ModelPath = "model.nif" } }, key => key);
                    vm.SelectedItem = modelItem;
                    window = new ItemOverviewWindow(vm, modelItem); window.Show();
                    for (var i = 0; i < 100 && Field<TextBlock>(window, "_status").Text != "Smart_ItemOverviewSnapshot"; i++) await Task.Delay(50);
                    var lines = Field<StackPanel>(window, "_textures").Children.OfType<TextBlock>().Select(x => x.Text).ToArray();
                    Assert.Contains(lines, x => x.Contains("Smart_ItemOverviewFound") && x.Contains("test.dds"));
                    Assert.Contains(lines, x => x.Contains("Smart_ItemOverviewMissing") && x.Contains("missing.dds"));
                    Assert.Contains(lines, x => x.Contains("Smart_TextureUnknown"));
                    completed.SetResult();
                }
                catch (Exception error) { completed.SetException(error); }
                finally { window?.Close(); vm?.Dispose(); dispatcher.InvokeShutdown(); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try { await completed.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { Directory.Delete(root, true); }
    }
    private static T Field<T>(object window, string name) => (T)window.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
}
