using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Navigation;
using NSubstitute;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.UI.ViewModels;
using SkyrimModPacker.UI.Views.Pages;
using SkyrimModPacker.UI.Views;
using SkyrimModPacker.UI.Services;
using Wpf.Ui;
using Xunit;

namespace SkyrimModPacker.Tests;

[CollectionDefinition("WPF page", DisableParallelization = true)]
public class WpfPageCollection { }

[Collection("WPF page")]
public class WeaponsPageTests
{
    [Fact]
    public async Task Page_RendersEveryWizardStepAtDesktopAndMinimumWidths()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            System.Windows.Application? app = null;
            try
            {
                app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources = (ResourceDictionary)XamlReader.Parse("""
                    <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                        xmlns:ui="clr-namespace:Wpf.Ui.Markup;assembly=Wpf.Ui"
                        xmlns:converters="clr-namespace:SkyrimModPacker.UI.Converters;assembly=SkyrimModPacker.UI">
                        <ResourceDictionary.MergedDictionaries>
                            <ui:ThemesDictionary Theme="Dark" />
                            <ui:ControlsDictionary />
                            <ResourceDictionary Source="pack://application:,,,/SkyrimModPacker.UI;component/Resources/Strings.uk.xaml" />
                        </ResourceDictionary.MergedDictionaries>
                        <BooleanToVisibilityConverter x:Key="BooleanToVisibilityConverter" />
                        <converters:InverseBooleanConverter x:Key="InverseBooleanConverter" />
                        <converters:NotNullToVisibilityConverter x:Key="NotNullToVisibilityConverter" />
                    </ResourceDictionary>
                    """);
                var settings = Substitute.For<ISettingsRepository>();
                settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new AppSettings());
                var profiles = Substitute.For<IProfileRepository>();
                profiles.LoadAllAsync(Arg.Any<CancellationToken>()).Returns(new List<ModProfile>());
                RecordOption Choice(uint id, string editorId) => new() { Reference = new PluginRecordReference("Skyrim.esm", id), EditorId = editorId };
                var keyword = Choice(0x100, "WeapTypeSword");
                var sound = Choice(0x103, "SwordAttackSound");
                var enchantment = Choice(0x104, "FireWeaponEnchantment");
                var workbench = Choice(0x101, "CraftingSmithingForge");
                var ingredient = Choice(0x102, "IngotIron");
                var perk = Choice(0x106, "SteelSmithing");
                var list = Choice(0x2000, "LItemSword");
                var vm = new WeaponsViewModel(Substitute.For<IPluginBuilderService>(), settings, profiles, Substitute.For<IWeaponProjectService>())
                {
                    IsBaseConnected = true, GameDataPath = @"C:\Games\Skyrim\Data",
                    ProjectResources = ["meshes/Custom/blade.nif", "textures/Custom/blade.dds"], TextureSubdirectory = "Custom",
                    ResourceCatalog =
                    [
                        new ProjectResourceRow(new ProjectResourceEntry("meshes/Custom/shared/blade.nif", FileCategory.Mesh,
                            ModelOrigin.Project, ModelAvailability.Found, true, ResourceArchiveStatus.Included,
                            [new ResourceUsage(new PluginRecordReference("MySwords.esp", 0x800), "CustomIronSword", ResourceUseKind.ThirdPerson)],
                            [ResourceIssue.VanillaShadow]), (key, fallback) => app.TryFindResource(key) as string ?? fallback),
                        new ProjectResourceRow(new ProjectResourceEntry("textures/Custom/blade.dds", FileCategory.Texture,
                            ModelOrigin.Project, ModelAvailability.Missing, true, ResourceArchiveStatus.Blocked, [],
                            [ResourceIssue.MissingOrEmpty]), (key, fallback) => app.TryFindResource(key) as string ?? fallback)
                    ],
                    PluginPath = @"C:\Mods\MySwords.esp", ShowCreationPanel = false,
                    Templates = [new SwordTemplate { FormId = 0x12EB7, EditorId = "IronSword", Name = "Iron Sword", Damage = 7, Weight = 9, Value = 25 }],
                    Weapons = [new WeaponRecord
                    {
                        FormId = 0x800, EditorId = "CustomIronSword", Name = "Крижаний меч", Damage = 20, Weight = 6.5f, Value = 400,
                        Details = new WeaponDetails { Description = "Опис меча", Speed = 1.2f, Reach = 1.4f, ModelPath = @"Weapons\Custom\blade.nif", Keywords = [keyword.Reference], AttackSound = sound.Reference, Enchantment = enchantment.Reference, EnchantmentCharge = 1000 }
                    }],
                    Options = new WeaponOptions { Keywords = [keyword, workbench], Sounds = [sound], Enchantments = [enchantment], Ingredients = [ingredient], Perks = [perk], LeveledLists = [list] },
                    SoundChoices = [sound], EnchantmentChoices = [enchantment], WorkbenchChoices = [workbench], PerkChoices = [perk],
                    SelectedLeveledList = list,
                    PackingProfiles = [new ModProfile { Name = "MySwords" }]
                };
                vm.SelectedWeapon = vm.Weapons[0];
                var recipe = new CraftingRecipe { FormId = 0x801, WeaponFormId = 0x800, EditorId = "RecipeCustomIronSword", Workbench = workbench.Reference, Ingredients = [new RecipeIngredient { Reference = ingredient.Reference, Count = 2 }], RequiredPerk = perk.Reference };
                vm.Recipes.Add(recipe);
                vm.SelectedRecipe = recipe;
                var page = new WeaponsPage(vm)
                {
                    Foreground = app.TryFindResource("TextFillColorPrimaryBrush") as Brush ?? Brushes.White,
                    FontSize = 14
                };
                var root = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(32, 32, 32)),
                    Child = new Frame { Content = page, NavigationUIVisibility = NavigationUIVisibility.Hidden }
                };
                vm.WeaponSummary = [
                    new WeaponSummaryRow { Label = "Плагін", Value = @"C:\Mods\MySwords.esp" },
                    new WeaponSummaryRow { Label = "Шаблон", Value = "IronSword" },
                    new WeaponSummaryRow { Label = "Назва", Value = "Iron Sword → Крижаний меч", IsChanged = true },
                    new WeaponSummaryRow { Label = "Шкода", Value = "7 → 20", IsChanged = true },
                    new WeaponSummaryRow { Label = "Модель", Value = @"Weapons\Custom\blade.nif", IsChanged = true }
                ];
                vm.SummaryFiles = ["MySwords.esp", "meshes/Custom/blade.nif", "textures/Custom/blade.dds"];
                foreach (var (width, scale) in new[] { (900, 1d), (600, 1d), (420, 1d), (1200, 1d), (600, 1.3d) })
                foreach (var step in Enumerable.Range(0, 5))
                {
                    page.EditorScale = scale;
                    vm.CurrentStep = step;
                    vm.ShowCreationPanel = step == 0;
                    vm.ShowAdvancedProperties = step == 3;
                    vm.Weight = "6.5";
                    if (width == 600 && step == 3)
                    {
                        vm.Weight = "NaN";
                        vm.WeightError = "Введіть скінченну вагу від 0.";
                    }
                    var height = step == 0 || step == 3 ? 1300 : 1000;
                    var scroll = Descendants<ScrollViewer>(page).FirstOrDefault();
                    scroll?.ScrollToTop();
                    root.Measure(new Size(width, height));
                    root.Arrange(new Rect(0, 0, width, height));
                    root.UpdateLayout();
                    foreach (var expander in Descendants<Expander>(page).ToList())
                    {
                        if (Equals(expander.Header, app.FindResource("Weapons_Resources")))
                            expander.SetCurrentValue(Expander.IsExpandedProperty, true);
                    }
                    var frame = new DispatcherFrame();
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                    timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                    timer.Start();
                    Dispatcher.PushFrame(frame);
                    root.UpdateLayout();
                    var viewport = Descendants<ScrollViewer>(page).First();
                    Assert.True(viewport.ExtentWidth <= viewport.ViewportWidth + 1, $"Horizontal overflow at width {width}, scale {scale}, step {step}");
                    var texts = Descendants<TextBox>(page).ToList();
                    switch (step)
                    {
                        case 0:
                            Assert.Contains(texts, box => box.Text == @"C:\Mods\MySwords.esp");
                            break;
                        case 1:
                            Assert.Single(Assert.Single(Descendants<DataGrid>(page), grid => ReferenceEquals(grid.ItemsSource, vm.Weapons)).Items);
                            break;
                        case 2:
                            Assert.Contains(texts, box => box.Text == @"Weapons\Custom\blade.nif");
                            Assert.Contains(texts, box => box.Text == "Custom");
                            Assert.Contains(Descendants<TextBlock>(page), text => text.Text == "textures/Custom/blade.dds");
                            break;
                        case 3:
                            Assert.Contains(texts, box => box.Text == "Крижаний меч");
                            Assert.Contains(texts, box => box.Text == "Опис меча");
                            if (width == 600) Assert.Contains(Descendants<TextBlock>(page), text => text.Text == vm.WeightError);
                            break;
                        case 4:
                            Assert.Contains(Descendants<TextBlock>(page), text => text.Text == "7 → 20");
                            Assert.Contains(Descendants<System.Windows.Controls.Button>(page), button => button.Command == vm.SaveWizardSwordCommand);
                            Assert.Contains(Descendants<System.Windows.Controls.Button>(page), button => button.Command == vm.PackProjectCommand);
                            break;
                    }
                    var previewDirectory = Environment.GetEnvironmentVariable("SKYRIM_UI_PREVIEW_DIR");
                    if (!string.IsNullOrWhiteSpace(previewDirectory))
                    {
                        Directory.CreateDirectory(previewDirectory);
                        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(root);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        var zoomSuffix = scale == 1 ? "" : "-zoom130";
                        using var file = File.Create(Path.Combine(previewDirectory, $"weapons-step{step}-{width}{zoomSuffix}.png"));
                        encoder.Save(file);
                    }
                }
                // Exercise the real asynchronous Closing handler without showing a desktop window.
                var prompt = Substitute.For<IUnsavedChangesPrompt>();
                prompt.ShowAsync().Returns(UnsavedChangesChoice.ContinueEditing);
                var closeEditor = new WeaponsViewModel(Substitute.For<IPluginBuilderService>(), settings, profiles,
                    Substitute.For<IWeaponProjectService>(), unsavedChangesPrompt: prompt);
                closeEditor.WeaponName = "Pending edit";
                var main = new MainWindow(new MainWindowViewModel(), Substitute.For<INavigationService>(), closeEditor);
                var closed = false;
                main.Closed += (_, _) => closed = true;
                void PumpClose()
                {
                    var frame = new DispatcherFrame();
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                    timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                    timer.Start(); Dispatcher.PushFrame(frame);
                }
                main.Close(); main.Close();
                PumpClose();
                Assert.False(closed);
                Assert.True(closeEditor.HasUnsavedChanges);
                prompt.Received(1).ShowAsync();
                prompt.ShowAsync().Returns(UnsavedChangesChoice.Discard);
                main.Close(); PumpClose();
                Assert.True(closed);
                prompt.Received(2).ShowAsync();
                using (var scheduler = new DraftAutosaveScheduler(TimeSpan.FromMilliseconds(20)))
                {
                    var saves = 0;
                    scheduler.Schedule(() => { saves += 100; return Task.CompletedTask; });
                    scheduler.Schedule(() => { saves++; return Task.CompletedTask; });
                    PumpClose();
                    Assert.Equal(1, saves); // The real dispatcher timer debounces to the newest callback.
                    scheduler.Schedule(() => { saves++; return Task.CompletedTask; });
                    scheduler.Cancel(); PumpClose();
                    Assert.Equal(1, saves);
                }
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
            finally { app?.Shutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
