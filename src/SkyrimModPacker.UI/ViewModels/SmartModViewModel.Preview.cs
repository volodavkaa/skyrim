using System.IO;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Infrastructure.Preview;

namespace SkyrimModPacker.UI.ViewModels;

public sealed partial class SmartModViewModel
{
    private CancellationTokenSource? _thumbnailCancellation;
    public string PreviewIdentity => ProjectPath + "|" + SelectedItem?.Id + "|" + SelectedItem?.ModelPath + "|" + SelectedItem?.FirstPersonPath + "|" +
        string.Join(";", _document.SourceRevision?.Files.Select(p => p.Key + "=" + p.Value) ?? []);
    public async Task RefreshThumbnailsAsync()
    {
        _thumbnailCancellation?.Cancel(); _thumbnailCancellation?.Dispose();
        _thumbnailCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _thumbnailCancellation.Token;
        if (_thumbnails == null || !HasProject) return;
        var project = ProjectDirectory;
        try
        {
            foreach (var row in Items.ToArray())
            {
                var model = row.ModelPath;
                if (string.IsNullOrWhiteSpace(model)) { row.Thumbnail = null; continue; }
                var notice = Label("Smart_ImportOnly");
                try
                {
                    var relative = model.Replace('\\', '/'); if (!relative.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase)) relative = "meshes/" + relative;
                    var path = PackingPathGuard.ResolveUnderRoot(project, relative);
                    if (File.Exists(path))
                    {
                        var header = NifCompatibilityInspector.Read(path);
                        notice = Label(header.Family switch { NifFormatFamily.SkyrimLe => "Smart_NifLe", NifFormatFamily.SkyrimSe => "Smart_NifSe", _ => "Smart_NifUnknown" });
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { notice += " " + ex.Message; }
                var result = await _thumbnails.CreateAsync(project, model, _gameData, token);
                token.ThrowIfCancellationRequested();
                if (Items.Contains(row) && row.ModelPath == model)
                {
                    row.Thumbnail = result.Image;
                    row.PreviewNotice = notice + Environment.NewLine + (result.Error == null ? Label("Smart_PreviewAvailable") : Label("Preview_Failed") + " " + result.Error);
                }
            }
        }
        catch (OperationCanceledException) { }
    }
}
