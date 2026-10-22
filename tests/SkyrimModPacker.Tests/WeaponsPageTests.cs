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
using Xunit;

namespace SkyrimModPacker.Tests;

[CollectionDefinition("WPF page", DisableParallelization = true)]
public class WpfPageCollection { }

[Collection("WPF page")]
public class WeaponsPageTests
{
    [Fact]
    public async Task Page_LoadsBindingsAndRendersAtDesktopAndMinimumWidths()
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
                var vm = new WeaponsViewModel(Substitute.For<IPluginBuilderService>(), settings, profiles)
                {
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
                foreach (var (width, creation, advanced) in new[] { (900, false, false), (600, false, false), (600, true, false), (600, false, true) })
                {
                    vm.ShowCreationPanel = creation;
                    var height = advanced ? 2600 : creation ? 1100 : 780;
                    root.Measure(new Size(width, height));
                    root.Arrange(new Rect(0, 0, width, height));
                    root.UpdateLayout();
                    foreach (var expander in Descendants<Expander>(page).ToList())
                    {
                        var isCreation = Equals(expander.Header, app.FindResource("Weapons_CreatePlugin"));
                        expander.SetCurrentValue(Expander.IsExpandedProperty, isCreation ? creation : advanced);
                    }
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                    if (creation || advanced)
                    {
                        // Let the WPF-UI expander finish its animation before capturing the form.
                        var frame = new DispatcherFrame();
                        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                        timer.Start();
                        Dispatcher.PushFrame(frame);
                    }
                    root.UpdateLayout();
                    var texts = Descendants<TextBox>(page).ToList();
                    Assert.Contains(texts, box => box.Text == "Крижаний меч");
                    Assert.Contains(texts, box => box.Text == @"C:\Mods\MySwords.esp");
                    Assert.Single(Assert.Single(Descendants<DataGrid>(page)).Items);
                    if (advanced) Assert.Contains(texts, box => box.Text == "Опис меча");
                    var previewDirectory = Environment.GetEnvironmentVariable("SKYRIM_UI_PREVIEW_DIR");
                    if (!string.IsNullOrWhiteSpace(previewDirectory))
                    {
                        Directory.CreateDirectory(previewDirectory);
                        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(root);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var file = File.Create(Path.Combine(previewDirectory, $"weapons-{(advanced ? "advanced-" : creation ? "create-" : string.Empty)}{width}.png"));
                        encoder.Save(file);
                    }
                    if (advanced && !string.IsNullOrWhiteSpace(previewDirectory))
                    {
                        root.Measure(new Size(width, 1000));
                        root.Arrange(new Rect(0, 0, width, 1000));
                        root.UpdateLayout();
                        Descendants<ScrollViewer>(page).First().ScrollToEnd();
                        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                        root.UpdateLayout();
                        var bitmap = new RenderTargetBitmap(width, 1000, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(root);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var file = File.Create(Path.Combine(previewDirectory, "weapons-advanced-bottom.png"));
                        encoder.Save(file);
                    }
                }
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
            finally { app?.Shutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
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
