using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Globalization;
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
    private readonly CheckBox _scabbard = new() { Content = "Створити піхви за формою клинка" };
    private readonly TextBox _scale = new() { Text = "1" };
    private readonly TextBox _rotationX = new() { Text = "0" };
    private readonly TextBox _rotationY = new() { Text = "0" };
    private readonly TextBox _rotationZ = new() { Text = "0" };
    private readonly TextBox _offsetX = new() { Text = "0" };
    private readonly TextBox _offsetY = new() { Text = "0" };
    private readonly TextBox _offsetZ = new() { Text = "0" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _run = new() { Content = "Підготувати два NIF", MinWidth = 170 };
    private readonly Button _cancel = new() { Content = "Скасувати", IsEnabled = false, Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _compare = new() { Content = "Порівняти результат", IsEnabled = false,
        Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _save = new() { Content = "Зберегти комплект", IsEnabled = false,
        Margin = new Thickness(8, 0, 0, 0) };
    private ModelPreparationDraft? _draft;
    private string? _resultFolder;
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
        _blender.Text = @"C:\Program Files (x86)\Steam\steamapps\common\Blender\blender.exe";
        _gameData.Text = @"E:\SteamLibrary\steamapps\common\Skyrim Special Edition\Data";
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "Підготовка статичного одноручного меча з ZIP/FBX за шаблоном Skyrim SE/AE.",
            TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = "Ефекти BloodEffects і BloodLighting підганяються до геометрії клинка. Піхви можна додати або прибрати. Вигляд і колізія в грі ще не перевірені; текстури не перетворюються.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkOrange, Margin = new Thickness(0, 0, 0, 15) });
        AddPath(panel, "ZIP з моделлю FBX", _archive, false, "ZIP (*.zip)|*.zip");
        AddPath(panel, "Папка Data встановленого Skyrim SE/AE", _gameData, true);
        AddPath(panel, "Blender executable", _blender, false, "Executable (*.exe)|*.exe");
        AddPath(panel, "Папка, що містить io_scene_nifly", _addon, true);
        AddPath(panel, "Вихідна папка поза грою", _output, true);
        _scabbard.Margin = new Thickness(0, 7, 0, 12);
        panel.Children.Add(_scabbard);
        panel.Children.Add(new TextBlock { Text = "Коригування після автоматичної підгонки",
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 6) });
        panel.Children.Add(new TextBlock { Text = "Поворот у градусах, зсув у NIF-одиницях. Зміни застосовуються також до крові, піхов і колізії.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var numbers = new WrapPanel();
        AddNumber(numbers, "Масштаб ×", _scale);
        AddNumber(numbers, "Поворот X", _rotationX);
        AddNumber(numbers, "Поворот Y", _rotationY);
        AddNumber(numbers, "Поворот Z", _rotationZ);
        AddNumber(numbers, "Зсув X", _offsetX);
        AddNumber(numbers, "Зсув Y", _offsetY);
        AddNumber(numbers, "Зсув Z", _offsetZ);
        panel.Children.Add(numbers);
        var reset = new Button { Content = "Скинути коригування", Margin = new Thickness(0, 3, 0, 12) };
        reset.Click += (_, _) =>
        {
            _scale.Text = "1";
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
            if (folder != null)
                new ModelPreparationComparisonWindow(folder, _gameData.Text.Trim())
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
            Number(_rotationZ), Number(_offsetX), Number(_offsetY), Number(_offsetZ));
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
            var draft = await Task.Run(() => ModelPreparationRunner.PrepareDraftAsync(
                archive, data, blender, addon, output, scabbard, _cancellation.Token,
                adjustments: adjustments));
            if (_closed) { draft.Dispose(); return; }
            _draft = draft;
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
