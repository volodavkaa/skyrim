using SkyrimModPacker.Infrastructure.ModelPreparation;
using SkyrimModPacker.Core.Interfaces;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Infrastructure.Preview;
using SkyrimModPacker.UI.Controls;
using SkyrimModPacker.UI.Services;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Reflection;
using System.Numerics;
using NSubstitute;
using Xunit;

namespace SkyrimModPacker.Tests;

[Collection("WPF page")]
public class CharacterPreviewTests
{
    [Theory]
    [InlineData("sword1h", "WeaponSword")]
    [InlineData("dagger", "WeaponDagger")]
    [InlineData("axe1h", "WeaponAxe")]
    [InlineData("mace", "WeaponMace")]
    [InlineData("sword2h", "WeaponBack")]
    [InlineData("axe2h", "WeaponBack")]
    [InlineData("hammer", "WeaponBack")]
    public void UsesCorrectVanillaAttachmentForWeaponType(string weaponType, string node)
    {
        Assert.Equal(node, CharacterPreviewService.GetAttachmentNode(weaponType, true));
        Assert.Equal("WEAPON", CharacterPreviewService.GetAttachmentNode(weaponType, false));
    }

    [Theory]
    [InlineData("dagger", "WeaponDagger")]
    [InlineData("axe1h", "WeaponAxe")]
    [InlineData("mace", "WeaponMace")]
    [InlineData("sword2h", "WeaponBack")]
    public async Task SheathedPoseLoadsSelectedSkyrimAttachment(string type, string node)
    {
        var data = Environment.GetEnvironmentVariable("SKYRIM_TEST_DATA");
        var blender = Environment.GetEnvironmentVariable("SKYRIM_TEST_BLENDER");
        var addon = Environment.GetEnvironmentVariable("SKYRIM_TEST_ADDON");
        if (new[] { data, blender, addon }.Any(string.IsNullOrWhiteSpace)) return;
        var pose = await CharacterPreviewService.LoadAsync(data!, blender!, addon!, "male", "sheathed", weaponType: type);
        Assert.True(pose.IsSheathed);
        Assert.Equal(node, pose.AttachmentNode);
        Assert.True(float.IsFinite(pose.WeaponTransform.GetDeterminant()));
    }

