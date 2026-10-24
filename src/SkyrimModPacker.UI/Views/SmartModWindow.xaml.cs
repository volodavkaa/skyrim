using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.UI.Controls;
using SkyrimModPacker.UI.Services;
using SkyrimModPacker.UI.ViewModels;
using SkyrimModPacker.Infrastructure.PluginBuilder;

namespace SkyrimModPacker.UI.Views;

public partial class SmartModWindow : Window
{
    public SmartModViewModel ViewModel { get; }
    private readonly ModelPreviewControl _preview;
    private readonly IModelPreviewService _previewService;
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly string? _initialProject;
    private string _previewIdentity = "";
    private bool _closeApproved, _closePending;
    public SmartModWindow(SmartModViewModel viewModel, IModelPreviewService previewService, string? projectPath = null)
    {
        ViewModel = viewModel; DataContext = viewModel; _initialProject = projectPath;
        InitializeComponent();
        _previewService = previewService; _preview = new(previewService); PreviewHost.Child = _preview;
        ViewModel.PreviewChanged += QueuePreview;
        ViewModel.PropertyChanged += OnViewModelChanged;
        _previewTimer.Tick += OnPreviewTick;
        Closed += (_, _) =>
        {
            _previewTimer.Stop(); _previewTimer.Tick -= OnPreviewTick;
            ViewModel.PreviewChanged -= QueuePreview; ViewModel.PropertyChanged -= OnViewModelChanged;
            _preview.Dispose(); ViewModel.Dispose();
        };
    }
    private async void OnLoaded(object sender, RoutedEventArgs e) { await ViewModel.InitializeAsync(_initialProject); QueuePreview(); }
    private void OnCancelOperation(object sender, RoutedEventArgs e) => ViewModel.CancelOperation();
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SmartModViewModel.IsBusy)) { WorkArea.IsEnabled = !ViewModel.IsBusy; ItemList.IsEnabled = !ViewModel.IsBusy; }
        if (e.PropertyName is nameof(SmartModViewModel.SelectedItem) or nameof(SmartModViewModel.Section)) EditorScroll.ScrollToTop();
        if (e.PropertyName == nameof(SmartModViewModel.IsAppearanceStep)) UpdateResponsiveLayout();
    }
    private void QueuePreview() { _previewTimer.Stop(); _previewTimer.Start(); }
    private async void OnPreviewTick(object? sender, EventArgs e)
    {
        _previewTimer.Stop();
        var identity = ViewModel.PreviewIdentity;
        if (_previewIdentity == identity) return;
        _previewIdentity = identity;
        var item = ViewModel.SelectedItem;
        var loading = _preview.LoadAsync(item == null ? null : new ModelPreviewRequest(ViewModel.ProjectDirectory, item.ModelPath, item.FirstPersonPath, ViewModel.GameDataPath));
        await ViewModel.RefreshThumbnailsAsync(); await loading;
    }
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateResponsiveLayout();
    private void UpdateResponsiveLayout()
    {
        if (NavigationColumn == null || EditorColumns == null || PreviewHost == null) return;
        NavigationColumn.Width = new GridLength(ActualWidth < 900 ? 160 : 265);
        var narrow = ActualWidth - 48 - NavigationColumn.Width.Value < EditorColumns.MinColumnWidth * 2 + EditorColumns.Gap;
        PreviewHost.Height = narrow ? 280 : 510;
        if (PreviewHost.Child is ModelPreviewControl preview) preview.SetCompact(narrow);
        var desiredIndex = narrow && ViewModel.IsAppearanceStep ? 0 : 1;
        if (EditorColumns.Children.IndexOf(PreviewHost) != desiredIndex)
        {
            EditorColumns.Children.Remove(PreviewHost); EditorColumns.Children.Insert(desiredIndex, PreviewHost);
        }
    }
    private bool Confirm(string key) => MessageBox.Show(this, ViewModel.Label(key), ViewModel.Label("Smart_Title"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    private async void OnRecoverBackup(object sender, RoutedEventArgs e) { if (!ViewModel.IsBusy && ViewModel.HasProject && Confirm("Smart_RecoverQuestion")) await ViewModel.RecoverBackupCommand.ExecuteAsync(null); }
    private void OnBrowse(object sender, RoutedEventArgs e) { var dialog = new OpenFolderDialog(); if (dialog.ShowDialog(this) == true) ViewModel.OutputDirectory = dialog.FolderName; }
    private async void OnOpen(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy) return;
        var dialog = new OpenFileDialog { Filter = "mod-project.json|mod-project.json" };
        if (dialog.ShowDialog(this) == true) { await ViewModel.OpenAsync(dialog.FileName); _previewIdentity = ""; QueuePreview(); }
    }
    private async void OnAdd(object sender, RoutedEventArgs e) { if (AddType.SelectedItem is SmartModTypeRow type) await ViewModel.AddItemAsync(type.Kind); }
    private async void OnDuplicate(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem == null || ViewModel.IsBusy) return;
        var copyRecipes = ViewModel.SelectedItem.Recipes.Count > 0 && Confirm("Smart_CopyRecipesQuestion");
        await ViewModel.DuplicateSelectedAsync(copyRecipes);
    }
    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem is not { } item || ViewModel.IsBusy) return;
        if (MessageBox.Show(this, item.Name + Environment.NewLine + item.Recipes.Count + " " + ViewModel.Label("Smart_DeleteConsequences"),
            ViewModel.Label("Smart_Title"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
            await ViewModel.DeleteSelectedAsync();
    }
    private async void OnApplyTemplate(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy) return;
        var choice = MessageBox.Show(this, ViewModel.Label("Smart_TemplateChangeQuestion"), ViewModel.Label("Smart_Title"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (choice != MessageBoxResult.Cancel) await ViewModel.ApplyTemplateAsync(choice == MessageBoxResult.Yes);
    }
    private async Task ImportModelAsync(bool first)
    {
        if (ViewModel.IsBusy) return;
        var dialog = new OpenFileDialog { Filter = "NIF (*.nif)|*.nif" };
        if (dialog.ShowDialog(this) == true) await ViewModel.ImportModelAsync(dialog.FileName, first);
    }
    private async void OnImportThird(object sender, RoutedEventArgs e) => await ImportModelAsync(false);
    private async void OnImportFirst(object sender, RoutedEventArgs e) => await ImportModelAsync(true);
    private async void OnMaterialPreset(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy || ViewModel.SelectedMaterialPreset is not { Available: true } preset) return;
        var dialog = new SmartTuningWindow((keep, _, _) => ViewModel.PreviewMaterialStats(preset, keep), ViewModel.Label, false, false, false) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Preview != null) await ViewModel.ApplyTuningAsync(dialog.Preview);
    }
    private async void OnBalance(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy || sender is not System.Windows.Controls.Button button || !Enum.TryParse<SmartBalanceMode>(button.Tag as string, out var mode)) return;
        var dialog = new SmartTuningWindow((keep, _, _) => ViewModel.PreviewBalance(mode, keep), ViewModel.Label, false, false, false) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Preview != null) await ViewModel.ApplyTuningAsync(dialog.Preview);
    }
    private async void OnUndoTuning(object sender, RoutedEventArgs e) => await ViewModel.UndoTuningAsync();
    private async Task ShowTuningAsync(Func<bool, SmartTuningPreview> calculate, bool keepManual = true, bool recipeOnly = false)
    {
        if (ViewModel.IsBusy || ViewModel.SelectedItem == null) return;
        var dialog = new SmartTuningWindow((keep, _, _) => calculate(keep), ViewModel.Label, false, false, false, keepManual, recipeOnly ? ViewModel.Label("Smart_RecipeKeepManual") : null) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Preview != null) await ViewModel.ApplyTuningAsync(dialog.Preview);
    }
    private async void OnHandling(object sender, RoutedEventArgs e)
    { if (sender is System.Windows.Controls.Button b && Enum.TryParse<SmartHandlingMode>(b.Tag as string, out var mode)) await ShowTuningAsync(keep => ViewModel.PreviewHandling(mode, keep)); }
    private async void OnReference(object sender, RoutedEventArgs e) => await ShowTuningAsync(ViewModel.PreviewReference);
    private async void OnRecipePreset(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button b) return;
        await ShowTuningAsync((string?)b.Tag switch { "standard" => ViewModel.PreviewStandardRecipe, "donor" => ViewModel.PreviewDonorRecipe, "personal" => ViewModel.PreviewPersonalRecipe, _ => _ => ViewModel.PreviewEmptyRecipe() }, keepManual: false, recipeOnly: true);
    }
    private async void OnSaveRecipePreset(object sender, RoutedEventArgs e) => await ViewModel.SaveRecipePresetAsync();
    private async void OnDeleteRecipePreset(object sender, RoutedEventArgs e)
    { if (!ViewModel.IsBusy && ViewModel.SelectedRecipePreset != null && Confirm("Smart_RecipePresetDeleteQuestion")) await ViewModel.DeleteRecipePresetAsync(); }
    private async void OnReloadMaterials(object sender, RoutedEventArgs e) => await ViewModel.RefreshMaterialPresetsAsync();
    private async Task OpenWeaponFolderAsync(string folder, bool createNew, string? sourceDisplayName = null)
    {
        if (ViewModel.IsBusy) return;
        var starting = !ViewModel.HasProject;
        if (starting && !ViewModel.CanStartFromResources) { ViewModel.Error = ViewModel.Label("Smart_StartNeedsName"); ViewModel.Section = SmartModSection.Overview; return; }
        var dialog = new WeaponFolderWindow(folder, starting ? ViewModel.ProposedProjectPath : ViewModel.ProjectPath, ViewModel.ScanWeaponFolderAsync, ViewModel.PreviewWeaponFolderAsync,
            ViewModel.Types, ViewModel.Label, starting || createNew, ViewModel.SelectedItem?.Kind ?? WeaponKind.OneHandSword, starting ? ViewModel.Name.Trim() + ".esp" : null, sourceDisplayName) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (dialog.TextureOnly)
        {
            if (starting) { ViewModel.Error = ViewModel.Label("Smart_FolderNoNif"); return; }
            var textures = new TextureFolderWindow(folder, mapping => ViewModel.PreviewTextureFolderAsync(folder, mapping),
                async () => ViewModel.SelectedItem is { ModelPath.Length: > 0 } item ? await _previewService.LoadAsync(ViewModel.ProjectDirectory, item.ModelPath, ViewModel.GameDataPath) : null, ViewModel.Label) { Owner = this };
            if (textures.ShowDialog() == true && textures.Plan != null) await ViewModel.ImportTextureFolderAsync(textures.Plan);
        }
        else if (dialog.Plan != null)
        {
            if (starting) await ViewModel.CreateFromWeaponFolderAsync(dialog.Plan, dialog.Kind);
            else await ViewModel.ImportWeaponFolderAsync(dialog.Plan, dialog.CreateNew, dialog.Kind);
        }
    }
    private async Task OpenWeaponArchiveAsync(string path)
    {
        if (ViewModel.IsBusy) return;
        try { await using var source = await ViewModel.ExtractWeaponArchiveAsync(path); if (source != null) await OpenWeaponFolderAsync(source.Root, true, System.IO.Path.GetFileName(path)); }
        catch (Exception ex) { ViewModel.Error = ex.Message; }
    }
    private async void OnWeaponArchive(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy) return;
        var dialog = new OpenFileDialog { Filter = "ZIP (*.zip)|*.zip" };
        if (dialog.ShowDialog(this) == true) await OpenWeaponArchiveAsync(dialog.FileName);
    }
    private async void OnWeaponFolder(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy) return; var folder = new OpenFolderDialog();
        if (folder.ShowDialog(this) == true) await OpenWeaponFolderAsync(folder.FolderName, (sender as System.Windows.Controls.Button)?.Tag as string != "existing");
    }
    private void OnFolderDragOver(object sender, DragEventArgs e)
    { e.Effects = !ViewModel.IsBusy && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths && (System.IO.Directory.Exists(paths[0]) || System.IO.Path.GetExtension(paths[0]).Equals(".zip", StringComparison.OrdinalIgnoreCase)) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private async void OnFolderDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths && System.IO.Directory.Exists(paths[0])) await OpenWeaponFolderAsync(paths[0], true);
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files && System.IO.Path.GetExtension(files[0]).Equals(".zip", StringComparison.OrdinalIgnoreCase)) await OpenWeaponArchiveAsync(files[0]);
        else ViewModel.Error = ViewModel.Label("Smart_FolderDropOne");
    }
    private async void OnManualTuning(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsBusy && ViewModel.SelectedItem != null) await ViewModel.ApplyTuningAsync(ViewModel.PreviewManualTuning());
    }
    private async void OnTextures(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy) return;
        var dialog = new OpenFileDialog { Filter = "DDS (*.dds)|*.dds", Multiselect = true };
        if (dialog.ShowDialog(this) == true) await ViewModel.ImportTexturesAsync(dialog.FileNames);
    }
    private async void OnTextureFolder(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy || !ViewModel.HasProject) return;
        var folder = new OpenFolderDialog(); if (folder.ShowDialog(this) != true) return;
        var dialog = new TextureFolderWindow(folder.FolderName, mapping => ViewModel.PreviewTextureFolderAsync(folder.FolderName, mapping),
            async () => ViewModel.SelectedItem is { ModelPath.Length: > 0 } item
                ? await _previewService.LoadAsync(ViewModel.ProjectDirectory, item.ModelPath, ViewModel.GameDataPath) : null, ViewModel.Label) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Plan is { } plan) await ViewModel.ImportTextureFolderAsync(plan);
    }
    private void OnClearPerk(object sender, RoutedEventArgs e) { if (!ViewModel.IsBusy && ViewModel.SelectedRecipe != null) ViewModel.SelectedRecipe.RequiredPerk = null; }
    private async void OnDeleteRecipe(object sender, RoutedEventArgs e) { if (!ViewModel.IsBusy && ViewModel.SelectedRecipe != null && Confirm("Smart_DeleteRecipeQuestion")) await ViewModel.DeleteRecipeAsync(); }
    private async void OnDisableRecipe(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy || ViewModel.SelectedItem == null || sender is not System.Windows.Controls.Button button) return;
        ViewModel.SelectedRecipe = ViewModel.SelectedItem.Recipes.FirstOrDefault(r => r.IsTempering == ((string?)button.Tag == "tempering"));
        if (ViewModel.SelectedRecipe != null && Confirm("Smart_DeleteRecipeQuestion")) await ViewModel.DeleteRecipeAsync();
    }
    private async void OnResolveRecipeConflict(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy || ViewModel.SelectedRecipe == null) return;
        var removed = ViewModel.SelectedItem!.Recipes.Where(r => r.IsTempering == ViewModel.SelectedRecipe.IsTempering && r.Id != ViewModel.SelectedRecipe.Id);
        var details = string.Join(Environment.NewLine, removed.Select(r => r.EditorId));
        if (MessageBox.Show(this, ViewModel.Label("Smart_ResolveRecipeQuestion") + Environment.NewLine + details, Title,
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes) await ViewModel.ResolveRecipeConflictAsync();
    }
    private void OnChooseIngredient(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy || ViewModel.SelectedRecipe == null) return;
        var row = (sender as System.Windows.Controls.Button)?.Tag as SmartModIngredientRow;
        var current = row?.Reference == null ? null : ViewModel.Options.Ingredients.FirstOrDefault(o => o.Reference == row.Reference)
            ?? new RecordOption { Reference = row.Reference, EditorId = row.Reference.ToString() };
        var picker = new MaterialPickerWindow(ViewModel.IngredientIndex, ViewModel.Label, current) { Owner = this };
        if (picker.ShowDialog() != true || picker.Selection is not { } selection) return;
        ViewModel.AddOrIncrementIngredient(selection, row);
    }
    private async void OnReplaceResource(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy || ViewModel.SelectedResource == null) return;
        var dialog = new OpenFileDialog { Filter = ViewModel.SelectedResource.RelativePath.EndsWith(".nif", StringComparison.OrdinalIgnoreCase) ? "NIF|*.nif" : "DDS|*.dds" };
        if (dialog.ShowDialog(this) == true && Confirm("Smart_ReplaceResourceQuestion")) await ViewModel.ReplaceResourceAsync(dialog.FileName);
    }
    private async void OnDeleteResource(object sender, RoutedEventArgs e) { if (!ViewModel.IsBusy && ViewModel.SelectedResource != null && Confirm("Smart_DeleteResourceQuestion")) await ViewModel.DeleteResourceAsync(); }
    private void OnEditResourceOwner(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy || ViewModel.SelectedResource == null) return;
        var resource = ViewModel.SelectedResource.Entry;
        var owner = ViewModel.Items.FirstOrDefault(i => resource.Uses.Any(u => u.Record.FormId == i.ConfirmedFormId) ||
            resource.RelativePath.Equals("meshes/" + i.ModelPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) ||
            resource.RelativePath.Equals("meshes/" + i.FirstPersonPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
        if (owner != null) { ViewModel.SelectedItem = owner; owner.Step = SmartModItemStep.Appearance; }
    }
    private async void OnReviewExternal(object sender, RoutedEventArgs e) { if (!ViewModel.IsBusy && Confirm("Smart_ReviewQuestion")) await ViewModel.ReviewExternalChangesCommand.ExecuteAsync(null); }
    private async void OnSaveExit(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.HasProject) { ViewModel.Error = ViewModel.Label("Smart_CreateFirst"); return; }
        if (await ViewModel.SaveDraftAsync()) { _closeApproved = true; Close(); }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeApproved) return;
        e.Cancel = true;
        if (_closePending || ViewModel.IsBusy) return;
        _closePending = true;
        try
        {
            await Dispatcher.Yield();
            if (!ViewModel.HasProject && (ViewModel.Types.Any(t => t.Count != "0") || ViewModel.Name != "MyWeapons") && !Confirm("Smart_DiscardSetup")) return;
            if (ViewModel.HasProject && !await ViewModel.SaveDraftAsync() && !Confirm("Smart_ExitWithoutSave")) return;
            _closeApproved = true; Close();
        }
        finally { _closePending = false; }
    }
}
