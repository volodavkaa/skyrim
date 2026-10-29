using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure.PluginBuilder;
using SkyrimModPacker.Infrastructure.Preview;
using SkyrimModPacker.UI.ViewModels;

namespace SkyrimModPacker.UI.Views;

/// <summary>A read-only snapshot of a selected draft and its verified saved references.</summary>
public sealed class ItemOverviewWindow : Window
{
    private readonly SmartModViewModel _vm;
    private readonly SmartModItemRow _item;
    private readonly CancellationTokenSource _cancel = new();
    private readonly StackPanel _models = new(), _textures = new(), _recipes = new(), _lists = new(), _issues = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private bool _closed, _refreshing;

    public ItemOverviewWindow(SmartModViewModel vm, SmartModItemRow item)
    {
        _vm = vm; _item = item;
        Title = L("Smart_ItemOverview"); Width = 760; Height = 780; MinWidth = 520; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = new SolidColorBrush(Color.FromRgb(16, 23, 34)); Foreground = Brushes.White; FontSize = 14;
        Resources.MergedDictionaries.Add(new() { Source = new Uri("/SkyrimModPacker.UI;component/Resources/SmartModTheme.xaml", UriKind.Relative) });
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = Title + ": " + item.Name, FontSize = 23, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        header.Children.Add(new TextBlock { Text = L("Smart_ItemOverviewScope"), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray, Margin = new Thickness(0, 4, 0, 10) });
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var refresh = new Button { Content = L("Smart_ItemOverviewRefresh") }; refresh.Click += async (_, _) => await RefreshAsync(); bottom.Children.Add(refresh);
        var close = new Button { Content = L("Smart_Close"), IsCancel = true }; bottom.Children.Add(close);
        var body = new StackPanel(); root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        body.Children.Add(_status);
        AddSection(body, L("Smart_ItemOverviewModels"), _models, L("Smart_ItemOverviewEdit"), () => Go(SmartModItemStep.Appearance));
        AddSection(body, L("Smart_ItemOverviewTextures"), _textures, L("Smart_ItemOverviewResources"), () => { _vm.Section = SmartModSection.Resources; Close(); });
        AddSection(body, L("Smart_ItemOverviewRecipes"), _recipes, L("Smart_ItemOverviewEdit"), () => Go(SmartModItemStep.Acquisition));
        var enchantment = new StackPanel();
        var enchantName = item.Enchantment == null ? L("Smart_ItemOverviewNone") :
            vm.Options.Enchantments.FirstOrDefault(e => e.Reference == item.Enchantment)?.DisplayName ?? item.Enchantment.ToString();
        Line(enchantment, enchantName + (item.Enchantment == null ? "" : " [" + item.Enchantment + "] · " + L("Weapons_Charge") + ": " + item.EnchantmentCharge));
        AddSection(body, L("Weapons_Enchantment"), enchantment, L("Smart_ItemOverviewEdit"), () => Go(SmartModItemStep.Properties));
        AddSection(body, L("Smart_ItemOverviewLists"), _lists, L("Smart_ItemOverviewEdit"), () => Go(SmartModItemStep.Acquisition));
        AddSection(body, L("Smart_ItemOverviewIssues"), _issues, L("Smart_ItemOverviewChecks"), () => { _vm.Section = SmartModSection.Checks; Close(); });
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => { _closed = true; _cancel.Cancel(); _cancel.Dispose(); };
    }

