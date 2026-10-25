using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Globalization;
using System.IO.Compression;
using Microsoft.Win32;
using SkyrimModPacker.Infrastructure.ModelPreparation;

namespace SkyrimModPacker.UI.Views;

/// <summary>Standalone preparation window; it does not access an open mod project.</summary>
public sealed class ModelPreparationWindow : Window
{
    private readonly TextBox _archive = new();
    private readonly TextBox _gameData = new();
    private readonly TextBox _blender = new();
    private readonly TextBox _addon = new();
    private readonly TextBox _output = new();
    private readonly ComboBox _weaponType = new() { DisplayMemberPath = "DisplayName" };
    private readonly CheckBox _useTextures = new() { Content = "Створити DDS та підключити текстури зброї" };
    private readonly TextBox _diffuseEntry = new();
    private readonly TextBox _normalEntry = new();
    private readonly TextBox _texconv = new();
    private readonly CheckBox _invertNormalY = new() { Content = "Інвертувати зелений канал normal map (якщо джерело OpenGL)" };
    private readonly CheckBox _scabbard = new() { Content = "Створити піхви за формою клинка" };
    private readonly TextBox _scale = new() { Text = "1" };
    private readonly TextBox _rotationX = new() { Text = "0" };
    private readonly TextBox _rotationY = new() { Text = "0" };
    private readonly TextBox _rotationZ = new() { Text = "0" };
    private readonly TextBox _offsetX = new() { Text = "0" };
    private readonly TextBox _offsetY = new() { Text = "0" };
    private readonly TextBox _offsetZ = new() { Text = "0" };
    private readonly TextBox _gripFraction = new() { Text = "0.11" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _run = new() { Content = "Підготувати два NIF", MinWidth = 170 };
    private readonly Button _cancel = new() { Content = "Скасувати", IsEnabled = false, Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _compare = new() { Content = "Порівняти результат", IsEnabled = false,
        Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _save = new() { Content = "Зберегти комплект", IsEnabled = false,
        Margin = new Thickness(8, 0, 0, 0) };
    private ModelPreparationDraft? _draft;
    private string? _resultFolder;
    private string? _thirdPersonPath;
    private string? _firstPersonPath;
    private WeaponTemplateDefinition? _preparedDefinition;
    private CancellationTokenSource? _cancellation;
    private bool _closed;

    public ModelPreparationWindow()
    {
        Title = "Підготовка моделі — експериментально";
        Width = 750;
        Height = 760;
        MinWidth = 640;
        MinHeight = 540;
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
        Resources.Add(typeof(Button), buttonStyle);
        var labelStyle = new Style(typeof(TextBlock));
        labelStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brushes.White));
        Resources.Add(typeof(TextBlock), labelStyle);
        var checkStyle = new Style(typeof(CheckBox));
        checkStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        Resources.Add(typeof(CheckBox), checkStyle);
        _blender.Text = @"C:\Program Files (x86)\Steam\steamapps\common\Blender\blender.exe";
        _gameData.Text = @"E:\SteamLibrary\steamapps\common\Skyrim Special Edition\Data";
        var panel = new StackPanel { Margin = new Thickness(20) };
        foreach (var weapon in WeaponTemplateCatalog.All.Where(item => item.ExportEnabled))
            _weaponType.Items.Add(weapon);
        _weaponType.SelectedIndex = 0;
        _weaponType.SelectionChanged += (_, _) =>
        {
            var supportsScabbard = (_weaponType.SelectedItem as WeaponTemplateDefinition)?.SupportsScabbard == true;
            _scabbard.IsEnabled = supportsScabbard;
            if (!supportsScabbard) _scabbard.IsChecked = false;
        };
        panel.Children.Add(new TextBlock { Text = "Тип зброї", Margin = new Thickness(0, 0, 0, 4) });
        _weaponType.Margin = new Thickness(0, 0, 0, 12);
        panel.Children.Add(_weaponType);
        panel.Children.Add(new TextBlock { Text = "Підготовка статичної зброї з ZIP/FBX або ZIP/GLB за шаблоном Skyrim SE/AE.",
            TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "Шари крові підганяються до робочої частини моделі. Піхви можна додати або прибрати для підтримуваного типу. Вигляд і колізія в грі ще не перевірені; для текстур потрібен texconv.exe.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkOrange, Margin = new Thickness(0, 0, 0, 15) });
        AddPath(panel, "ZIP з моделлю FBX або GLB", _archive, false, "ZIP (*.zip)|*.zip");
        AddPath(panel, "Папка Data встановленого Skyrim SE/AE", _gameData, true);
        AddPath(panel, "Blender executable", _blender, false, "Executable (*.exe)|*.exe");
        AddPath(panel, "Папка, що містить io_scene_nifly", _addon, true);
        AddPath(panel, "Вихідна папка поза грою", _output, true);
        _useTextures.Margin = new Thickness(0, 8, 0, 6);
        panel.Children.Add(_useTextures);
        panel.Children.Add(new TextBlock { Text = "Точні шляхи зображень усередині ZIP. Metallic зберігається лише у звіті.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(new TextBlock { Text = "Diffuse / BaseColor у ZIP" });
        panel.Children.Add(_diffuseEntry);
        panel.Children.Add(new TextBlock { Text = "Normal у ZIP", Margin = new Thickness(0, 6, 0, 0) });
        panel.Children.Add(_normalEntry);
        AddPath(panel, "texconv.exe", _texconv, false, "Executable (*.exe)|*.exe");
        var suggestTextures = new Button { Content = "Знайти BaseColor і Normal у ZIP",
            Margin = new Thickness(0, 0, 0, 6) };
        suggestTextures.Click += (_, _) => SuggestTextures();
        panel.Children.Add(suggestTextures);
        panel.Children.Add(_invertNormalY);
        _scabbard.Margin = new Thickness(0, 7, 0, 12);
        panel.Children.Add(_scabbard);
        panel.Children.Add(new TextBlock { Text = "Коригування після автоматичної підгонки",
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 6) });
        panel.Children.Add(new TextBlock { Text = "Точка хвата: частка довжини джерела від основи до вістря (0–1). Поворот у градусах, зсув у NIF-одиницях. Зміни застосовуються також до крові, піхов і колізії.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var numbers = new WrapPanel();
        AddNumber(numbers, "Масштаб ×", _scale);
        AddNumber(numbers, "Поворот X", _rotationX);
        AddNumber(numbers, "Поворот Y", _rotationY);
        AddNumber(numbers, "Поворот Z", _rotationZ);
        AddNumber(numbers, "Зсув X", _offsetX);
        AddNumber(numbers, "Зсув Y", _offsetY);
        AddNumber(numbers, "Зсув Z", _offsetZ);
        AddNumber(numbers, "Точка хвата", _gripFraction);
        panel.Children.Add(numbers);
        var reset = new Button { Content = "Скинути коригування", Margin = new Thickness(0, 3, 0, 12) };
        reset.Click += (_, _) =>
        {
            _scale.Text = "1";
            _gripFraction.Text = "0.11";
            foreach (var field in new[] { _rotationX, _rotationY, _rotationZ, _offsetX, _offsetY, _offsetZ })
                field.Text = "0";
        };
        panel.Children.Add(reset);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
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
        _save.Click += (_, _) =>
        {
            if (_draft == null) return;
            try
            {
                _resultFolder = _draft.Publish();
                _draft.Dispose();
                _draft = null;
                _save.IsEnabled = false;
                _status.Text = "Збережено комплект: " + _resultFolder +
                    "\nПеревірка поведінки у Skyrim ще не проведена.";
            }
            catch (Exception error) { _status.Text = "Помилка збереження: " + error.Message; }
        };
        actions.Children.Add(_run);
        actions.Children.Add(_cancel);
        actions.Children.Add(_compare);
        actions.Children.Add(_save);
        panel.Children.Add(actions);
        _status.Margin = new Thickness(0, 14, 0, 0);
        panel.Children.Add(_status);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static void AddNumber(Panel panel, string label, TextBox box)
    {
        var group = new StackPanel { Width = 145, Margin = new Thickness(0, 0, 12, 9) };
        group.Children.Add(new TextBlock { Text = label });
        group.Children.Add(box);
        panel.Children.Add(group);
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
            Number(_rotationZ), Number(_offsetX), Number(_offsetY), Number(_offsetZ), Number(_gripFraction));
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
            _status.Text = string.IsNullOrWhiteSpace(_diffuseEntry.Text) || string.IsNullOrWhiteSpace(_normalEntry.Text)
                ? "Не вдалося однозначно знайти BaseColor і Normal. Вкажіть точні шляхи файлів у ZIP."
                : "Знайдено BaseColor і Normal. Перевірте вибір перед підготовкою.";
        }
        catch (Exception error) { _status.Text = "Помилка читання ZIP: " + error.Message; }
    }

    private void AddPath(Panel panel, string label, TextBox textBox, bool folder, string? filter = null)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 4) });
        var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        textBox.MinWidth = 420;
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
                if (dialog.ShowDialog(this) == true) textBox.Text = dialog.FileName;
            }
        };
        row.Children.Add(browse);
        panel.Children.Add(row);
    }

    private async void OnRun(object sender, RoutedEventArgs e)
    {
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
        _cancel.IsEnabled = true;
        _status.Text = "Імпорт, підгонка шарів крові та створення NIF…";
        try
        {
            var archive = _archive.Text.Trim();
            var data = _gameData.Text.Trim();
            var blender = _blender.Text.Trim();
            var addon = _addon.Text.Trim();
            var output = _output.Text.Trim();
            var scabbard = _scabbard.IsChecked == true;
            var adjustments = ReadAdjustments();
            var weaponType = ((WeaponTemplateDefinition)_weaponType.SelectedItem).Id;
            var textures = _useTextures.IsChecked == true
                ? new ModelPreparationTextureOptions(_diffuseEntry.Text.Trim(), _normalEntry.Text.Trim(),
                    _texconv.Text.Trim(), _invertNormalY.IsChecked == true)
                : null;
            var draft = await Task.Run(() => ModelPreparationRunner.PrepareDraftAsync(
                archive, data, blender, addon, output, scabbard, _cancellation.Token,
                adjustments: adjustments, weaponType: weaponType, textures: textures));
            if (_closed) { draft.Dispose(); return; }
            _draft = draft;
            _preparedDefinition = WeaponTemplateCatalog.GetExportable(weaponType);
            _thirdPersonPath = draft.ThirdPersonPath;
            _firstPersonPath = draft.FirstPersonPath;
            _compare.IsEnabled = true;
            _save.IsEnabled = true;
            _status.Text = "Обидва NIF пройшли структурну перевірку. Порівняйте їх із шаблоном, потім натисніть «Зберегти комплект». Ігрова перевірка ще не проведена.";
        }
        catch (OperationCanceledException) { _status.Text = "Скасовано. Неповний комплект прибрано."; }
        catch (Exception error) { _status.Text = "Помилка: " + error.Message; }
        finally
        {
            _cancel.IsEnabled = false;
            _run.IsEnabled = true;
            _cancellation.Dispose();
            _cancellation = null;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _cancellation?.Cancel();
        _draft?.Dispose();
        base.OnClosed(e);
    }
}
