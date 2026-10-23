using System.Security.Cryptography;
using SkyrimModPacker.Core.Models.PluginBuilder;

namespace SkyrimModPacker.Core.Common;

/// <summary>Optional editor expectation, checked again by a writer after acquiring its destination lock.</summary>
public sealed class WorkspaceWriteExpectation : IDisposable
{
    private static readonly AsyncLocal<WorkspaceWriteExpectation?> Current = new();
    private readonly WorkspaceWriteExpectation? _previous;
    private readonly Dictionary<string, string?> _files;
    public bool HasWrites { get; private set; }
    public WorkspaceWriteExpectation(WorkspaceRevision revision)
    {
        _previous = Current.Value;
        var folder = Path.GetDirectoryName(revision.Context.PluginPath)!;
        _files = revision.Files.ToDictionary(p => PackingPathGuard.ResolveUnderRoot(folder, p.Key), p => p.Value, StringComparer.OrdinalIgnoreCase);
        Current.Value = this;
    }
    public static void Check(string path)
    {
        var scope = Current.Value;
        path = Path.GetFullPath(path);
        if (scope == null || !scope._files.TryGetValue(path, out var expected)) return;
        PackingPathGuard.EnsureNoLinks(path);
        using var stream = File.Exists(path) ? File.OpenRead(path) : null;
        var actual = stream == null ? null : Convert.ToHexString(SHA256.HashData(stream));
        if (actual != expected) throw new IOException("The workspace changed since it was reviewed: " + path);
    }
    public static void Written(string path)
    {
        var scope = Current.Value;
        if (scope == null) return;
        scope.HasWrites = true;
        path = Path.GetFullPath(path);
        if (!scope._files.ContainsKey(path)) return;
        using var stream = File.Exists(path) ? File.OpenRead(path) : null;
        scope._files[path] = stream == null ? null : Convert.ToHexString(SHA256.HashData(stream));
    }
    public void Dispose() => Current.Value = _previous;
}
