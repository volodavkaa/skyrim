using NSubstitute;
using SkyrimModPacker.Infrastructure.PluginBuilder;
using SkyrimModPacker.UI.Services;
using SkyrimModPacker.UI.ViewModels;
using Xunit;

namespace SkyrimModPacker.Tests;

public partial class WeaponsViewModelTests
{
    [Fact]
    public async Task PreviewUsesCurrentUnsavedIndependentPathsWithoutWritingPluginOrProject()
    {
        var source = await PrepareAsync(); var presenter = Substitute.For<IModelPreviewPresenter>();
        var vm = new WeaponsViewModel(_builder, _settings, _profiles, new WeaponProjectService(_builder), previewPresenter: presenter);
        await vm.InitializeCommand.ExecuteAsync(null); await vm.OpenProjectCommand.ExecuteAsync(source.ProjectFilePath);
        vm.ModelPath = @"Custom\third.nif"; vm.FirstPersonModelPath = @"Custom\first.nif"; vm.Damage = "unfinished";
        var plugin = File.ReadAllBytes(vm.PluginPath); var metadata = File.ReadAllBytes(vm.ProjectFilePath);
        await vm.PreviewModelCommand.ExecuteAsync(null); Assert.Null(vm.ErrorMessage);
        presenter.Received(1).Show(Arg.Is<ModelPreviewRequest>(r => r.ThirdPersonPath == @"Custom\third.nif" && r.FirstPersonPath == @"Custom\first.nif" && r.GameDataPath == DataPath));
        Assert.True(vm.HasUnsavedChanges); Assert.Equal("unfinished", vm.Damage);
        Assert.Equal(plugin, File.ReadAllBytes(vm.PluginPath)); Assert.Equal(metadata, File.ReadAllBytes(vm.ProjectFilePath));
    }

    [Fact]
    public async Task PreviewPresenterFailureIsReportedWithoutEscapingTheCommand()
    {
        var presenter = Substitute.For<IModelPreviewPresenter>(); presenter.When(p => p.Show(Arg.Any<ModelPreviewRequest>())).Do(_ => throw new IOException("Injected preview error"));
        var vm = new WeaponsViewModel(_builder, _settings, _profiles, new WeaponProjectService(_builder), previewPresenter: presenter);
        await vm.InitializeCommand.ExecuteAsync(null); await vm.PreviewModelCommand.ExecuteAsync(null);
        Assert.Contains("Injected preview error", vm.ErrorMessage);
    }
}