    private string L(string key) => _vm.Label(key);
    private void Go(SmartModItemStep step) { _vm.GoToStepCommand.Execute(step); Close(); }
    private static void Line(Panel panel, string text) => panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) });
    private void ModelIssue(string message)
    {
        var button = new Button { Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, HorizontalContentAlignment = HorizontalAlignment.Left };
        button.Click += (_, _) => Go(SmartModItemStep.Appearance); _issues.Children.Add(button);
    }
    private static void AddSection(Panel root, string title, StackPanel content, string action, Action navigate)
    {
        var card = new StackPanel { Margin = new Thickness(0, 8, 0, 8) }; root.Children.Add(card);
        card.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 17 });
        card.Children.Add(content);
        var button = new Button { Content = action, HorizontalAlignment = HorizontalAlignment.Left }; button.Click += (_, _) => navigate(); card.Children.Add(button);
    }

    private async Task RefreshAsync()
    {
        if (_refreshing || _closed) return;
        _refreshing = true;
        var token = _cancel.Token;
        _models.Children.Clear(); _textures.Children.Clear(); _recipes.Children.Clear(); _lists.Children.Clear(); _issues.Children.Clear();
        _status.Text = L("Smart_ItemOverviewLoading");
        var third = _item.ModelPath; var first = _item.FirstPersonPath;
        Line(_models, L("Models_Third") + ": " + (string.IsNullOrWhiteSpace(third) ? L("Smart_ItemOverviewNotSet") : third));
        Line(_models, L("Models_First") + ": " + (string.IsNullOrWhiteSpace(first) ? L("Smart_ItemOverviewNotSet") : first));
        Line(_models, L("Smart_ItemOverviewPlugin") + ": " + Path.GetFileName(_vm.PluginPath));
        if (_item.Recipes.Count == 0) Line(_recipes, L("Smart_ItemOverviewNone"));
        foreach (var recipe in _item.Recipes)
            Line(_recipes, L(recipe.IsTempering ? "Smart_Tempering" : "Smart_Crafting") + ": " + recipe.EditorId + " — " + L(recipe.ConfirmedFormId == null ? "Smart_ItemOverviewDraft" : "Smart_ItemOverviewSavedRecipe"));
        foreach (var issue in _vm.Issues.Where(i => i.ItemId == _item.Id))
        {
            var button = new Button { Content = new TextBlock { Text = issue.Message, TextWrapping = TextWrapping.Wrap }, HorizontalContentAlignment = HorizontalAlignment.Left };
            button.Click += (_, _) => { _vm.NavigateIssueCommand.Execute(issue); Close(); }; _issues.Children.Add(button);
        }
        Line(_issues, L("Smart_ItemOverviewLastCheck"));
        try
        {
            foreach (var model in new[] { (Label: L("Models_Third"), Path: third), (Label: L("Models_First"), Path: first) }.DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(model.Path)) { Line(_textures, model.Label + ": " + L("Smart_ItemOverviewModelUnknown")); continue; }
                try
                {
                    var scene = await Task.Run(() => ModelPreviewService.Inspect(_vm.ProjectDirectory, model.Path, _vm.GameDataPath, token), token);
                    if (_closed) return;
                    Line(_textures, model.Label + ": " + model.Path);
                    if (scene.TextureDependencies.Count == 0 && scene.TextureDependenciesComplete) Line(_textures, L("Smart_ItemOverviewNoTextureRefs"));
                    foreach (var texture in scene.TextureDependencies)
                    {
                        var status = texture.Available == true ? L("Smart_ItemOverviewFound") : texture.Available == false ? L("Smart_ItemOverviewMissing") : L("Smart_ItemOverviewUnknown");
                        Line(_textures, "  " + status + ": " + texture.Path + (texture.Available == null && texture.Detail != null ? " — " + texture.Detail : ""));
                        if (texture.Available == false) ModelIssue(L("Smart_ItemOverviewMissing") + ": " + texture.Path);
                    }
                    if (!scene.TextureDependenciesComplete) Line(_textures, L("Smart_TextureUnknown"));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (_closed) return;
                    var message = model.Label + ": " + (ex is FileNotFoundException ? L("Smart_ItemOverviewMissing") : L("Smart_ItemOverviewUnknown")) + " — " + ex.Message;
                    Line(_textures, message); ModelIssue(message);
                }
            }
            if (_item.ConfirmedFormId is not { } formId) Line(_lists, L("Smart_ItemOverviewUnsaved"));
            else
            {
                try
                {
                    var links = await Task.Run(() => ItemOverviewInspector.ReadDirectLists(_vm.PluginPath, formId, token), token);
                    if (_closed) return;
                    if (links.Count == 0) Line(_lists, L("Smart_ItemOverviewNoLists"));
                    foreach (var link in links)
                    {
                        var name = (string.IsNullOrWhiteSpace(link.EditorId) ? link.List.ToString() : link.EditorId + " [" + link.List + "]") + " — " + L("Smart_ListMinLevel") + ": " + link.Level + "; " + L("Smart_Quantity") + ": " + link.Count;
                        var option = _vm.Options.LeveledLists.FirstOrDefault(o => o.Reference == link.List);
                        if (option == null) Line(_lists, name);
                        else
                        {
                            var button = new Button { Content = new TextBlock { Text = name, TextWrapping = TextWrapping.Wrap }, HorizontalContentAlignment = HorizontalAlignment.Left };
                            button.Click += (_, _) => { _vm.FilterListCommand.Execute("All"); _vm.SelectedLeveledList = option; Go(SmartModItemStep.Acquisition); };
                            _lists.Children.Add(button);
                        }
                    }
                    Line(_lists, L("Smart_ItemOverviewListScope"));
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { if (!_closed) Line(_lists, L("Smart_ItemOverviewUnknown") + ": " + ex.Message); }
            }
            if (!_closed) _status.Text = L("Smart_ItemOverviewSnapshot");
        }
        catch (OperationCanceledException) { }
        finally { _refreshing = false; }
    }
}
