using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SkyrimModPacker.Infrastructure.Preview;
using SkyrimModPacker.UI.Controls;
using SkyrimModPacker.UI.Services;

namespace SkyrimModPacker.UI.Views;

/// <summary>Read-only side-by-side view of an exported sword and its vanilla template.</summary>
public sealed class ModelPreparationComparisonWindow : Window
{
    private readonly ModelPreviewControl _prepared;
    private readonly ModelPreviewControl _template;

    public ModelPreparationComparisonWindow(string outputFolder, string gameDataFolder,
        string thirdPersonPath, string firstPersonPath,
        string templateThirdPersonPath, string templateFirstPersonPath)
    {
        Title = "Порівняння підготовленої моделі";
        Width = 1220;
        Height = 700;
        MinWidth = 750;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(16, 23, 34));
        Foreground = Brushes.White;
        var service = new ModelPreviewService();
        _prepared = new ModelPreviewControl(service);
        _template = new ModelPreviewControl(service);
        _prepared.SetCompact(true);
        _template.SetCompact(true);
        var root = new DockPanel { Margin = new Thickness(14) };
        var note = new TextBlock
        {
            Text = "Ліворуч — підготовлена модель, праворуч — ванільний зразок обраного типу. Оберіть першу або третю особу в кожній панелі. Перегляд показує статичну геометрію; звіт перевіряє структуру крові, піхов і колізії. Фізика в грі ще не перевірена.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 0, 4, 12)
        };
        DockPanel.SetDock(note, Dock.Top);
        root.Children.Add(note);
        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel();
        left.Children.Add(new TextBlock { Text = "Підготовлена модель", FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(5, 0, 5, 7) });
        left.Children.Add(_prepared);
        columns.Children.Add(left);
        var right = new StackPanel();
        right.Children.Add(new TextBlock { Text = "Ванільний шаблон", FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(5, 0, 5, 7) });
        right.Children.Add(_template);
        Grid.SetColumn(right, 1);
        columns.Children.Add(right);
        root.Children.Add(columns);
        Content = root;
        _prepared.ViewChanged += state => _template.SetView(state);
        _template.ViewChanged += state => _prepared.SetView(state);
        void SetScale()
        {
            if (_prepared.OriginalExtent <= 0 || _template.OriginalExtent <= 0) return;
            var extent = Math.Max(_prepared.OriginalExtent, _template.OriginalExtent);
            _prepared.SetNormalizationExtent(extent);
            _template.SetNormalizationExtent(extent);
        }
        _prepared.SceneChanged += SetScale;
        _template.SceneChanged += SetScale;
        Loaded += async (_, _) =>
        {
            await Task.WhenAll(
                _prepared.LoadAsync(new ModelPreviewRequest(outputFolder, thirdPersonPath,
                    firstPersonPath, gameDataFolder)),
                _template.LoadAsync(new ModelPreviewRequest("", templateThirdPersonPath,
                    templateFirstPersonPath, gameDataFolder)));
            SetScale();
        };
        Closed += (_, _) => { _prepared.Dispose(); _template.Dispose(); };
    }
}
