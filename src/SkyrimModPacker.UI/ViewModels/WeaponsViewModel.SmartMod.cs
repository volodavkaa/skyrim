namespace SkyrimModPacker.UI.ViewModels;

public sealed partial class WeaponsViewModel
{
    // The page obtained permission to leave the manual draft before opening the modal wizard.
    public Task ReloadAfterSmartModAsync() => RunOperationAsync(async token =>
    {
        if (string.IsNullOrWhiteSpace(ProjectFilePath)) return;
        var project = await _projects.OpenAsync(ProjectFilePath, GameDataPath, token);
        if (project.IsFailure) throw new InvalidOperationException(project.Error);
        await ActivateProjectAsync(project.Value, token);
    });
}
