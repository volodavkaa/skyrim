using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NSubstitute;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.UI.Controls;
using SkyrimModPacker.UI.Services;
using SkyrimModPacker.UI.ViewModels;
using SkyrimModPacker.UI.Views;
using Xunit;

namespace SkyrimModPacker.Tests;

public sealed partial class SmartModWindowTests
{
    [Fact]
    public Task UnknownImportTypeRequiresExplicitChoiceAndExcludedModelsAreNotImported() => Sta(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "RecognitionUi", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var models = new[] { "Mystery.nif", "Sword.nif", "Sword_1st.nif", "Sword_scabbard.nif" }.Select(name => { var path = Path.Combine(root, name); File.WriteAllBytes(path, PreviewFixtures.Nif(diffuse: "")); return new WeaponFolderSource(path, name, 100, "SSE", null); }).ToArray();
            var strings = new ResourceDictionary { Source = new Uri("pack://application:,,,/SkyrimModPacker.UI;component/Resources/Strings.uk.xaml") };
            string L(string key) => strings[key] as string ?? key;
            WeaponFolderRequest? captured = null;
            var window = new WeaponFolderWindow(root, Path.Combine(root, "output/mod-project.json"), (_, _) => Task.FromResult(new WeaponFolderScan(root, models, 0)), (request, _) =>
            {
                captured = request; return Task.FromResult(new WeaponFolderPlan(request, "NEW", [new(models[0].SourcePath, "meshes/Mystery.nif", "", 1, false, null)], "Mystery.nif", "Mystery.nif", [], [], 0, false));
            }, [new(WeaponKind.OneHandSword, "Sword"), new(WeaponKind.Dagger, "Dagger")], L, true, WeaponKind.OneHandSword, "output.esp") { ShowActivated = false, ShowInTaskbar = false };
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            // Analysis runs on a worker, so wait for completion without arbitrary sleeps.
            for (var i = 0; i < 100 && window.ItemRows.Count == 0; i++) { await Task.Delay(10); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
            var content = (FrameworkElement)window.Content;
            T Find<T>(string name) where T : FrameworkElement => Descendants<T>(content).Single(e => e.Name == name);
            Assert.Equal(3, window.ItemRows.Count); var unknown = window.ItemRows.Single(r => r.Name == "Mystery"); Assert.True(unknown.NeedsTypeChoice);
            Assert.False(window.ItemRows.Single(r => r.Name.Contains("scabbard")).Included);
            Assert.NotNull(window.ItemRows.Single(r => r.Name == "Sword").FirstModel);
            Find<Button>("ImportPreview").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); Assert.Null(captured);
            Find<ListBox>("ImportItemQueue").SelectedItem = unknown; Find<Button>("ImportConfirmType").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.False(unknown.NeedsTypeChoice);
            Find<Button>("ImportPreview").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.NotNull(captured); Assert.Equal(2, captured.Items.Count); Assert.All(captured.Items, r => Assert.StartsWith("SMP_", r.EditorId));
            window.Close();
        }
        finally { Directory.Delete(root, true); }
    });

    [Fact]
    public Task ComparisonRendersBothLanguagesAndSyncsViewsWithLocalRecipes() => Sta(async () =>
    {
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ui-recognition-comparison")); Directory.CreateDirectory(output);
        foreach (var language in new[] { "uk", "en" })
        {
            var strings = new ResourceDictionary { Source = new Uri($"pack://application:,,,/SkyrimModPacker.UI;component/Resources/Strings.{language}.xaml") };
            var localization = Substitute.For<ILocalizationService>(); localization.CurrentLanguage.Returns(language); localization.GetString(Arg.Any<string>()).Returns(c => strings[c.Arg<string>()] as string ?? c.Arg<string>());
            using var vm = new SmartModViewModel(Substitute.For<IPluginBuilderService>(), Substitute.For<IWeaponProjectService>(), Substitute.For<ISettingsRepository>(), Substitute.For<ISmartModDraftRepository>(), Substitute.For<IPluginValidationService>(), localization: localization);
            vm.Types.Add(new(WeaponKind.OneHandSword, "Sword"));
            var iron = new PluginRecordReference("Skyrim.esm", 1);
            var first = new SmartModItemRow(new() { Weapon = new() { Name = "Мій меч / My sword", EditorId = "MySword", Damage = "14", Weight = "10", ModelPath = "mine.nif", FirstPersonPath = "mine1st.nif" },
                Recipes = [new() { EditorId = "CraftMySword", Ingredients = [new(iron, "10")] }] }, vm.Label);
            var second = new SmartModItemRow(new() { Weapon = first.Fields with { Name = "Зразок / Sample", EditorId = "SampleSword", Damage = "12", ModelPath = "sample.nif", FirstPersonPath = "sample1st.nif" },
                Recipes = [new() { EditorId = "CraftSample", Ingredients = [new(iron, "6")] }] }, vm.Label);
            vm.Items.Add(first); vm.Items.Add(second); vm.SelectedItem = first;
            var service = Substitute.For<IModelPreviewService>(); service.LoadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Scene());
            var window = new WeaponComparisonWindow(vm, service) { ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual }; window.Resources.MergedDictionaries.Add(strings);
            window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var content = (FrameworkElement)window.Content;
            T Find<T>(string name) where T : FrameworkElement => Descendants<T>(content).Single(e => e.Name == name);
            Find<TextBox>("CompareSearch").Text = "SampleSword"; var results = Find<ListBox>("CompareResults"); results.SelectedIndex = 0; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.NotNull(window.Sample); Assert.True(window.MinePreview.HasScene); Assert.True(window.SamplePreview.HasScene);
            window.MinePreview.SetView(new(73, 20, 5)); // Programmatic setting is quiet; a user action is represented by the zoom slider below.
            var zoom = Descendants<Slider>(window.MinePreview).Single(); zoom.Value = 6; Assert.Equal(window.MinePreview.ViewState, window.SamplePreview.ViewState);
            Find<CheckBox>("CompareSameScale").IsChecked = true;
            var tabs = Find<TabControl>("CompareTabs");
            foreach (var width in new[] { 600d, 1240d })
            {
                window.Width = width;
                for (var tab = 0; tab < tabs.Items.Count; tab++)
                {
                    tabs.SelectedIndex = tab; content.Measure(new Size(width - 36, 850)); content.Arrange(new Rect(0, 0, width - 36, 850)); content.UpdateLayout();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); content.UpdateLayout();
                    foreach (var grid in Descendants<DataGrid>(content).Where(g => g.IsVisible)) Assert.All(grid.Columns, c => Assert.True(c.ActualWidth >= 70, c.ActualWidth.ToString()));
                    foreach (var text in Descendants<TextBlock>(content).Where(t => t.IsVisible)) Assert.False(text.Text.StartsWith("Smart_"), text.Text);
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    var drawing = new DrawingVisual(); using (var dc = drawing.RenderOpen()) { dc.DrawRectangle(window.Background, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight)); dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight)); } bitmap.Render(drawing);
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(output, $"{language}-{width}-{tab}.png")); png.Save(file);
                }
            }
            tabs.SelectedIndex = 1; content.UpdateLayout(); var rows = Assert.IsAssignableFrom<IEnumerable<WeaponComparisonValue>>(Find<DataGrid>("CompareStatistics").ItemsSource); Assert.Contains(rows, r => r.Mine == "14" && r.Sample == "12");
            window.Close();
        }
    });
}
