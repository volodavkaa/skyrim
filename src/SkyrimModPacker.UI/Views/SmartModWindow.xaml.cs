using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.UI.Controls;
using SkyrimModPacker.UI.Services;
using SkyrimModPacker.UI.ViewModels;

namespace SkyrimModPacker.UI.Views;

public partial class SmartModWindow : Window
{
    public SmartModViewModel ViewModel { get; }
    private readonly ModelPreviewControl _preview;
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly string? _initialProject;
    private string _previewIdentity = "";
    private bool _closeApproved, _closePending;
    public SmartModWindow(SmartModViewModel viewModel, IModelPreviewService previewService, string? projectPath = null)
    {
        ViewModel = viewModel; DataContext = viewModel; _initialProject = projectPath;
        InitializeComponent();
        _preview = new(previewService); PreviewHost.Child = _preview;
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
    private async void OnTextures(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy) return;
        var dialog = new OpenFileDialog { Filter = "DDS (*.dds)|*.dds", Multiselect = true };
        if (dialog.ShowDialog(this) == true) await ViewModel.ImportTexturesAsync(dialog.FileNames);
    }
    private void OnClearPerk(object sender, RoutedEventArgs e) { if (!ViewModel.IsBusy && ViewModel.SelectedRecipe != null) ViewModel.SelectedRecipe.RequiredPerk = null; }
    private async void OnDeleteRecipe(object sender, RoutedEventArgs e) { if (!ViewModel.IsBusy && ViewModel.SelectedRecipe != null && Confirm("Smart_DeleteRecipeQuestion")) await ViewModel.DeleteRecipeAsync(); }
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