    [Fact]
    public async Task CharacterAndWeaponRenderTogether()
    {
        var data = Environment.GetEnvironmentVariable("SKYRIM_TEST_DATA");
        var blender = Environment.GetEnvironmentVariable("SKYRIM_TEST_BLENDER");
        var addon = Environment.GetEnvironmentVariable("SKYRIM_TEST_ADDON");
        var weapon = Environment.GetEnvironmentVariable("SKYRIM_TEST_WEAPON_NIF");
        if (new[] { data, blender, addon, weapon }.Any(string.IsNullOrWhiteSpace)) return;
        var character = await CharacterPreviewService.LoadAsync(data!, blender!, addon!, "male", "1hm_idle");
        var sheathed = await CharacterPreviewService.LoadAsync(data!, blender!, addon!, "male", "sheathed");
        var back = await CharacterPreviewService.LoadAsync(data!, blender!, addon!, "male", "sheathed", weaponType: "sword2h");
        Assert.NotEqual(sheathed.WeaponTransform, back.WeaponTransform);
        var scene = SseNifReader.Read(File.ReadAllBytes(weapon!));
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    var service = Substitute.For<IModelPreviewService>();
                    service.LoadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(scene);
                    var preview = new ModelPreviewControl(service) { Width = 700, Height = 700 };
                    preview.SetCharacterPose(character);
                    await preview.LoadAsync(new ModelPreviewRequest("", "blade.nif", "blade1.nif", ""));
                    preview.SetView(new PreviewViewState(0, -5, 3.3));
                    var modelField = typeof(ModelPreviewControl).GetField("_model", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var frameBefore = ((System.Windows.Media.Media3D.Model3DGroup)modelField.GetValue(preview)!).Transform.Value;
                    var compose = typeof(ModelPreviewControl).GetMethod("ComposeScene", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    preview.SetFitAdjustment(new(), new(ScaleMultiplier: 2, OffsetX: 3, OffsetY: 4, OffsetZ: 5));
                    Assert.Equal(frameBefore, ((System.Windows.Media.Media3D.Model3DGroup)modelField.GetValue(preview)!).Transform.Value);
                    var combined = (ModelPreview)compose.Invoke(preview, [scene])!;
                    Assert.Equal(character.Meshes[0].Positions[0], combined.Meshes[0].Positions[0]);
                    var original = scene.Meshes.First(mesh => !mesh.Name.Equals("Scb", StringComparison.OrdinalIgnoreCase));
                    var transformed = combined.Meshes.First(mesh => mesh.Name == original.Name);
                    var expected = Vector3.Transform(original.Positions[0] * 2 + new Vector3(3, 4, 5), character.WeaponTransform);
                    Assert.True(Vector3.Distance(expected, transformed.Positions[0]) < 0.001);
                    preview.SetFitAdjustment(new(), new());
                    preview.SetFitGizmoMode(FitGizmoMode.Move);
                    preview.Measure(new Size(700, 700)); preview.Arrange(new Rect(0, 0, 700, 700)); preview.UpdateLayout();
                    Assert.True(preview.HasScene);
                    var screenshot = Environment.GetEnvironmentVariable("SKYRIM_CHARACTER_PREVIEW_IMAGE");
                    if (!string.IsNullOrWhiteSpace(screenshot))
                    {
                        var bitmap = new RenderTargetBitmap(700, 700, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(preview); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var file = File.Create(screenshot); encoder.Save(file);
                        preview.SetView(new PreviewViewState(90, -5, 3.3));
                        var side = new RenderTargetBitmap(700, 700, 96, 96, PixelFormats.Pbgra32);
                        side.Render(preview); var sideEncoder = new PngBitmapEncoder(); sideEncoder.Frames.Add(BitmapFrame.Create(side));
                        using var sideFile = File.Create(Path.Combine(Path.GetDirectoryName(screenshot)!, "preview-side.png")); sideEncoder.Save(sideFile);
                    }
                    preview.SetCharacterPose(sheathed);
                    var stowed = (ModelPreview)compose.Invoke(preview, [scene])!;
                    Assert.Contains(stowed.Meshes, mesh => mesh.Name == "Scb");
                    if (!string.IsNullOrWhiteSpace(screenshot))
                    {
                        var bitmap = new RenderTargetBitmap(700, 700, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(preview); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var file = File.Create(Path.Combine(Path.GetDirectoryName(screenshot)!, "preview-sheathed.png")); encoder.Save(file);
                        preview.SetCharacterPose(back);
                        var backBitmap = new RenderTargetBitmap(700, 700, 96, 96, PixelFormats.Pbgra32);
                        backBitmap.Render(preview); var backEncoder = new PngBitmapEncoder(); backEncoder.Frames.Add(BitmapFrame.Create(backBitmap));
                        using var backFile = File.Create(Path.Combine(Path.GetDirectoryName(screenshot)!, "preview-back.png")); backEncoder.Save(backFile);
                    }
                    preview.Dispose(); finished.SetResult();
                }
                catch (Exception ex) { finished.SetException(ex); }
                finally { dispatcher.InvokeShutdown(); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
    [Theory]
    [InlineData("male", "1hm_idle")]
    [InlineData("female", "1hm_idle")]
    [InlineData("male", "2hm_idle")]
    [InlineData("female", "2hm_idle")]
    [InlineData("male", "sneak1hm_idle")]
    [InlineData("female", "sneak1hm_idle")]
    [InlineData("male", "sheathed")]
    [InlineData("female", "sheathed")]
    public async Task LoadsPosedCharacterAndWeaponAttachmentFromGame(string gender, string pose)
    {
        var data = Environment.GetEnvironmentVariable("SKYRIM_TEST_DATA");
        var blender = Environment.GetEnvironmentVariable("SKYRIM_TEST_BLENDER");
        var addon = Environment.GetEnvironmentVariable("SKYRIM_TEST_ADDON");
        if (new[] { data, blender, addon }.Any(string.IsNullOrWhiteSpace)) return;
        var result = await CharacterPreviewService.LoadAsync(data!, blender!, addon!, gender, pose);
        Assert.True(result.Meshes.Count >= 3);
        Assert.True(result.Meshes.Sum(mesh => mesh.Positions.Length) > 2000);
        Assert.NotEmpty(result.Textures);
        Assert.Equal(pose == "sheathed", result.IsSheathed);
        Assert.True(result.WeaponTransform.GetDeterminant() > 0.5f);
        Assert.True(Math.Abs(result.WeaponTransform.M43) > 10);
    }
}
