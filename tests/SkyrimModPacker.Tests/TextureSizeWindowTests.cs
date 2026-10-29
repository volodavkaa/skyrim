using System.IO.Compression;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SkyrimModPacker.UI.Views;
using Xunit;

namespace SkyrimModPacker.Tests;

[Collection("WPF page")]
public class TextureSizeWindowTests
{
    [Fact]
    public async Task SelectingLimitUpdatesThePreconversionEstimate()
    {
        var root = Path.Combine(Path.GetTempPath(), "skyrim-size-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var zip = Path.Combine(root, "textures.zip");
            var pixels = new byte[1024 * 512 * 4];
            var bitmap = BitmapSource.Create(1024, 512, 96, 96, PixelFormats.Bgra32, null, pixels, 4096);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var buffer = new MemoryStream(); encoder.Save(buffer); buffer.Position = 0;
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            using (var output = archive.CreateEntry("image.png").Open()) buffer.CopyTo(output);
            var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                dispatcher.BeginInvoke(async () =>
                {
                    try
                    {
                        var window = new ModelPreparationWindow();
                        T Field<T>(string name) => (T)typeof(ModelPreparationWindow).GetField(name,
                            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
                        Field<TextBox>("_archive").Text = zip;
                        Field<TextBox>("_diffuseEntry").Text = "image.png";
                        var limits = Field<ComboBox>("_textureSizeLimit");
                        limits.SelectedItem = limits.Items.OfType<ComboBoxItem>().Single(item => (int)item.Tag == 256);
                        var report = Field<TextBlock>("_textureSizeReport");
                        for (var attempt = 0; attempt < 40 && !report.Text.Contains("1024×512 → 256×128"); attempt++)
                            await Task.Delay(100);
                        Assert.Contains("1024×512 → 256×128", report.Text);
                        Assert.Contains("Разом", report.Text);
                        result.SetResult();
                    }
                    catch (Exception error) { result.SetException(error); }
                    finally { dispatcher.InvokeShutdown(); }
                });
                Dispatcher.Run();
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            await result.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally { Directory.Delete(root, true); }
    }
}
