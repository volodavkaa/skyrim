using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SkyrimModPacker.UI.Controls;
using Xunit;

namespace SkyrimModPacker.Tests;

[Collection("WPF page")]
public class ResponsiveColumnsPanelTests
{
    [Fact]
    public async Task Resizing_ReflowsRowsAndPreservesNaturalHeights_WithoutOverlapping()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var panel = new ResponsiveColumnsPanel { MinColumnWidth = 350, MaxColumns = 2, Gap = 20 };
                var first = new Border { Height = 80 };
                var second = new Border { Height = 120 };
                var third = new Border { Height = 60 };
                panel.Children.Add(first); panel.Children.Add(second); panel.Children.Add(third);
                panel.Measure(new Size(900, double.PositiveInfinity));
                panel.Arrange(new Rect(0, 0, 900, panel.DesiredSize.Height));
                Assert.Equal(200, panel.DesiredSize.Height);
                Assert.Equal(460, VisualTreeHelper.GetOffset(second).X);
                Assert.Equal(140, VisualTreeHelper.GetOffset(third).Y);
                panel.Measure(new Size(500, double.PositiveInfinity));
                panel.Arrange(new Rect(0, 0, 500, panel.DesiredSize.Height));
                Assert.Equal(300, panel.DesiredSize.Height);
                Assert.Equal(0, VisualTreeHelper.GetOffset(second).X);
                Assert.Equal(100, VisualTreeHelper.GetOffset(second).Y);
                Assert.Equal(240, VisualTreeHelper.GetOffset(third).Y);
                second.Visibility = Visibility.Collapsed;
                panel.Measure(new Size(900, double.PositiveInfinity));
                panel.Arrange(new Rect(0, 0, 900, panel.DesiredSize.Height));
                Assert.Equal(80, panel.DesiredSize.Height);
                Assert.Equal(460, VisualTreeHelper.GetOffset(third).X);
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
