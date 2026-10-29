using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkyrimModPacker.UI.Controls;
using Xunit;

namespace SkyrimModPacker.Tests;

[Collection("WPF page")]
public class TriangleColorPickerTests
{
    [Fact]
    public async Task PickerPreservesColorsAndRendersHueRingAndTriangle()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var picker = new TriangleColorPicker();
                foreach (var color in new[] { Colors.White, Colors.Black, Colors.Red, Colors.Lime, Colors.Blue, Color.FromRgb(27, 128, 215) })
                {
                    picker.SelectedColor = color;
                    var actual = picker.SelectedColor;
                    Assert.InRange(Math.Abs(actual.R - color.R), 0, 1);
                    Assert.InRange(Math.Abs(actual.G - color.G), 0, 1);
                    Assert.InRange(Math.Abs(actual.B - color.B), 0, 1);
                }
                picker.Measure(new Size(220, 220)); picker.Arrange(new Rect(0, 0, 220, 220));
                var bitmap = new RenderTargetBitmap(220, 220, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(picker); var pixels = new byte[220 * 220 * 4]; bitmap.CopyPixels(pixels, 880, 0);
                var screenshot = Environment.GetEnvironmentVariable("SKYRIM_COLOR_PICKER_IMAGE");
                if (!string.IsNullOrWhiteSpace(screenshot))
                {
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(screenshot); encoder.Save(file);
                }
                Assert.True(pixels[(14 * 220 + 110) * 4 + 3] > 0); // hue ring
                Assert.True(pixels[(100 * 220 + 110) * 4 + 3] > 0); // triangle
                result.SetResult();
            }
            catch (Exception error) { result.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await result.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
}
