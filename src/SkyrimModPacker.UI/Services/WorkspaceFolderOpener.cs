using System.Diagnostics;
namespace SkyrimModPacker.UI.Services;

public interface IWorkspaceFolderOpener { void Open(string folder); }
public sealed class WorkspaceFolderOpener : IWorkspaceFolderOpener
{
    public void Open(string folder) => Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
}
