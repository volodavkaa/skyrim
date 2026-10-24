using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NSubstitute;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure.Preview;
using SkyrimModPacker.UI.Controls;
using SkyrimModPacker.UI.Services;
using SkyrimModPacker.UI.ViewModels;
using SkyrimModPacker.UI.Views;
using Xunit;

namespace SkyrimModPacker.Tests;

[Collection("WPF page")]
public sealed class SmartModWindowTests
{
    private static async Task Sta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
                finally { dispatcher.InvokeShutdown(); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(90));
    }
    private static ModelPreview Scene() => SseNifReader.Read(PreviewFixtures.Nif()) with
        { Textures = [DdsPreviewDecoder.Decode("textures/test.dds", PreviewFixtures.Dds())] };

    [Fact]
    public Task ThumbnailCacheUsesActualSceneContent_AndFailureIsNonBlocking() => Sta(async () =>
    {
        var preview = Substitute.For<IModelPreviewService>();
        preview.LoadAsync("", "model.nif", "", Arg.Any<CancellationToken>()).Returns(Scene());
        var thumbnails = new ModelThumbnailService(preview);
        var first = await thumbnails.CreateAsync("", "model.nif", "");
        var again = await thumbnails.CreateAsync("", "model.nif", "");
        Assert.Null(first.Error); Assert.NotNull(first.Image); Assert.Same(first.Image, again.Image); Assert.True(first.Image.IsFrozen);
        var scene = Scene(); var pixels = (byte[])scene.Textures[0].BgraPixels.Clone(); pixels[0] ^= 255;
        preview.LoadAsync("", "model.nif", "", Arg.Any<CancellationToken>()).Returns(scene with { Textures = [scene.Textures[0] with { BgraPixels = pixels }] });
        var changed = await thumbnails.CreateAsync("", "model.nif", ""); Assert.NotSame(first.Image, changed.Image);
        preview.LoadAsync("", "bad.nif", "", Arg.Any<CancellationToken>()).Returns(Task.FromException<ModelPreview>(new NotSupportedException("LE")));
        var failed = await thumbnails.CreateAsync("", "bad.nif", ""); Assert.Null(failed.Image); Assert.Contains("LE", failed.Error);
        var bitmap = (BitmapSource)first.Image;
        var bytes = new byte[128 * 128 * 4]; bitmap.CopyPixels(bytes, 128 * 4, 0);
        Assert.Contains(Enumerable.Range(0, bytes.Length / 4), i => bytes[i * 4] != 42 || bytes[i * 4 + 1] != 35 || bytes[i * 4 + 2] != 32);
    });

    [Fact]
    public Task EmbeddedPreviewRejectsStaleCompletionsAndClearsFailedScene() => Sta(async () =>
    {
        var service = Substitute.For<IModelPreviewService>();
        var pending = new TaskCompletionSource<ModelPreview>();
        service.LoadAsync("", "old.nif", "", Arg.Any<CancellationToken>()).Returns(pending.Task);
        service.LoadAsync("", "new.nif", "", Arg.Any<CancellationToken>()).Returns(Scene());
        service.LoadAsync("", "bad.nif", "", Arg.Any<CancellationToken>()).Returns(Task.FromException<ModelPreview>(new IOException("broken")));
        using var control = new ModelPreviewControl(service);
        var old = control.LoadAsync(new("", "old.nif", "", ""));
        await control.LoadAsync(new("", "new.nif", "", "")); Assert.True(control.HasScene);
        pending.SetResult(Scene()); await old; Assert.True(control.HasScene);
        await control.LoadAsync(new("", "bad.nif", "", "")); Assert.False(control.HasScene); Assert.Contains("broken", control.Status);
        await control.LoadAsync(null); Assert.False(control.HasScene);
    });

    [Fact]
    public Task WizardRendersBothLanguagesAllStepsAndNarrowLayout() => Sta(async () =>
    {
        var builder = Substitute.For<IPluginBuilderService>(); var settings = Substitute.For<ISettingsRepository>();
        var projects = Substitute.For<IWeaponProjectService>(); var drafts = Substitute.For<ISmartModDraftRepository>();
        var validator = Substitute.For<IPluginValidationService>(); var preview = Substitute.For<IModelPreviewService>();
        var languageCode = "uk";
        var strings = new ResourceDictionary { Source = new Uri("pack://application:,,,/SkyrimModPacker.UI;component/Resources/Strings.uk.xaml") };
        var localization = Substitute.For<ILocalizationService>();
        localization.CurrentLanguage.Returns(_ => languageCode);
        localization.GetString(Arg.Any<string>()).Returns(c => strings[c.Arg<string>()] as string ?? c.Arg<string>());
        var source = new SwordTemplate { FormId = 0x12EB7, Name = "Iron sword", EditorId = "IronSword", Damage = 7, Weight = 9, Value = 25,
            Details = new() { ModelPath = "third.nif", FirstPersonModelPath = "first.nif" } };
        builder.GetWeaponTemplatesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<SwordTemplate>>.Success([source]));
        builder.GetWeaponOptionsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Result<WeaponOptions>.Success(new()));
        settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new AppSettings { SkyrimDataPath = @"C:\Game\Data" });
        projects.CreateAsync(Arg.Any<CreatePluginRequest>(), Arg.Any<CancellationToken>()).Returns(Result<WeaponProject>.Success(new()
            { Name = "Smart", ProjectFilePath = @"C:\Mods\Smart\mod-project.json", PluginPath = @"C:\Mods\Smart\Smart.esp" }));
        drafts.SaveAsync(Arg.Any<SmartModDraft>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(c =>
            Result<DraftWriteReceipt>.Success(new("draft.json", c.Arg<long>() + 1, DateTimeOffset.UtcNow)));
        preview.LoadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Scene());
        using var vm = new SmartModViewModel(builder, projects, settings, drafts, validator, localization: localization);
        await vm.InitializeAsync(); Assert.False(vm.CanCreateProject); vm.Types[0].Count = "2"; Assert.True(vm.CanCreateProject); await vm.CreateProjectCommand.ExecuteAsync(null); Assert.Equal("", vm.Error); Assert.False(vm.CanCreateProject);
        var thumbnail = await new ModelThumbnailService(preview).CreateAsync("", "third.nif", "");
        foreach (var item in vm.Items) item.Thumbnail = thumbnail.Image;
        var window = new SmartModWindow(vm, preview);
        window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -10000; window.Top = -10000;
        window.ShowInTaskbar = false; window.ShowActivated = false;
        var root = (FrameworkElement)window.Content;
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ui-smart-mod")); Directory.CreateDirectory(output);
        foreach (var language in new[] { "uk", "en" })
        {
            languageCode = language;
            foreach (var old in window.Resources.MergedDictionaries.Where(d => d.Source?.OriginalString.Contains("Strings.") == true).ToArray()) window.Resources.MergedDictionaries.Remove(old);
            strings = new ResourceDictionary { Source = new Uri($"pack://application:,,,/SkyrimModPacker.UI;component/Resources/Strings.{language}.xaml") };
            window.Resources.MergedDictionaries.Add(strings);
            localization.LanguageChanged += Raise.Event<Action>();
            if (!window.IsVisible) { window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
            foreach (var width in new[] { 580d, 900d, 1360d })
            foreach (var step in Enum.GetValues<SmartModItemStep>())
            {
                vm.Section = SmartModSection.Weapons; vm.SelectedItem!.Step = step;
                window.Width = width;
                window.Measure(new Size(width, 900)); window.Arrange(new Rect(0, 0, width, 900));
                root.Measure(new Size(width - 32, 830)); root.Arrange(new Rect(0, 0, width - 32, 830)); root.UpdateLayout();
                Assert.True(root.ActualWidth > 0);
                var viewport = Assert.Single(Descendants<ModelPreviewControl>(root)); Assert.True(viewport.ActualHeight >= 260);
                await viewport.LoadAsync(new("", "third.nif", "first.nif", "")); Assert.True(viewport.HasScene);
                root.UpdateLayout();
                var list = Descendants<ListBox>(root).First(); Assert.True(list.IsVisible); Assert.Equal(2, list.Items.Count);
                var inputs = Descendants<TextBox>(root).Where(t => t.IsVisible && !t.IsReadOnly).ToArray();
                if (step is SmartModItemStep.NameAndTemplate or SmartModItemStep.Properties) Assert.NotEmpty(inputs);
                foreach (var input in inputs) Assert.True(Contrast(((SolidColorBrush)input.Foreground).Color, ((SolidColorBrush)input.Background).Color) >= 4.5);
                Assert.True(Contrast(((SolidColorBrush)window.Foreground).Color, ((SolidColorBrush)window.Background).Color) >= 4.5);
                Assert.Equal(5, vm.StepLinks.Count()); Assert.Single(vm.StepLinks, s => s.IsCurrent);
                if (step == SmartModItemStep.NameAndTemplate)
                {
                    var combo = Descendants<ComboBox>(root).First(c => c.IsVisible && c.Items.Count > 0);
                    var toggle = (System.Windows.Controls.Primitives.ToggleButton)combo.Template.FindName("DropToggle", combo);
                    Assert.True(toggle.ActualWidth >= combo.ActualWidth - 2);
                    combo.IsDropDownOpen = true; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var item = (ComboBoxItem)combo.ItemContainerGenerator.ContainerFromIndex(0);
                    Assert.True(Contrast(((SolidColorBrush)item.Foreground).Color, ((SolidColorBrush)item.Background).Color) >= 4.5);
                    combo.IsDropDownOpen = false;
                }
                var bitmap = new RenderTargetBitmap((int)(width - 32), 830, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, $"{language}-{width}-{step}.png")); encoder.Save(file);
            }
        }
        var closed = new TaskCompletionSource(); window.Closed += (_, _) => closed.TrySetResult(); window.Close();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    });
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is T item) yield return item;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static double Contrast(Color first, Color second)
    {
        static double Linear(byte channel) { var value = channel / 255d; return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4); }
        static double L(Color color) => 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        var a = L(first); var b = L(second); return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
}
