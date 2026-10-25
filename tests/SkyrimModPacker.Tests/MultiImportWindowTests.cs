using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.UI.ViewModels;
using SkyrimModPacker.UI.Views;
using Xunit;
namespace SkyrimModPacker.Tests;
public sealed partial class SmartModWindowTests
{
    [Fact]
    public Task ImportQueueAndDestinationCanBeEditedAndRecoveredInBothLanguages() => Sta(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "MultiImportUi", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(root, "Taken")); File.WriteAllText(Path.Combine(root, "Taken", "keep.txt"), "untouched");
        var source = Path.Combine(root, "assets"); Directory.CreateDirectory(source); var sword = Path.Combine(source, "Sword.nif"); var greatsword = Path.Combine(source, "Greatsword.nif"); File.WriteAllBytes(sword, PreviewFixtures.Nif(diffuse: "")); File.WriteAllBytes(greatsword, PreviewFixtures.Nif(diffuse: ""));
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ui-multi-import")); Directory.CreateDirectory(output);
        try
        {
            foreach (var language in new[] { "uk", "en" })
            {
                var strings = new ResourceDictionary { Source = new Uri($"pack://application:,,,/SkyrimModPacker.UI;component/Resources/Strings.{language}.xaml") };
                string Label(string key) => strings[key] as string ?? key;
                var inventory = new WeaponFolderScan(source, [new(sword, "Sword.nif", 100, "SSE", null), new(greatsword, "Greatsword.nif", 100, "SSE", null)], 0);
                var window = new WeaponFolderWindow(source, Path.Combine(root, "Taken", "mod-project.json"), (_, _) => Task.FromResult(inventory), (request, _) =>
                {
                    if (Directory.Exists(Path.GetDirectoryName(request.ProjectPath))) throw new IOException(Label("Smart_FolderDestinationExists"));
                    return Task.FromResult(new WeaponFolderPlan(request, "NEW", [new(sword, "meshes/Sword.nif", "hash", 100, false, null), new(greatsword, "meshes/Greatsword.nif", "hash", 100, false, null)], "Sword.nif", "Sword.nif", [], [], 0, false)
                    { Items = request.Items.Select(i => new WeaponFolderItemPlan(i.Id, i.Name, i.Kind, Path.GetFileName(i.ThirdPersonSource), Path.GetFileName(i.ThirdPersonSource))).ToArray() });
                }, [new(WeaponKind.OneHandSword, Label("WeaponType_OneHandSword")), new(WeaponKind.Greatsword, Label("WeaponType_Greatsword"))], Label, true, WeaponKind.OneHandSword, "Taken.esp", "DarkSister_HD.zip")
                { ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual };
                window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var content = (FrameworkElement)window.Content;
                T Find<T>(string name) where T : FrameworkElement => Descendants<T>(content).Single(e => e.Name == name);
                var name = Find<TextBox>("ImportProjectName"); Assert.NotEqual("Taken", name.Text); Assert.Contains("DarkSister_HD", name.Text);
                Assert.Equal(2, window.ItemRows.Count); var first = window.ItemRows[0]; first.Name = "Dark Sister 1H"; first.ThirdModel = window.Models.Single(m => m.Source.SourcePath == sword);
                var second = window.ItemRows[1]; second.Name = "Dark Sister 2H"; Find<ListBox>("ImportItemQueue").SelectedItem = second; Find<ComboBox>("ImportItemKind").SelectedValue = WeaponKind.Greatsword; second.ThirdModel = window.Models.Single(m => m.Source.SourcePath == greatsword);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Assert.Equal(WeaponKind.Greatsword, second.Kind);
                Find<Button>("ImportPreview").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.NotNull(window.Plan); Assert.True(window.Plan.CanImport); Assert.Equal(2, window.Plan.Items.Count); Assert.True(Find<Button>("ImportConfirm").IsEnabled);
                name.Text = "Taken"; Assert.Null(window.Plan); Find<Button>("ImportPreview").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.False(Find<Button>("ImportConfirm").IsEnabled); Assert.Equal(Label("Smart_FolderPlanFailed"), Find<TextBlock>("ImportProgress").Text);
                Find<Button>("ImportSuggestName").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal("Taken_2", name.Text);
                Find<Button>("ImportPreview").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Assert.True(window.Plan!.CanImport); Assert.Contains("Taken_2", window.Plan.Request.ProjectPath);
                foreach (var width in new[] { 600d, 1040d })
                {
                    window.Width = width; content.Measure(new Size(width - 32, 820)); content.Arrange(new Rect(0, 0, width - 32, 820)); content.UpdateLayout();
                    foreach (var input in Descendants<TextBox>(content).Where(t => t.IsVisible)) Assert.True(Contrast(((SolidColorBrush)input.Foreground).Color, ((SolidColorBrush)input.Background).Color) >= 4.5);
                    var panel = (FrameworkElement)Find<TextBlock>("ImportProgress").Parent;
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth), (int)Math.Ceiling(panel.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    var drawing = new DrawingVisual(); using (var dc = drawing.RenderOpen()) { dc.DrawRectangle(window.Background, null, new Rect(0, 0, panel.ActualWidth, panel.ActualHeight)); dc.DrawRectangle(new VisualBrush(panel), null, new Rect(0, 0, panel.ActualWidth, panel.ActualHeight)); }
                    bitmap.Render(drawing); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(output, $"{language}-{width}-queue.png")); png.Save(file);
                }
                Find<Button>("ImportCopyItem").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(3, window.ItemRows.Count); Assert.NotEqual(second.Id, window.ItemRows[2].Id);
                Find<Button>("ImportRemoveItem").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal(2, window.ItemRows.Count); Assert.Null(window.Plan);
                window.Close(); Assert.Equal("untouched", File.ReadAllText(Path.Combine(root, "Taken", "keep.txt")));
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    });
}
