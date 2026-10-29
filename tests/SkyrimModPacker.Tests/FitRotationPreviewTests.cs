using System.Windows.Media.Media3D;
using System.Windows.Controls;
using System.Reflection;
using SkyrimModPacker.UI.Services;
using SkyrimModPacker.UI.Views;
using Xunit;

namespace SkyrimModPacker.Tests;

public class FitRotationPreviewTests
{
    [Fact]
    public async Task RotationSlidersAndPreciseFieldsStayInSync()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var window = new ModelPreparationWindow();
                T Field<T>(string name) => (T)typeof(ModelPreparationWindow)
                    .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var slider = Field<Slider>("_rotationSliderX");
                var field = Field<TextBox>("_rotationX");
                slider.Value = 35;
                Assert.Equal("35", field.Text);
                field.Text = "-42";
                Assert.Equal(-42, slider.Value);
                field.Text = "999";
                Assert.Equal(-42, slider.Value);
                result.SetResult();
            }
            catch (Exception error) { result.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await result.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void PreparedRotationIsRemovedBeforeApplyingNewEulerAngles()
    {
        var initial = new FitRotation(35, -20, 75);
        var target = new FitRotation(-40, 55, 10);
        var original = new Vector3D(0.3, 0.8, -0.2);
        static Matrix3D Absolute(FitRotation angles)
        {
            var matrix = Matrix3D.Identity;
            matrix.Rotate(new Quaternion(new Vector3D(1, 0, 0), angles.X));
            matrix.Rotate(new Quaternion(new Vector3D(0, 0, -1), angles.Y));
            matrix.Rotate(new Quaternion(new Vector3D(0, 1, 0), angles.Z));
            return matrix;
        }
        var actual = FitRotationPreview.Difference(initial, target).Transform(Absolute(initial).Transform(original));
        var expected = Absolute(target).Transform(original);
        Assert.True((actual - expected).Length < 1e-10);
        Assert.True((FitRotationPreview.Difference(initial, initial).Transform(original) - original).Length < 1e-10);
    }

    [Fact]
    public void NinetyDegreeNifRotationsFollowDisplayAxes()
    {
        var identity = new FitRotation();
        var x = FitRotationPreview.Difference(identity, new(90, 0, 0)).Transform(new Vector3D(0, 0, -1));
        var y = FitRotationPreview.Difference(identity, new(0, 90, 0)).Transform(new Vector3D(1, 0, 0));
        var z = FitRotationPreview.Difference(identity, new(0, 0, 90)).Transform(new Vector3D(1, 0, 0));
        Assert.True((x - new Vector3D(0, 1, 0)).Length < 1e-10);
        Assert.True((y - new Vector3D(0, -1, 0)).Length < 1e-10);
        Assert.True((z - new Vector3D(0, 0, -1)).Length < 1e-10);
    }
}
