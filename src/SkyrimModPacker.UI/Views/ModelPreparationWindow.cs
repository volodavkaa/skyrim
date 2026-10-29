using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Win32;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Infrastructure.ModelPreparation;
using SkyrimModPacker.Infrastructure.Preview;
using SkyrimModPacker.UI.Controls;
using SkyrimModPacker.UI.Services;

namespace SkyrimModPacker.UI.Views;

/// <summary>Standalone preparation window; it does not access an open mod project.</summary>
public sealed class ModelPreparationWindow : Window
{
    private readonly TextBox _archive = new();
    private readonly ComboBox _modelEntry = new() { IsEditable = true, MinWidth = 280 };
    private readonly TextBox _gameData = new();
    private readonly TextBox _blender = new();
    private readonly TextBox _addon = new();
    private readonly TextBox _output = new();
    private readonly ComboBox _weaponType = new() { DisplayMemberPath = "DisplayName" };
    private readonly CheckBox _useTextures = new() { Content = "Створити DDS та підключити текстури зброї" };
    private readonly CheckBox _defaultShader = new() { Content = "Шейдер зброї: Default (без відбиттів Environment Map)" };
    private readonly CheckBox _useVertexColor = new() { Content = "Задати однаковий Vertex Color для зброї та піхов" };
    private readonly Slider _vertexColor = new() { Minimum = 0, Maximum = 100, Value = 100, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly TextBlock _vertexColorValue = new();
    private readonly CheckBox _useEnvironmentStrength = new() { Content = "Налаштувати силу Environment Map" };
    private readonly Slider _environmentStrength = new() { Minimum = 0, Maximum = 4, Value = 1, TickFrequency = 0.05, IsSnapToTickEnabled = true };
    private readonly CheckBox _useSpecular = new() { Content = "Налаштувати колір і силу відблиску (Specular)" };
    private readonly Slider _specularStrength = new() { Minimum = 0, Maximum = 4, Value = 1, TickFrequency = 0.05, IsSnapToTickEnabled = true };
    private readonly TriangleColorPicker _specularColor = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _diffuseEntry = new();
    private readonly TextBox _normalEntry = new();
    private readonly TextBox _metallicEntry = new();
    private readonly TextBox _cubemapEntry = new();
    private readonly ComboBox _textureSizeLimit = new();
    private readonly TextBlock _textureSizeReport = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 6) };
    private int _textureSizeGeneration;
    private readonly ComboBox _gameCubemap = new() { DisplayMemberPath = "Name" };
    private readonly ModelPreviewControl _preview = new(new ModelPreviewService());
    private readonly TextBlock _previewStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _materialEntries = new() { AcceptsReturn = true, Height = 78,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _materialAudit = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _texconv = new();
    private readonly CheckBox _invertNormalY = new() { Content = "Інвертувати зелений канал normal map (якщо джерело OpenGL)" };
    private readonly CheckBox _scabbard = new() { Content = "Створити піхви за формою клинка" };
    private readonly TextBox _scale = new() { Text = "1" };
    private readonly TextBox _rotationX = new() { Text = "0" };
    private readonly TextBox _rotationY = new() { Text = "0" };
    private readonly TextBox _rotationZ = new() { Text = "0" };
    private readonly Slider _rotationSliderX = RotationSlider();
    private readonly Slider _rotationSliderY = RotationSlider();
    private readonly Slider _rotationSliderZ = RotationSlider();
    private readonly Slider _scaleSlider = new() { Minimum = 0.25, Maximum = 4, Value = 1, TickFrequency = 0.05, IsSnapToTickEnabled = true, SmallChange = 0.05, LargeChange = 0.25 };
    private readonly Slider _offsetSliderX = OffsetSlider(), _offsetSliderY = OffsetSlider(), _offsetSliderZ = OffsetSlider();
    private readonly StackPanel _fitPanel = new() { Margin = new Thickness(16) };
    private FitRotation _preparedRotation;
    private ModelPreparationAdjustments _preparedAdjustments = new();
    private bool _syncingRotation, _loadingFit;
    private FitGizmoMode _fitGizmoMode = FitGizmoMode.Move;
    private readonly CheckBox _showCharacter = new() { Content = "Персонаж" };
    private readonly ComboBox _characterGender = new() { MinWidth = 112 };
    private readonly ComboBox _characterPose = new() { MinWidth = 155 };
    private CancellationTokenSource? _characterCancellation;
    private int _characterGeneration;
    private readonly Dictionary<string, CharacterPose> _characterCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBox _offsetX = new() { Text = "0" };
    private readonly TextBox _offsetY = new() { Text = "0" };
    private readonly TextBox _offsetZ = new() { Text = "0" };
    private readonly TextBox _gripFraction = new() { Text = "0.11" };
    private readonly CheckBox _reverseAxis = new() { Content = "Поміняти місцями основу й вістря джерельної моделі" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _run = new() { Content = "Підготувати два NIF", MinWidth = 170 };
    private readonly Button _cancel = new() { Content = "Скасувати", IsEnabled = false, Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _compare = new() { Content = "Порівняти результат", IsEnabled = false,
        Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _save = new() { Content = "Зберегти комплект", IsEnabled = false,
        Margin = new Thickness(8, 0, 0, 0) };
    private ModelPreparationDraft? _draft;
    private string? _resultFolder;
    public string? PublishedFolder { get; private set; }
    private string _lastPreparedFolder = "";
    private string? _thirdPersonPath;
    private string? _firstPersonPath;
    private WeaponTemplateDefinition? _preparedDefinition;
    private CancellationTokenSource? _cancellation;
    private bool _closed;
    private int _reflectionGeneration;

    public ModelPreparationWindow()
    {
        Title = "Редактор NIF — зброя Skyrim";
        Width = 1420;
        Height = 820;
        MinWidth = 1000;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(29, 34, 43));
        Foreground = Brushes.White;
        FontSize = 14;
        var textStyle = new Style(typeof(TextBox));
        textStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(44, 53, 66))));
        textStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        textStyle.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(101, 118, 138))));
        textStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 4, 6, 4)));
        Resources.Add(typeof(TextBox), textStyle);
        var buttonStyle = new Style(typeof(Button));
        buttonStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(58, 69, 83))));
        buttonStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        buttonStyle.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(113, 131, 152))));
        buttonStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 5)));
        var buttonTemplate = new ControlTemplate(typeof(Button));
        var buttonBorder = new FrameworkElementFactory(typeof(Border));
        buttonBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        buttonBorder.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        buttonBorder.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        buttonBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        var buttonContent = new FrameworkElementFactory(typeof(ContentPresenter));
        buttonContent.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        buttonContent.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        buttonContent.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(Control.PaddingProperty));
        buttonBorder.AppendChild(buttonContent);
        buttonTemplate.VisualTree = buttonBorder;
        buttonStyle.Setters.Add(new Setter(Control.TemplateProperty, buttonTemplate));
        var disabledButton = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabledButton.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(43, 51, 62))));
        disabledButton.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(145, 156, 170))));
        disabledButton.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(65, 77, 91))));
        buttonStyle.Triggers.Add(disabledButton);
        Resources.Add(typeof(Button), buttonStyle);
        var labelStyle = new Style(typeof(TextBlock));
        labelStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brushes.White));
        Resources.Add(typeof(TextBlock), labelStyle);
        var checkStyle = new Style(typeof(CheckBox));
        checkStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        Resources.Add(typeof(CheckBox), checkStyle);
        var preferences = ModelPreparationPreferencesStore.Load();
        _blender.Text = preferences.Blender.Length > 0 ? preferences.Blender :
            @"C:\Program Files (x86)\Steam\steamapps\common\Blender\blender.exe";
        _gameData.Text = preferences.GameData.Length > 0 ? preferences.GameData :
            @"E:\SteamLibrary\steamapps\common\Skyrim Special Edition\Data";
        _addon.Text = preferences.Addon.Length > 0 ? preferences.Addon :
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "skyrim-pynifly-V29.1.0");
        _texconv.Text = preferences.Texconv.Length > 0 ? preferences.Texconv :
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "skyrim-texconv-may2026.exe");
        _output.Text = preferences.Output.Length > 0 ? preferences.Output :
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Mods");
        _archive.Text = preferences.Archive;
        _scabbard.IsChecked = preferences.IncludeScabbard;
        _invertNormalY.IsChecked = preferences.InvertNormalY;
        _defaultShader.IsChecked = preferences.DefaultShader;
        _useVertexColor.IsChecked = preferences.UseVertexColor;
        _vertexColor.Value = double.IsFinite(preferences.VertexColor) ? Math.Clamp(preferences.VertexColor, 0, 100) : 100;
        static double ClampPreference(double value, double max) => double.IsFinite(value) ? Math.Clamp(value, 0, max) : 1;
        _useEnvironmentStrength.IsChecked = preferences.UseEnvironmentStrength;
        _environmentStrength.Value = ClampPreference(preferences.EnvironmentStrength, 4);
        _useSpecular.IsChecked = preferences.UseSpecular;
        _specularStrength.Value = ClampPreference(preferences.SpecularStrength, 4);
        _specularColor.SelectedColor = Color.FromRgb((byte)Math.Round(ClampPreference(preferences.SpecularRed, 1) * 255),
            (byte)Math.Round(ClampPreference(preferences.SpecularGreen, 1) * 255), (byte)Math.Round(ClampPreference(preferences.SpecularBlue, 1) * 255));
        if (Directory.Exists(preferences.LastPreparedFolder))
        { _resultFolder = preferences.LastPreparedFolder; _lastPreparedFolder = preferences.LastPreparedFolder; }
        var sourcePanel = new StackPanel { Margin = new Thickness(16) };
        var materialPanel = new StackPanel { Margin = new Thickness(16) };
        var toolsPanel = new StackPanel { Margin = new Thickness(16) };
        var fitPanel = _fitPanel;
        var panel = sourcePanel;
        foreach (var weapon in WeaponTemplateCatalog.All.Where(item => item.ExportEnabled))
            _weaponType.Items.Add(weapon);
        _weaponType.SelectedIndex = 0;
        _weaponType.SelectionChanged += (_, _) =>
        {
            var supportsScabbard = (_weaponType.SelectedItem as WeaponTemplateDefinition)?.SupportsScabbard == true;
            _scabbard.IsEnabled = supportsScabbard;
            if (!supportsScabbard) _scabbard.IsChecked = false;
            if (_draft != null) { _save.IsEnabled = false; _status.Text = "Тип зброї змінено. Підготуйте NIF повторно."; }
            if (_showCharacter.IsChecked == true && (_characterPose.SelectedItem as ComboBoxItem)?.Tag as string == "sheathed")
                _ = RefreshCharacterAsync();
        };
        panel.Children.Add(new TextBlock { Text = "Тип зброї", Margin = new Thickness(0, 0, 0, 4) });
        _weaponType.Margin = new Thickness(0, 0, 0, 12);
        panel.Children.Add(_weaponType);
        AddPath(panel, "ZIP із моделлю", _archive, false, "ZIP (*.zip)|*.zip");
        _archive.LostFocus += (_, _) => { if (File.Exists(_archive.Text.Trim())) SuggestTextures(); };
        panel.Children.Add(new TextBlock { Text = "Модель усередині ZIP (для архіву з кількома файлами)",
            Margin = new Thickness(0, 0, 0, 4) });
        var modelPicker = new StackPanel { Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 12) };
        modelPicker.Children.Add(_modelEntry);
        var listModels = new Button { Content = "Знайти моделі",
            Margin = new Thickness(8, 0, 0, 0) };
        listModels.Click += (_, _) =>
        {
            try
            {
                using var zip = ZipFile.OpenRead(_archive.Text.Trim());
                var entries = zip.Entries.Where(item => !string.IsNullOrEmpty(item.Name) &&
                    new[] { ".blend", ".fbx", ".obj", ".glb", ".gltf", ".nif" }
                        .Contains(System.IO.Path.GetExtension(item.Name).ToLowerInvariant()))
                    .Select(item => item.FullName).ToArray();
                _modelEntry.Items.Clear();
                foreach (var name in entries) _modelEntry.Items.Add(name);
                _modelEntry.Text = entries.Length == 1 ? entries[0] : "";
                _status.Text = entries.Length == 1 ? "Знайдено одну модель: " + entries[0] :
                    "Знайдено моделей: " + entries.Length + ". Оберіть потрібну зі списку.";
            }
            catch (Exception error) { _status.Text = "Помилка читання ZIP: " + error.Message; }
        };
        modelPicker.Children.Add(listModels);
        panel.Children.Add(modelPicker);
        _scabbard.Margin = new Thickness(0, 6, 0, 8);
        panel.Children.Add(_scabbard);
        panel = toolsPanel;
        AddPath(panel, "Папка Data встановленого Skyrim SE/AE", _gameData, true);
        AddPath(panel, "Blender executable", _blender, false, "Executable (*.exe)|*.exe");
        AddPath(panel, "Папка, що містить io_scene_nifly", _addon, true);
        AddPath(panel, "Вихідна папка поза грою", _output, true);
        AddPath(panel, "texconv.exe", _texconv, false, "Executable (*.exe)|*.exe");
        panel = materialPanel;
        _defaultShader.Margin = new Thickness(0, 0, 0, 6);
        panel.Children.Add(_defaultShader);
        panel.Children.Add(new TextBlock { Text = "Default: без Metallic і cubemap.", Margin = new Thickness(0, 0, 0, 6) });
        void UpdateShaderControls()
        {
            var environmentEnabled = _defaultShader.IsChecked != true;
            _metallicEntry.IsEnabled = environmentEnabled;
            _cubemapEntry.IsEnabled = environmentEnabled;
            _gameCubemap.IsEnabled = environmentEnabled;
            _useEnvironmentStrength.IsEnabled = environmentEnabled;
            _environmentStrength.IsEnabled = environmentEnabled && _useEnvironmentStrength.IsChecked == true;
            if (!environmentEnabled)
            {
                ++_reflectionGeneration;
                _preview.SetReflectionPreview(null);
                _previewStatus.Text = "Default: Metallic і cubemap не потрібні та не використовуються.";
            }
            else _ = UpdateReflectionPreviewAsync(false);
            if (_draft != null) { _save.IsEnabled = false; _status.Text = "Шейдер змінено. Підготуйте NIF повторно."; }
        }
        _defaultShader.Checked += (_, _) => UpdateShaderControls();
        _defaultShader.Unchecked += (_, _) => UpdateShaderControls();
        UpdateShaderControls();
        var shaderValues = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 6) };
        panel.Children.Add(_useEnvironmentStrength);
        panel.Children.Add(_environmentStrength);
        panel.Children.Add(_useSpecular);
        panel.Children.Add(new TextBlock { Text = "Сила відблиску" });
        panel.Children.Add(_specularStrength);
        panel.Children.Add(new TextBlock { Text = "Колір відблиску" });
        panel.Children.Add(_specularColor);
        panel.Children.Add(shaderValues);
        panel.Children.Add(new TextBlock { Text = "Змінили матеріал? Підготуйте NIF знову.", Margin = new Thickness(0, 0, 0, 8) });
        void UpdateShaderValues()
        {
            _environmentStrength.IsEnabled = _defaultShader.IsChecked != true && _useEnvironmentStrength.IsChecked == true;
            _specularStrength.IsEnabled = _useSpecular.IsChecked == true;
            _specularColor.IsEnabled = _useSpecular.IsChecked == true;
            var color = _specularColor.SelectedColor;
            shaderValues.Text = $"Environment: {_environmentStrength.Value:0.00}   Specular: {_specularStrength.Value:0.00}   #{color.R:X2}{color.G:X2}{color.B:X2}";
            _preview.SetShaderPreview(_useEnvironmentStrength.IsChecked == true ? _environmentStrength.Value : 1,
                _useSpecular.IsChecked == true ? color : null, _useSpecular.IsChecked == true ? _specularStrength.Value : 0);
            if (_draft != null) { _save.IsEnabled = false; _status.Text = "Параметри шейдера змінено. Підготуйте NIF повторно."; }
        }
        _useEnvironmentStrength.Checked += (_, _) => UpdateShaderValues();
        _useEnvironmentStrength.Unchecked += (_, _) => UpdateShaderValues();
        _useSpecular.Checked += (_, _) => UpdateShaderValues();
        _useSpecular.Unchecked += (_, _) => UpdateShaderValues();
        foreach (var slider in new[] { _environmentStrength, _specularStrength })
            slider.ValueChanged += (_, _) => UpdateShaderValues();
        _specularColor.ColorChanged += _ => UpdateShaderValues();
        UpdateShaderValues();
        panel.Children.Add(_useVertexColor);
        panel.Children.Add(new TextBlock { Text = "Яскравість: чорний — білий", Margin = new Thickness(0, 4, 0, 2) });
        panel.Children.Add(_vertexColor);
        panel.Children.Add(_vertexColorValue);
        void UpdateVertexColor()
        {
            var enabled = _useVertexColor.IsChecked == true;
            _vertexColor.IsEnabled = enabled;
            _vertexColorValue.Text = $"{_vertexColor.Value:0}%";
            _preview.SetVertexColorPreview(enabled ? _vertexColor.Value / 100 : null);
            if (_draft != null) { _save.IsEnabled = false; _status.Text = "Vertex Color змінено. Підготуйте NIF повторно."; }
        }
        _useVertexColor.Checked += (_, _) => UpdateVertexColor();
        _useVertexColor.Unchecked += (_, _) => UpdateVertexColor();
        _vertexColor.ValueChanged += (_, _) => UpdateVertexColor();
        UpdateVertexColor();
        _useTextures.Margin = new Thickness(0, 8, 0, 6);
        panel.Children.Add(_useTextures);
        panel.Children.Add(new TextBlock { Text = "Максимальний розмір текстур" });
        foreach (var (label, size) in new[] { ("Без зменшення", 0), ("4096 px", 4096), ("2048 px", 2048),
                     ("1024 px", 1024), ("512 px", 512), ("256 px", 256) })
            _textureSizeLimit.Items.Add(new ComboBoxItem { Content = label, Tag = size });
        _textureSizeLimit.SelectedItem = _textureSizeLimit.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => (int)item.Tag == preferences.MaxTextureSize) ?? (ComboBoxItem)_textureSizeLimit.Items[0];
        panel.Children.Add(_textureSizeLimit);
        panel.Children.Add(_textureSizeReport);
        panel.Children.Add(new TextBlock { Text = "Оцінка BC7 з mipmap; cubemap не змінюється.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6), Foreground = Brushes.LightGray });
        AddTexturePicker(panel, "Diffuse / BaseColor", _diffuseEntry, false);
        AddTexturePicker(panel, "Normal", _normalEntry, false);
        AddTexturePicker(panel, "Metallic / маска відбиття (необов’язково)", _metallicEntry, false);
        panel.Children.Add(new TextBlock { Text = "Кубічна карта з гри", Margin = new Thickness(0, 6, 0, 0) });
        foreach (var cubemap in GameCubemapCatalog.Choices) _gameCubemap.Items.Add(cubemap);
        _gameCubemap.SelectedItem = GameCubemapCatalog.Choices.FirstOrDefault(item =>
            item.Path.Equals(preferences.GameCubemap, StringComparison.OrdinalIgnoreCase)) ??
            GameCubemapCatalog.Choices[0];
        panel.Children.Add(_gameCubemap);
        AddTexturePicker(panel, "Власний cubemap DDS (необов’язково)", _cubemapEntry, true);
        panel.Children.Add(new TextBlock { Text = "Власний cubemap має пріоритет.", Margin = new Thickness(0, 5, 0, 5) });
        var advancedMaterial = new StackPanel { Margin = new Thickness(8, 8, 8, 10) };
        var advancedExpander = new Expander { Header = "Додатково: кілька матеріалів і Normal",
            IsExpanded = false, Content = advancedMaterial, Margin = new Thickness(0, 8, 0, 0),
            Foreground = Brushes.White };
        panel.Children.Add(advancedExpander);
        advancedMaterial.Children.Add(new TextBlock { Text = "Слот | BaseColor | Normal | Metallic | cubemap (два останні необов’язкові; слоти від 0)",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 3) });
        advancedMaterial.Children.Add(_materialEntries);
        var slotRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 4) };
        slotRow.Children.Add(new TextBlock { Text = "Слот", VerticalAlignment = VerticalAlignment.Center });
        var slotNumber = new TextBox { Text = "0", Width = 45, Margin = new Thickness(6, 0, 6, 0) };
        slotRow.Children.Add(slotNumber);
        var addSlot = new Button { Content = "Додати вибрані текстури" };
        addSlot.Click += (_, _) =>
        {
            try
            {
                if (!int.TryParse(slotNumber.Text, out var slot) || slot < 0 ||
                    string.IsNullOrWhiteSpace(_diffuseEntry.Text) || string.IsNullOrWhiteSpace(_normalEntry.Text))
                    throw new FormatException("Вкажіть номер слота, BaseColor і Normal.");
                if (ReadMaterialEntries()?.Any(item => item.Slot == slot) == true)
                    throw new FormatException("Цей слот уже додано. Відредагуйте його у списку.");
                var line = string.Join(" | ", slot.ToString(), _diffuseEntry.Text.Trim(), _normalEntry.Text.Trim(),
                    _defaultShader.IsChecked == true ? "" : _metallicEntry.Text.Trim(),
                    _defaultShader.IsChecked == true ? "" : _cubemapEntry.Text.Trim());
                _materialEntries.Text = string.IsNullOrWhiteSpace(_materialEntries.Text) ? line : _materialEntries.Text.TrimEnd() + Environment.NewLine + line;
                slotNumber.Text = (slot + 1).ToString();
            }
            catch (FormatException error) { _materialAudit.Text = error.Message; }
        };
        slotRow.Children.Add(addSlot);
        advancedMaterial.Children.Add(slotRow);
        var inspectMaterials = new Button { Content = "Переглянути матеріальні слоти ZIP",
            Margin = new Thickness(0, 6, 0, 5) };
        inspectMaterials.Click += async (_, _) =>
        {
            inspectMaterials.IsEnabled = false;
            _materialAudit.Text = "Читаю матеріали моделі…";
            try
            {
                var report = await ModelPreparationRunner.InspectSourceMaterialsAsync(
                    _archive.Text.Trim(), _blender.Text.Trim(), _addon.Text.Trim(),
                    modelEntry: _modelEntry.Text.Trim());
                using var document = JsonDocument.Parse(report);
                var meshes = document.RootElement.GetProperty("meshes").EnumerateArray().ToArray();
                var selectedMesh = document.RootElement.GetProperty("selected_mesh").GetString();
                var mesh = meshes.First(item => item.GetProperty("name").GetString() == selectedMesh);
                var slots = mesh.GetProperty("materials").EnumerateArray()
                    .Select((item, index) => index + ": " +
                        (item.GetProperty("name").GetString() ?? "без назви")).ToArray();
                var applied = mesh.GetProperty("applied_modifiers").EnumerateArray()
                    .Select(item => item.GetString()).ToArray();
                _materialAudit.Text = "Mesh: " + mesh.GetProperty("name").GetString() +
                    "; UV: " + mesh.GetProperty("uv_layers").GetInt32() +
                    "; слоти: " + (slots.Length == 0 ? "немає" : string.Join("; ", slots)) +
                    "; застосовані модифікатори: " + (applied.Length == 0 ? "немає" :
                        string.Join(", ", applied)) +
                    ". Зображення ZIP: " + string.Join(", ", document.RootElement.GetProperty("images")
                        .EnumerateArray().Select(item => item.GetProperty("name").GetString()));
            }
            catch (Exception error) { _materialAudit.Text = "Помилка аудиту: " + error.Message; }
            finally { inspectMaterials.IsEnabled = true; }
        };
        advancedMaterial.Children.Add(inspectMaterials);
        advancedMaterial.Children.Add(_materialAudit);
        var suggestTextures = new Button { Content = "Знайти текстури у ZIP",
            Margin = new Thickness(0, 0, 0, 6) };
        suggestTextures.Click += (_, _) => SuggestTextures();
        advancedMaterial.Children.Add(suggestTextures);
        advancedMaterial.Children.Add(_invertNormalY);
        panel = fitPanel;
        var gizmoControls = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var (caption, mode) in new[] { ("Перемістити", FitGizmoMode.Move), ("Повернути", FitGizmoMode.Rotate), ("Масштаб", FitGizmoMode.Scale), ("Приховати", FitGizmoMode.None) })
        {
            var choice = new RadioButton { Content = caption, GroupName = "FitGizmo", Margin = new Thickness(0, 0, 10, 4),
                Foreground = Brushes.White, IsChecked = mode == _fitGizmoMode };
            choice.Checked += (_, _) => { _fitGizmoMode = mode; if (_fitPanel.IsVisible && _run.IsEnabled) _preview.SetFitGizmoMode(mode); };
            gizmoControls.Children.Add(choice);
        }
        panel.Children.Add(gizmoControls);
        panel.Children.Add(new TextBlock { Text = "Тягніть стрілку; масштаб — жовте коло. Поза осями обертається камера.", TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(new TextBlock { Text = "Коригування після автоматичної підгонки",
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 6) });
        panel.Children.Add(new TextBlock { Text = "Хват: 0–1 · Поворот: градуси · Зсув: NIF-одиниці",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var numbers = new WrapPanel();
        AddNumber(numbers, "Точка хвата*", _gripFraction);
        panel.Children.Add(numbers);
        AddRotationRow(panel, "×", _scale, _scaleSlider);
        panel.Children.Add(new TextBlock { Text = "Поворот моделі · градуси", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 3) });
        AddRotationRow(panel, "X", _rotationX, _rotationSliderX);
        AddRotationRow(panel, "Y", _rotationY, _rotationSliderY);
        AddRotationRow(panel, "Z", _rotationZ, _rotationSliderZ);
        panel.Children.Add(new TextBlock { Text = "Зсув · NIF-одиниці", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 3) });
        AddRotationRow(panel, "X", _offsetX, _offsetSliderX);
        AddRotationRow(panel, "Y", _offsetY, _offsetSliderY);
        AddRotationRow(panel, "Z", _offsetZ, _offsetSliderZ);
        panel.Children.Add(new TextBlock { Text = "* Хват і напрям осі: після повторної підготовки NIF.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8) });
        _gripFraction.TextChanged += (_, _) => InvalidateFitDraft();
        _reverseAxis.Checked += (_, _) => InvalidateFitDraft();
        _reverseAxis.Unchecked += (_, _) => InvalidateFitDraft();
        panel.Children.Add(_reverseAxis);
        var reset = new Button { Content = "Скинути коригування", Margin = new Thickness(0, 3, 0, 12) };
        reset.Click += (_, _) =>
        {
            _scale.Text = "1";
            _gripFraction.Text = "0.11";
            _reverseAxis.IsChecked = false;
            foreach (var field in new[] { _rotationX, _rotationY, _rotationZ, _offsetX, _offsetY, _offsetZ })
                field.Text = "0";
        };
        panel.Children.Add(reset);
        var actions = new Grid { Margin = new Thickness(10, 8, 10, 8) };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        actions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _run.Click += OnRun;
        _cancel.Click += (_, _) => _cancellation?.Cancel();
        _compare.Click += (_, _) =>
        {
            var folder = _draft?.StagingDirectory ?? _resultFolder;
            if (folder != null && _thirdPersonPath != null && _firstPersonPath != null)
                new ModelPreparationComparisonWindow(folder, _gameData.Text.Trim(),
                    _thirdPersonPath, _firstPersonPath,
                    _preparedDefinition!.ThirdPerson.Source,
                    _preparedDefinition.FirstPerson.Source)
                    { Owner = this }.ShowDialog();
        };
        _save.Click += async (_, _) =>
        {
            if (_draft == null) return;
            try
            {
                _resultFolder = _draft.Publish();
                PublishedFolder = _resultFolder;
                _lastPreparedFolder = _resultFolder;
                _draft.Dispose();
                _draft = null;
                _save.IsEnabled = false;
                if (_thirdPersonPath != null && _firstPersonPath != null)
                    await _preview.LoadAsync(new ModelPreviewRequest(_resultFolder,
                        _thirdPersonPath, _firstPersonPath, _gameData.Text.Trim()), preserveView: true);
                await UpdateReflectionPreviewAsync(false);
                SavePreferences();
                _status.Text = "Збережено комплект: " + _resultFolder +
                    "\nПеревірка поведінки у Skyrim ще не проведена.";
                Close();
            }
            catch (Exception error) { _status.Text = "Помилка збереження: " + error.Message; }
        };
        _run.Margin = new Thickness(0, 0, 4, 6);
        _save.Margin = new Thickness(4, 0, 0, 6);
        _compare.Margin = new Thickness(0, 0, 4, 0);
        _cancel.Margin = new Thickness(4, 0, 0, 0);
        Grid.SetColumn(_save, 1);
        Grid.SetRow(_compare, 1);
        Grid.SetRow(_cancel, 1);
        Grid.SetColumn(_cancel, 1);
        actions.Children.Add(_run);
        actions.Children.Add(_save);
        actions.Children.Add(_compare);
        actions.Children.Add(_cancel);
        _status.Margin = new Thickness(14, 2, 14, 12);
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(465) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var sidebar = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(25, 30, 39)) };
        var title = new TextBlock { Text = "NIF  /  ЗБРОЯ", FontSize = 18,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(16, 16, 16, 8) };
        DockPanel.SetDock(title, Dock.Top); sidebar.Children.Add(title);
        var footer = new StackPanel(); footer.Children.Add(actions); footer.Children.Add(_status);
        DockPanel.SetDock(footer, Dock.Bottom); sidebar.Children.Add(footer);
        var tabs = new TabControl { Background = new SolidColorBrush(Color.FromRgb(29, 34, 43)),
            Foreground = Brushes.White, BorderThickness = new Thickness(0) };
        foreach (var (name, content) in new[]
                 { ("Модель", sourcePanel), ("Матеріал", materialPanel),
                   ("Підгонка", fitPanel), ("Шляхи", toolsPanel) })
            tabs.Items.Add(new TabItem { Header = name, Width = 112, Height = 36,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromRgb(43, 52, 66)),
                Content = new ScrollViewer { Content = content,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        tabs.SelectedIndex = 0;
        tabs.SelectionChanged += (_, e) =>
        {
            if (e.Source == tabs) _preview.SetFitGizmoMode(tabs.SelectedIndex == 2 && _run.IsEnabled ? _fitGizmoMode : FitGizmoMode.None);
        };
        sidebar.Children.Add(tabs);
        layout.Children.Add(sidebar);
        var previewPanel = new DockPanel { Margin = new Thickness(12, 16, 16, 16) };
        var heading = new TextBlock { Text = "3D  /  ПЕРЕГЛЯД", FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(4, 0, 4, 6) };
        DockPanel.SetDock(heading, Dock.Top); previewPanel.Children.Add(heading);
        var characterRow = new WrapPanel { Margin = new Thickness(4, 0, 4, 6) };
        _characterGender.Items.Add(new ComboBoxItem { Content = "Чоловік", Tag = "male" });
        _characterGender.Items.Add(new ComboBoxItem { Content = "Жінка", Tag = "female" });
        _characterGender.SelectedIndex = 0;
        _characterPose.Items.Add(new ComboBoxItem { Content = "Одноручна", Tag = "1hm_idle" });
        _characterPose.Items.Add(new ComboBoxItem { Content = "Дворучна", Tag = "2hm_idle" });
        _characterPose.Items.Add(new ComboBoxItem { Content = "Скрадання", Tag = "sneak1hm_idle" });
        _characterPose.Items.Add(new ComboBoxItem { Content = "Прибрана зброя", Tag = "sheathed" });
        _characterPose.SelectedIndex = 0;
        _characterGender.Margin = new Thickness(8, 0, 0, 0); _characterPose.Margin = new Thickness(8, 0, 0, 0);
        _characterGender.IsEnabled = _characterPose.IsEnabled = false;
        characterRow.Children.Add(_showCharacter); characterRow.Children.Add(_characterGender); characterRow.Children.Add(_characterPose);
        DockPanel.SetDock(characterRow, Dock.Top); previewPanel.Children.Add(characterRow);
        _showCharacter.Checked += (_, _) => _ = RefreshCharacterAsync();
        _showCharacter.Unchecked += (_, _) => _ = RefreshCharacterAsync();
        _characterGender.SelectionChanged += (_, _) => { if (_showCharacter.IsChecked == true) _ = RefreshCharacterAsync(); };
        _characterPose.SelectionChanged += (_, _) => { if (_showCharacter.IsChecked == true) _ = RefreshCharacterAsync(); };
        var note = new TextBlock { Text = "3D-перегляд наближений.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 4, 8) };
        DockPanel.SetDock(note, Dock.Top); previewPanel.Children.Add(note);
        _previewStatus.Margin = new Thickness(4, 3, 4, 8);
        DockPanel.SetDock(_previewStatus, Dock.Bottom); previewPanel.Children.Add(_previewStatus);
        _preview.SetCompact(true);
        _preview.FitAdjustmentRequested += ApplyGizmoAdjustment;
        previewPanel.Children.Add(_preview);
        Grid.SetColumn(previewPanel, 1); layout.Children.Add(previewPanel);
        Content = layout;
        _gameCubemap.SelectionChanged += (_, _) => _ = UpdateReflectionPreviewAsync(true);
        _cubemapEntry.TextChanged += (_, _) => _ = UpdateReflectionPreviewAsync(true);
        _metallicEntry.TextChanged += (_, _) => _ = UpdateReflectionPreviewAsync(true);
        foreach (var input in new[] { _archive, _diffuseEntry, _normalEntry, _metallicEntry, _materialEntries })
            input.TextChanged += (_, _) => TextureInputsChanged();
        _textureSizeLimit.SelectionChanged += (_, _) => TextureInputsChanged();
        _defaultShader.Checked += (_, _) => TextureInputsChanged();
        _defaultShader.Unchecked += (_, _) => TextureInputsChanged();
        _useTextures.Checked += (_, _) => TextureInputsChanged();
        _useTextures.Unchecked += (_, _) => TextureInputsChanged();
        TextureInputsChanged();
        Loaded += async (_, _) => await LoadPreviousPreviewAsync();
    }

    private static void AddNumber(Panel panel, string label, TextBox box)
    {
        var group = new StackPanel { Width = 145, Margin = new Thickness(0, 0, 12, 9) };
        group.Children.Add(new TextBlock { Text = label });
        group.Children.Add(box);
        panel.Children.Add(group);
    }

    private int SelectedTextureSize => (_textureSizeLimit.SelectedItem as ComboBoxItem)?.Tag is int size ? size : 0;

    private void TextureInputsChanged()
    {
        if (_closed) return;
        if (_draft != null) { _save.IsEnabled = false; _status.Text = "Текстури змінено. Підготуйте NIF повторно."; }
        _ = UpdateTextureEstimatesAsync();
    }

    private async Task UpdateTextureEstimatesAsync()
    {
        var generation = ++_textureSizeGeneration;
        var archive = _archive.Text.Trim(); var max = SelectedTextureSize;
        var includeMetallic = _defaultShader.IsChecked != true;
        var sources = new List<(string Label, string Path)>();
        try
        {
            var materials = ReadMaterialEntries();
            if (materials is { Count: > 0 })
            {
                foreach (var item in materials.OrderBy(item => item.Slot))
                {
                    sources.Add(($"{item.Slot}: BaseColor", item.DiffuseEntry));
                    sources.Add(($"{item.Slot}: Normal", item.NormalEntry));
                    if (includeMetallic && !string.IsNullOrWhiteSpace(item.MetallicEntry)) sources.Add(($"{item.Slot}: Metallic", item.MetallicEntry));
                }
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(_diffuseEntry.Text)) sources.Add(("BaseColor", _diffuseEntry.Text.Trim()));
                if (!string.IsNullOrWhiteSpace(_normalEntry.Text)) sources.Add(("Normal", _normalEntry.Text.Trim()));
                if (includeMetallic && !string.IsNullOrWhiteSpace(_metallicEntry.Text)) sources.Add(("Metallic", _metallicEntry.Text.Trim()));
            }
        }
        catch (FormatException) { _textureSizeReport.Text = "Перевірте записи матеріальних слотів."; return; }
        if (sources.Count == 0) { _textureSizeReport.Text = "Виберіть текстури для оцінки."; return; }
        _textureSizeReport.Text = "Оцінка текстур…";
        await Task.Delay(350);
        if (_closed || generation != _textureSizeGeneration) return;
        var lines = await Task.Run(() =>
        {
            var rows = new List<string>(); long total = 0; var missing = 0;
            foreach (var source in sources)
            {
                try
                {
                    var estimate = TextureSizePlanner.Inspect(archive, source.Path, max);
                    total += estimate.Bc7Bytes;
                    rows.Add($"{source.Label}: {estimate.SourceWidth}×{estimate.SourceHeight} → {estimate.OutputWidth}×{estimate.OutputHeight} · ≈ {estimate.Bc7Bytes / 1048576.0:0.##} МіБ");
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or OverflowException or InvalidOperationException or NotSupportedException)
                { missing++; rows.Add(source.Label + ": " + error.Message); }
            }
            rows.Add($"Разом{(missing > 0 ? " (лише доступні)" : "")}: ≈ {total / 1048576.0:0.##} МіБ");
            return string.Join(Environment.NewLine, rows);
        });
        if (!_closed && generation == _textureSizeGeneration) _textureSizeReport.Text = lines;
    }

    private void ApplyGizmoAdjustment(ModelPreparationAdjustments value)
    {
        _syncingRotation = true;
        try
        {
            void Set(TextBox field, Slider slider, double number)
            {
                field.Text = number.ToString("0.##", CultureInfo.CurrentCulture);
                slider.Value = number;
            }
            Set(_scale, _scaleSlider, value.ScaleMultiplier);
            Set(_rotationX, _rotationSliderX, value.RotationXDegrees);
            Set(_rotationY, _rotationSliderY, value.RotationYDegrees);
            Set(_rotationZ, _rotationSliderZ, value.RotationZDegrees);
            Set(_offsetX, _offsetSliderX, value.OffsetX);
            Set(_offsetY, _offsetSliderY, value.OffsetY);
            Set(_offsetZ, _offsetSliderZ, value.OffsetZ);
        }
        finally { _syncingRotation = false; }
        UpdateFitRotation();
    }

    private static Slider RotationSlider() => new() { Minimum = -180, Maximum = 180, TickFrequency = 1,
        IsSnapToTickEnabled = true, SmallChange = 1, LargeChange = 15, VerticalAlignment = VerticalAlignment.Center };
    private static Slider OffsetSlider() => new() { Minimum = -100, Maximum = 100, TickFrequency = 0.5,
        IsSnapToTickEnabled = true, SmallChange = 0.5, LargeChange = 5, VerticalAlignment = VerticalAlignment.Center };

    private void AddRotationRow(Panel panel, string axis, TextBox field, Slider slider)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
        row.Children.Add(new TextBlock { Text = axis, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(slider, 1); row.Children.Add(slider);
        field.Margin = new Thickness(8, 0, 0, 0); Grid.SetColumn(field, 2); row.Children.Add(field);
        slider.ValueChanged += (_, _) =>
        {
            if (_syncingRotation) return;
            _syncingRotation = true;
            try { field.Text = slider.Value.ToString("0.##", CultureInfo.CurrentCulture); }
            finally { _syncingRotation = false; }
            UpdateFitRotation();
        };
        field.TextChanged += (_, _) =>
        {
            if (_syncingRotation) return;
            if (TrySliderValue(field, slider, out var value))
            {
                _syncingRotation = true;
                try { slider.Value = value; }
                finally { _syncingRotation = false; }
            }
            UpdateFitRotation();
        };
        panel.Children.Add(row);
    }

    private static bool TrySliderValue(TextBox field, Slider slider, out double value) =>
        (double.TryParse(field.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
         double.TryParse(field.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) &&
        double.IsFinite(value) && value >= slider.Minimum && value <= slider.Maximum;

    private void UpdateFitRotation()
    {
        InvalidateFitDraft();
    }

    private async Task RefreshCharacterAsync()
    {
        var generation = ++_characterGeneration;
        _characterCancellation?.Cancel(); _characterCancellation?.Dispose(); _characterCancellation = null;
        var enabled = _showCharacter.IsChecked == true;
        _characterGender.IsEnabled = _characterPose.IsEnabled = enabled;
        if (!enabled) { _preview.SetCharacterPose(null); _previewStatus.Text = ""; return; }
        var gender = ((ComboBoxItem)_characterGender.SelectedItem).Tag.ToString()!;
        var pose = ((ComboBoxItem)_characterPose.SelectedItem).Tag.ToString()!;
        var weaponType = (_weaponType.SelectedItem as WeaponTemplateDefinition)?.Id ?? "sword1h";
        var data = _gameData.Text.Trim(); var blender = _blender.Text.Trim(); var addon = _addon.Text.Trim();
        var key = string.Join("|", data, blender, addon, gender, pose, pose == "sheathed" ? weaponType : "hand");
        _preview.SetCharacterPose(null);
        _previewStatus.Text = "Завантаження персонажа зі Skyrim…";
        _characterCancellation = new CancellationTokenSource();
        var token = _characterCancellation.Token;
        try
        {
            if (!_characterCache.TryGetValue(key, out var character))
            {
                character = await CharacterPreviewService.LoadAsync(data, blender, addon, gender, pose, token, weaponType: weaponType);
                if (_closed || token.IsCancellationRequested || generation != _characterGeneration) return;
                _characterCache[key] = character;
            }
            if (_closed || token.IsCancellationRequested || generation != _characterGeneration) return;
            _preview.SetCharacterPose(character);
            _preview.SetView(new PreviewViewState(0, -5, 3.3));
            _previewStatus.Text = pose == "sheathed"
                ? "Зброя прибрана · положення наближене."
                : "Ігрова поза · попередній перегляд хвата наближений.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!_closed && generation == _characterGeneration)
                _previewStatus.Text = "Не вдалося завантажити персонажа: " + error.Message;
        }
    }

    private void InvalidateFitDraft()
    {
        if (!_loadingFit)
        {
            try { var values = ReadAdjustments(); values.Validate(); _preview.SetFitAdjustment(_preparedAdjustments, values); }
            catch (Exception error) when (error is FormatException or ArgumentOutOfRangeException) { }
        }
        if (!_loadingFit && _draft != null)
        {
            _save.IsEnabled = false;
            _status.Text = "Підгонку змінено. Підготуйте NIF повторно.";
        }
    }

    private async Task LoadPreviousPreviewAsync()
    {
        if (File.Exists(_archive.Text)) SuggestTextures();
        if (_resultFolder == null) return;
        try
        {
            using var report = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(_resultFolder, "report.json")));
            var views = report.RootElement.GetProperty("views").EnumerateArray()
                .Select(item => item.GetProperty("output_nif").GetString()).OfType<string>().ToArray();
            if (views.Length < 2) return;
            if (report.RootElement.TryGetProperty("adjustments", out var adjustments))
            {
                _preparedAdjustments = JsonSerializer.Deserialize<ModelPreparationAdjustments>(adjustments.GetRawText()) ?? new();
                _preparedRotation = new FitRotation(adjustments.GetProperty("RotationXDegrees").GetDouble(),
                    adjustments.GetProperty("RotationYDegrees").GetDouble(), adjustments.GetProperty("RotationZDegrees").GetDouble());
                _loadingFit = true;
                try
                {
                    _scale.Text = adjustments.GetProperty("ScaleMultiplier").GetDouble().ToString(CultureInfo.CurrentCulture);
                    _rotationX.Text = _preparedRotation.X.ToString(CultureInfo.CurrentCulture);
                    _rotationY.Text = _preparedRotation.Y.ToString(CultureInfo.CurrentCulture);
                    _rotationZ.Text = _preparedRotation.Z.ToString(CultureInfo.CurrentCulture);
                    _offsetX.Text = adjustments.GetProperty("OffsetX").GetDouble().ToString(CultureInfo.CurrentCulture);
                    _offsetY.Text = adjustments.GetProperty("OffsetY").GetDouble().ToString(CultureInfo.CurrentCulture);
                    _offsetZ.Text = adjustments.GetProperty("OffsetZ").GetDouble().ToString(CultureInfo.CurrentCulture);
                    _gripFraction.Text = adjustments.GetProperty("SourceGripFraction").GetDouble().ToString(CultureInfo.CurrentCulture);
                    _reverseAxis.IsChecked = adjustments.GetProperty("ReverseLongAxis").GetBoolean();
                }
                finally { _loadingFit = false; }
                UpdateFitRotation();
            }
            _thirdPersonPath = views[0]; _firstPersonPath = views[1];
            await _preview.LoadAsync(new ModelPreviewRequest(_resultFolder, views[0], views[1],
                _gameData.Text.Trim()));
            _preview.SetView(new PreviewViewState(90, -10, 3.3));
            await UpdateReflectionPreviewAsync(false);
        }
        catch (Exception error) { _previewStatus.Text = "Попередній перегляд недоступний: " + error.Message; }
    }

    private void SavePreferences()
    {
        try
        {
            ModelPreparationPreferencesStore.Save(new ModelPreparationPreferences(
                _archive.Text.Trim(), _gameData.Text.Trim(), _blender.Text.Trim(),
                _addon.Text.Trim(), _output.Text.Trim(), _texconv.Text.Trim(),
                _lastPreparedFolder, (_gameCubemap.SelectedItem as GameCubemap)?.Path ?? "",
                _scabbard.IsChecked == true, _invertNormalY.IsChecked == true, _defaultShader.IsChecked == true,
                _useVertexColor.IsChecked == true, _vertexColor.Value,
                _useEnvironmentStrength.IsChecked == true, _environmentStrength.Value,
                _useSpecular.IsChecked == true, _specularStrength.Value,
                _specularColor.SelectedColor.R / 255.0, _specularColor.SelectedColor.G / 255.0, _specularColor.SelectedColor.B / 255.0,
                SelectedTextureSize));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { _status.Text = "Не вдалося зберегти шляхи: " + error.Message; }
    }

    private ModelPreparationAdjustments ReadAdjustments()
    {
        static double Number(TextBox box)
        {
            if (double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var value) ||
                double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return value;
            throw new FormatException("Введіть числові значення масштабу, повороту та зсуву.");
        }
        return new ModelPreparationAdjustments(Number(_scale), Number(_rotationX), Number(_rotationY),
            Number(_rotationZ), Number(_offsetX), Number(_offsetY), Number(_offsetZ), Number(_gripFraction),
            _reverseAxis.IsChecked == true);
    }

    private IReadOnlyList<ModelPreparationMaterialTexture>? ReadMaterialEntries()
    {
        if (string.IsNullOrWhiteSpace(_materialEntries.Text)) return null;
        var result = new List<ModelPreparationMaterialTexture>();
        foreach (var line in _materialEntries.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Trim().Split('|', StringSplitOptions.TrimEntries);
            if (fields.Length is < 3 or > 5 || !int.TryParse(fields[0], out var slot) || slot < 0 ||
                string.IsNullOrWhiteSpace(fields[1]) || string.IsNullOrWhiteSpace(fields[2]))
                throw new FormatException("Для кожного матеріалу введіть: слот | BaseColor | Normal | Metallic | cubemap DDS (останні два необов’язкові).");
            if (result.Any(item => item.Slot == slot))
                throw new FormatException("Номер матеріального слота повторюється: " + slot);
            result.Add(new ModelPreparationMaterialTexture(slot, fields[1], fields[2],
                fields.Length >= 4 ? fields[3] : null, fields.Length >= 5 ? fields[4] : null));
        }
        return result;
    }

    private void SuggestTextures()
    {
        try
        {
            using var archive = ZipFile.OpenRead(_archive.Text.Trim());
            var images = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name) &&
                new[] { ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".dds" }
                    .Contains(System.IO.Path.GetExtension(entry.Name).ToLowerInvariant())).ToArray();
            static string? Unique(ZipArchiveEntry[] entries, string[] markers)
            {
                var found = entries.Where(entry => markers.Any(marker =>
                        System.IO.Path.GetFileNameWithoutExtension(entry.Name)
                            .Contains(marker, StringComparison.OrdinalIgnoreCase)))
                    .Select(entry => entry.FullName).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(2).ToArray();
                return found.Length == 1 ? found[0] : null;
            }
            _diffuseEntry.Text = Unique(images, ["basecolor", "albedo", "diffuse"]) ?? "";
            _normalEntry.Text = Unique(images, ["normal", "_n"]) ?? "";
            _metallicEntry.Text = Unique(images, ["metallic", "metalness", "envmask", "_em"]) ?? "";
            _cubemapEntry.Text = Unique(images.Where(entry =>
                System.IO.Path.GetExtension(entry.Name).Equals(".dds", StringComparison.OrdinalIgnoreCase))
                .ToArray(), ["cubemap", "envmap", "_e"]) ?? "";
            if (_diffuseEntry.Text.Length > 0 && _normalEntry.Text.Length > 0)
                _useTextures.IsChecked = true;
            _status.Text = string.IsNullOrWhiteSpace(_diffuseEntry.Text) || string.IsNullOrWhiteSpace(_normalEntry.Text)
                ? "Не вдалося однозначно знайти BaseColor і Normal. Вкажіть точні шляхи файлів у ZIP."
                : "Знайдено BaseColor і Normal" +
                  (string.IsNullOrWhiteSpace(_metallicEntry.Text) ? "" : ", а також Metallic") +
                  ". Перевірте вибір перед підготовкою.";
        }
        catch (Exception error) { _status.Text = "Помилка читання ZIP: " + error.Message; }
    }

    private async Task UpdateReflectionPreviewAsync(bool changedSelection)
    {
        var generation = ++_reflectionGeneration;
        if (_closed) return;
        if (_defaultShader.IsChecked == true)
        {
            _preview.SetReflectionPreview(null);
            return;
        }
        var folder = _draft?.StagingDirectory ?? _resultFolder;
        if (folder == null) return;
        if (changedSelection && _draft != null)
        {
            _save.IsEnabled = false;
            _previewStatus.Text = "Вибір карти змінено. Щоб записати його в NIF, натисніть «Підготувати два NIF» знову.";
        }
        var ownEntry = _cubemapEntry.Text.Trim();
        var gamePath = (_gameCubemap.SelectedItem as GameCubemap)?.Path ?? "";
        var archivePath = _archive.Text.Trim();
        var gameData = _gameData.Text.Trim();
        var selectedMask = _metallicEntry.Text.Trim();
        try
        {
            if (selectedMask.Length == 0 && !string.IsNullOrWhiteSpace(_materialEntries.Text))
                selectedMask = ReadMaterialEntries()?.FirstOrDefault()?.MetallicEntry ?? "";
            await Task.Delay(250);
            if (generation != _reflectionGeneration || _closed) return;
            var reflection = await Task.Run(() =>
            {
                using var report = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(folder, "report.json")));
                var textures = report.RootElement.GetProperty("textures");
                string? maskRelative = null;
                if (textures.ValueKind == JsonValueKind.Object)
                {
                    maskRelative = textures.GetProperty("env_mask").GetString();
                    var slots = textures.GetProperty("material_slots");
                    if (maskRelative == null && slots.GetArrayLength() > 0)
                        maskRelative = slots[0].GetProperty("env_mask").GetString();
                }
                PreviewTexture mask;
                if (!string.IsNullOrWhiteSpace(maskRelative))
                {
                    var maskPath = System.IO.Path.Combine(folder, maskRelative.Replace('/', System.IO.Path.DirectorySeparatorChar));
                    mask = DdsPreviewDecoder.Decode(maskRelative, File.ReadAllBytes(maskPath));
                }
                else if (selectedMask.Length > 0)
                {
                    using var memory = new MemoryStream(ReadPreviewTextureSource(archivePath, selectedMask));
                    if (selectedMask.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                        mask = DdsPreviewDecoder.Decode(selectedMask, memory.ToArray());
                    else
                    {
                        memory.Position = 0;
                        var frame = BitmapFrame.Create(memory, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0 || (long)bitmap.PixelWidth * bitmap.PixelHeight > 16 * 1024 * 1024)
                            throw new InvalidDataException("Metallic image dimensions exceed the preview limit.");
                        var pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
                        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                        mask = new PreviewTexture(selectedMask, bitmap.PixelWidth, bitmap.PixelHeight, pixels);
                    }
                }
                else return null;
                byte[] cubeBytes;
                string cubeName;
                if (ownEntry.Length > 0)
                {
                    cubeBytes = ReadPreviewTextureSource(archivePath, ownEntry); cubeName = ownEntry;
                }
                else
                {
                    cubeName = gamePath.Length > 0 ? gamePath : report.RootElement.GetProperty("views")[0]
                        .GetProperty("weapon_textures")[0].GetProperty("EnvMap").GetString() ?? "";
                    cubeBytes = GameCubemapCatalog.Read(gameData, cubeName);
                }
                var faces = DdsCubemapPreviewDecoder.Decode(cubeName, cubeBytes);
                return new PreviewSceneRenderer.ReflectionPreview(mask, faces);
            });
            if (generation != _reflectionGeneration || _closed) return;
            _preview.SetReflectionPreview(reflection);
            if (reflection != null && !changedSelection)
                _previewStatus.Text = "Показано наближений вплив Environment Map. Поверніть модель для огляду.";
            else if (reflection == null)
                _previewStatus.Text = "Для перегляду відбиттів виберіть Metallic і cubemap та підготуйте NIF повторно.";
        }
        catch (Exception error)
        {
            if (generation != _reflectionGeneration || _closed) return;
            _preview.SetReflectionPreview(null);
            _previewStatus.Text = "Відбиття в перегляді недоступне: " + error.Message;
        }
    }

    private static byte[] ReadPreviewTextureSource(string archivePath, string name)
    {
        const int limit = 64 * 1024 * 1024;
        if (System.IO.Path.IsPathFullyQualified(name))
        {
            var info = new FileInfo(name);
            if (info.Length is <= 0 or > limit) throw new InvalidDataException("Texture exceeds the 64 MiB limit.");
            return File.ReadAllBytes(name);
        }
        using var archive = ZipFile.OpenRead(archivePath);
        var entry = archive.Entries.Single(item => item.FullName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (entry.Length is <= 0 or > limit) throw new InvalidDataException("Texture exceeds the 64 MiB limit.");
        using var stream = entry.Open(); using var memory = new MemoryStream();
        var buffer = new byte[81920]; int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (memory.Length + count > limit) throw new InvalidDataException("Texture exceeds the 64 MiB limit.");
            memory.Write(buffer, 0, count);
        }
        return memory.ToArray();
    }

    private void AddTexturePicker(Panel panel, string label, TextBox textBox, bool ddsOnly)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 2) });
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        textBox.MinWidth = 0;
        row.Children.Add(textBox);
        var zip = new Button { Content = "ZIP…", Margin = new Thickness(5, 0, 0, 0) };
        zip.SetBinding(IsEnabledProperty, new Binding(nameof(IsEnabled)) { Source = textBox });
        Grid.SetColumn(zip, 1);
        zip.Click += (_, _) => SelectZipTexture(textBox, ddsOnly);
        row.Children.Add(zip);
        var file = new Button { Content = "Файл…", Margin = new Thickness(5, 0, 0, 0) };
        file.SetBinding(IsEnabledProperty, new Binding(nameof(IsEnabled)) { Source = textBox });
        Grid.SetColumn(file, 2);
        file.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = ddsOnly ? "DDS (*.dds)|*.dds" :
                "Зображення|*.png;*.jpg;*.jpeg;*.tga;*.bmp;*.dds" };
            if (dialog.ShowDialog(this) == true) { textBox.Text = dialog.FileName; EnableSelectedTextures(); }
        };
        row.Children.Add(file); panel.Children.Add(row);
    }

    private void SelectZipTexture(TextBox target, bool ddsOnly)
    {
        try
        {
            using var archive = ZipFile.OpenRead(_archive.Text.Trim());
            var extensions = ddsOnly ? new[] { ".dds" } : new[] { ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".dds" };
            var names = archive.Entries.Where(item => !string.IsNullOrEmpty(item.Name) &&
                extensions.Contains(System.IO.Path.GetExtension(item.Name), StringComparer.OrdinalIgnoreCase))
                .Select(item => item.FullName).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var list = new ListBox { ItemsSource = names, Margin = new Thickness(8), MinHeight = 220 };
            list.SelectedItem = names.FirstOrDefault(name => name.Equals(target.Text, StringComparison.OrdinalIgnoreCase));
            var select = new Button { Content = "Обрати", IsDefault = true, Margin = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Right };
            var layout = new DockPanel(); DockPanel.SetDock(select, Dock.Bottom); layout.Children.Add(select); layout.Children.Add(list);
            var window = new Window { Title = "Текстура в ZIP", Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 650, Height = 420, MinWidth = 350, MinHeight = 260, Content = layout };
            void Accept() { if (list.SelectedItem is string) window.DialogResult = true; }
            select.Click += (_, _) => Accept();
            list.MouseDoubleClick += (_, _) => Accept();
            if (window.ShowDialog() == true && list.SelectedItem is string name) { target.Text = name; EnableSelectedTextures(); }
        }
        catch (Exception error) { _status.Text = "Не вдалося відкрити ZIP: " + error.Message; }
    }

    private void EnableSelectedTextures()
    {
        if (!string.IsNullOrWhiteSpace(_diffuseEntry.Text) && !string.IsNullOrWhiteSpace(_normalEntry.Text))
            _useTextures.IsChecked = true;
    }

    private void AddPath(Panel panel, string label, TextBox textBox, bool folder, string? filter = null)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 4) });
        var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        textBox.MinWidth = 280;
        row.Children.Add(textBox);
        var browse = new Button { Content = "Огляд…", Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(browse, 1);
        browse.Click += (_, _) =>
        {
            if (folder)
            {
                var dialog = new OpenFolderDialog();
                if (dialog.ShowDialog(this) == true) textBox.Text = dialog.FolderName;
            }
            else
            {
                var dialog = new OpenFileDialog { Filter = filter ?? "All files|*.*" };
                if (dialog.ShowDialog(this) == true)
                {
                    textBox.Text = dialog.FileName;
                    if (textBox == _archive) SuggestTextures();
                }
            }
        };
        row.Children.Add(browse);
        panel.Children.Add(row);
    }

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        SavePreferences();
        ++_reflectionGeneration;
        await _preview.LoadAsync(null);
        _draft?.Dispose();
        _draft = null;
        _cancellation = new CancellationTokenSource();
        _resultFolder = null;
        _thirdPersonPath = null;
        _firstPersonPath = null;
        _preparedDefinition = null;
        _compare.IsEnabled = false;
        _save.IsEnabled = false;
        _run.IsEnabled = false;
        _fitPanel.IsEnabled = false;
        _preview.SetFitGizmoMode(FitGizmoMode.None);
        _cancel.IsEnabled = true;
        _status.Text = "Імпорт, підгонка шарів крові та створення NIF…";
        try
        {
            var archive = _archive.Text.Trim();
            var data = _gameData.Text.Trim();
            var blender = _blender.Text.Trim();
            var addon = _addon.Text.Trim();
            var output = _output.Text.Trim();
            var modelEntry = _modelEntry.Text.Trim();
            var cancellationToken = _cancellation.Token;
            var scabbard = _scabbard.IsChecked == true;
            var defaultShader = _defaultShader.IsChecked == true;
            double? vertexColor = _useVertexColor.IsChecked == true ? _vertexColor.Value / 100 : null;
            var shaderSettings = new ModelPreparationShaderSettings(
                !defaultShader && _useEnvironmentStrength.IsChecked == true ? _environmentStrength.Value : null,
                _useSpecular.IsChecked == true ? _specularStrength.Value : null,
                _specularColor.SelectedColor.R / 255.0, _specularColor.SelectedColor.G / 255.0, _specularColor.SelectedColor.B / 255.0);
            var adjustments = ReadAdjustments();
            var weaponType = ((WeaponTemplateDefinition)_weaponType.SelectedItem).Id;
            var textures = _useTextures.IsChecked == true
                ? new ModelPreparationTextureOptions(_diffuseEntry.Text.Trim(), _normalEntry.Text.Trim(),
                    _texconv.Text.Trim(), _invertNormalY.IsChecked == true, ReadMaterialEntries(),
                    _metallicEntry.Text.Trim(), _cubemapEntry.Text.Trim(),
                    (_gameCubemap.SelectedItem as GameCubemap)?.Path, SelectedTextureSize)
                : null;
            var draft = await Task.Run(() => ModelPreparationRunner.PrepareDraftAsync(
                archive, data, blender, addon, output, scabbard, cancellationToken,
                adjustments: adjustments, weaponType: weaponType, textures: textures,
                modelEntry: modelEntry, defaultShader: defaultShader, vertexColor: vertexColor, shaderSettings: shaderSettings));
            if (_closed) { draft.Dispose(); return; }
            _draft = draft;
            _preparedAdjustments = adjustments;
            _preparedRotation = new FitRotation(adjustments.RotationXDegrees, adjustments.RotationYDegrees, adjustments.RotationZDegrees);
            _preview.SetFitAdjustment(adjustments, adjustments);
            _preparedDefinition = WeaponTemplateCatalog.GetExportable(weaponType);
            _thirdPersonPath = draft.ThirdPersonPath;
            _firstPersonPath = draft.FirstPersonPath;
            _compare.IsEnabled = true;
            _save.IsEnabled = true;
            _preview.SetReflectionPreview(null);
            await _preview.LoadAsync(new ModelPreviewRequest(draft.StagingDirectory,
                draft.ThirdPersonPath, draft.FirstPersonPath, data));
            _preview.SetView(new PreviewViewState(90, -10, 3.3));
            await UpdateReflectionPreviewAsync(false);
            _status.Text = "Обидва NIF пройшли структурну перевірку. Порівняйте їх із шаблоном, потім натисніть «Зберегти комплект». Ігрова перевірка ще не проведена.";
        }
        catch (OperationCanceledException) { _status.Text = "Скасовано. Неповний комплект прибрано."; }
        catch (Exception error) { _status.Text = "Помилка: " + error.Message; }
        finally
        {
            _cancel.IsEnabled = false;
            _run.IsEnabled = true;
            _fitPanel.IsEnabled = true;
            if (_fitPanel.IsVisible) _preview.SetFitGizmoMode(_fitGizmoMode);
            _cancellation.Dispose();
            _cancellation = null;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        SavePreferences();
        _closed = true;
        _cancellation?.Cancel();
        _characterCancellation?.Cancel();
        _draft?.Dispose();
        _preview.Dispose();
        base.OnClosed(e);
    }
}
