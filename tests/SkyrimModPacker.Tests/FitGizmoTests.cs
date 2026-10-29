using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using NSubstitute;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Infrastructure.ModelPreparation;
using SkyrimModPacker.Infrastructure.Preview;
using SkyrimModPacker.UI.Controls;
using SkyrimModPacker.UI.Services;
using Xunit;

namespace SkyrimModPacker.Tests;

[Collection("WPF page")]
public class FitGizmoTests
{
    [Fact]
    public async Task GizmoProjectsAxesAndDragUpdatesFitWithoutRotatingCamera()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    var scene = SseNifReader.Read(PreviewFixtures.Nif());
                    var service = Substitute.For<IModelPreviewService>();
                    service.LoadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(scene);
                    var preview = new ModelPreviewControl(service) { Width = 700, Height = 700 };
                    preview.SetFitGizmoMode(FitGizmoMode.Move);
                    await preview.LoadAsync(new ModelPreviewRequest("", "weapon.nif", "", ""));
                    preview.SetView(new PreviewViewState(35, -15, 4));
                    preview.Measure(new Size(700, 700)); preview.Arrange(new Rect(0, 0, 700, 700)); preview.UpdateLayout();
                    var canvas = (Canvas)typeof(ModelPreviewControl).GetField("_gizmoCanvas", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(preview)!;
                    Assert.True(canvas.Children.OfType<Line>().Count() >= 4);
                    Assert.Contains(canvas.Children.OfType<TextBlock>(), label => label.Text == "X");
                    var originalView = preview.ViewState;
                    var dragType = typeof(ModelPreviewControl).GetNestedType("GizmoDrag", BindingFlags.NonPublic)!;
                    var field = typeof(ModelPreviewControl).GetField("_gizmoDrag", BindingFlags.NonPublic | BindingFlags.Instance)!;
                    var move = typeof(ModelPreviewControl).GetMethod("ApplyGizmoDrag", BindingFlags.NonPublic | BindingFlags.Instance)!;
                    ModelPreparationAdjustments? selected = null;
                    preview.FitAdjustmentRequested += value => selected = value;
                    field.SetValue(preview, Activator.CreateInstance(dragType, FitGizmoMode.Move, 0, new Point(0, 0), new Vector(1, 0), 2d, new ModelPreparationAdjustments()));
                    move.Invoke(preview, [new Point(10, 0)]);
                    Assert.Equal(5, selected!.OffsetX);
                    Assert.Equal(originalView, preview.ViewState);
                    var viewport = (Viewport3D)typeof(ModelPreviewControl).GetField("_viewport", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(preview)!;
                    var before = ((ModelVisual3D)viewport.Children[1]).Transform.Transform(new Point3D());
                    preview.SetFitAdjustment(new(), selected!);
                    var after = ((ModelVisual3D)viewport.Children[1]).Transform.Transform(new Point3D());
                    Assert.True((after - before).Length > 0.001);
                    field.SetValue(preview, Activator.CreateInstance(dragType, FitGizmoMode.Rotate, 2, new Point(0, 0), new Vector(1, 0), 2d, new ModelPreparationAdjustments()));
                    move.Invoke(preview, [new Point(10, 0)]);
                    Assert.Equal(6, selected!.RotationZDegrees);
                    field.SetValue(preview, Activator.CreateInstance(dragType, FitGizmoMode.Scale, 0, new Point(0, 0), new Vector(0, -1), 1d, new ModelPreparationAdjustments()));
                    move.Invoke(preview, [new Point(0, -120)]);
                    Assert.Equal(Math.E, selected!.ScaleMultiplier, 6);
                    preview.Dispose(); result.SetResult();
                }
                catch (Exception error) { result.SetException(error); }
                finally { dispatcher.InvokeShutdown(); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await result.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
