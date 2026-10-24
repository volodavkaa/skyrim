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
using SkyrimModPacker.UI.Services;
using SkyrimModPacker.UI.ViewModels;
using SkyrimModPacker.UI.Views;
using Xunit;
namespace SkyrimModPacker.Tests;
public sealed partial class SmartModWindowTests
{
    [Fact]
    public Task ResourceStartAndExpandedRecipePanelsRenderBothLanguages() => Sta(async () =>
    {
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ui-smart-expanded")); Directory.CreateDirectory(output);
        foreach (var language in new[] { "uk", "en" })
        {
            var strings = new ResourceDictionary { Source = new Uri($"pack://application:,,,/SkyrimModPacker.UI;component/Resources/Strings.{language}.xaml") };
            var localization = Substitute.For<ILocalizationService>(); localization.CurrentLanguage.Returns(language); localization.GetString(Arg.Any<string>()).Returns(c => strings[c.Arg<string>()] as string ?? c.Arg<string>());
            var builder = Substitute.For<IPluginBuilderService>(); var projects = Substitute.For<IWeaponProjectService>(); var settings = Substitute.For<ISettingsRepository>(); var drafts = Substitute.For<ISmartModDraftRepository>(); var preview = Substitute.For<IModelPreviewService>();
            var reference = new PluginRecordReference("Skyrim.esm", 0x13989); var bench = new PluginRecordReference("Skyrim.esm", 1); var ingredient = new PluginRecordReference("Skyrim.esm", 2);
            var recipe = new CraftingRecipe { EditorId = "SteelSwordRecipe", Workbench = bench, Ingredients = [new() { Reference = ingredient, Count = 2 }] };
            builder.GetWeaponTemplatesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<SwordTemplate>>.Success([new() { FormId = reference.FormId, Name = "Steel sword / Сталевий меч", EditorId = "SteelSword", Damage = 8, Weight = 10, Value = 45, Details = new() { ModelPath = "third.nif", FirstPersonModelPath = "first.nif" } }]));
            builder.GetWeaponOptionsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Result<WeaponOptions>.Success(new() { Keywords = [new() { Reference = bench, EditorId = "CraftingSmithingForge" }], Ingredients = [new() { Reference = ingredient, EditorId = "IngotSteel", Name = "Steel ingot / Сталевий злиток" }] }));
            builder.GetSmartMaterialPresetsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<SmartMaterialPreset>>.Success(new[] { "Iron", "Steel", "Orcish", "Dwarven", "Elven", "Glass", "Ebony", "Daedric", "Dragonbone" }.Select(m => new SmartMaterialPreset(m + ":Sword", m, SmartWeaponSubtype.Sword, WeaponKind.OneHandSword, reference, m + "Sword", new(8, 10, 45, 1, 1), [], new(bench, recipe), null, "", null)).ToArray()));
            builder.GetSmartRecipeSourcesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<SmartRecipeSource>>.Success([new(bench, reference, "SteelSword", "Steel sword / Сталевий меч", WeaponKind.OneHandSword, SmartWeaponSubtype.Sword, recipe, "")]));
            settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new AppSettings { SkyrimDataPath = @"C:\Game\Data" });
            projects.CreateAsync(Arg.Any<CreatePluginRequest>(), Arg.Any<CancellationToken>()).Returns(Result<WeaponProject>.Success(new() { Name = "Smart", ProjectFilePath = @"C:\Mods\Smart\mod-project.json", PluginPath = @"C:\Mods\Smart\Smart.esp" }));
            drafts.SaveAsync(Arg.Any<SmartModDraft>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(c => Result<DraftWriteReceipt>.Success(new("draft", c.Arg<long>() + 1, DateTimeOffset.UtcNow)));
            preview.LoadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Scene());
            using var vm = new SmartModViewModel(builder, projects, settings, drafts, Substitute.For<IPluginValidationService>(), localization: localization);
            var window = new SmartModWindow(vm, preview) { ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000 };
            window.Resources.MergedDictionaries.Add(strings); window.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(vm.CanStartFromResources); Assert.False(vm.CanCreateProject); Assert.Empty(vm.Items);
            Assert.False(vm.ShowTemplateSetup); vm.GameTemplateSetupExpanded = true; Assert.True(vm.ShowTemplateSetup); vm.GameTemplateSetupExpanded = false;
            void Render(string step, double width)
            {
                window.Width = width; var root = (FrameworkElement)window.Content; root.Measure(new Size(width - 32, 830)); root.Arrange(new Rect(0, 0, width - 32, 830)); root.UpdateLayout();
                foreach (var text in Descendants<TextBox>(root).Where(t => t.IsVisible)) Assert.True(Contrast(((SolidColorBrush)text.Foreground).Color, ((SolidColorBrush)text.Background).Color) >= 4.5);
                var bitmap = new RenderTargetBitmap((int)(width - 32), 830, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, $"{language}-{step}-{width}.png")); png.Save(file);
            }
            void RenderPanel(FrameworkElement panel, string name, double width)
            {
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth), (int)Math.Ceiling(panel.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                var drawing = new DrawingVisual(); using (var context = drawing.RenderOpen())
                { context.DrawRectangle(new SolidColorBrush(Color.FromRgb(16, 23, 34)), null, new Rect(0, 0, panel.ActualWidth, panel.ActualHeight)); context.DrawRectangle(new VisualBrush(panel), null, new Rect(0, 0, panel.ActualWidth, panel.ActualHeight)); }
                bitmap.Render(drawing); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, $"{language}-{name}-panel-{width}.png")); png.Save(file);
            }
            foreach (var width in new[] { 600d, 1360d }) Render("start", width);
            vm.Types[0].Count = "1"; await vm.CreateProjectCommand.ExecuteAsync(null); Assert.Equal("", vm.Error);
            vm.SelectedItem!.Step = SmartModItemStep.Properties;
            foreach (var expander in Descendants<Expander>((FrameworkElement)window.Content)) if (expander.Header as string == (string)strings["Smart_ReferenceOther"]) expander.IsExpanded = true;
            foreach (var width in new[] { 600d, 1360d })
            { Render("statistics", width); var card = Descendants<Border>((FrameworkElement)window.Content).First(b => b.Child is StackPanel panel && panel.Children.OfType<TextBlock>().Any(t => t.Text == (string)strings["Smart_MaterialTitle"])); RenderPanel(card, "statistics", width); }
            vm.AddRecipeCommand.Execute("crafting"); vm.AddOrIncrementIngredient(vm.Options.Ingredients[0]); vm.SelectedRecipeMaterial = vm.MaterialChoices.First(); vm.SelectedRecipeSource = vm.RecipeSourceChoices.First();
            foreach (var expander in Descendants<Expander>((FrameworkElement)window.Content)) if (expander.Header as string is { } header && (header == (string)strings["Smart_RecipeStandard"] || header == (string)strings["Smart_RecipeDonor"] || header == (string)strings["Smart_RecipePersonal"])) expander.IsExpanded = true;
            foreach (var width in new[] { 600d, 1360d }) { Render("recipes", width); RenderPanel((FrameworkElement)window.FindName("RecipeEditor"), "recipes", width); }
            var replacement = new SmartTuningWindow((keep, _, _) => vm.PreviewStandardRecipe(keep), vm.Label, false, false, false, false, vm.Label("Smart_RecipeKeepManual"))
                { Owner = window, ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000, WindowStartupLocation = WindowStartupLocation.Manual };
            replacement.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(Descendants<CheckBox>((FrameworkElement)replacement.Content).First().IsChecked);
            Assert.Equal("2", replacement.Preview!.After.Recipes.Single().Ingredients.Single().Count);
            Assert.Contains("× 1", replacement.Preview.Description); Assert.Contains("× 2", replacement.Preview.Description);
            RenderPanel((FrameworkElement)replacement.Content, "recipe-replacement", 780); replacement.Close();
            var closed = new TaskCompletionSource(); window.Closed += (_, _) => closed.TrySetResult(); window.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    });
}
