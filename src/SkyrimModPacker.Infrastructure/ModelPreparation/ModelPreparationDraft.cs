using SkyrimModPacker.Core.Common;

namespace SkyrimModPacker.Infrastructure.ModelPreparation;

/// <summary>Validated temporary result, published only after the user chooses Save.</summary>
public sealed class ModelPreparationDraft : IDisposable
{
    private readonly string _outputDirectory;
    private readonly string _slug;
    private bool _disposed;
    private bool _published;

    internal ModelPreparationDraft(string stagingDirectory, string outputDirectory, string slug,
        string thirdPersonPath, string firstPersonPath)
    {
        StagingDirectory = stagingDirectory;
        _outputDirectory = outputDirectory;
        _slug = slug;
        ThirdPersonPath = thirdPersonPath;
        FirstPersonPath = firstPersonPath;
    }

    public string StagingDirectory { get; }
    public string ThirdPersonPath { get; }
    public string FirstPersonPath { get; }

    public string Publish()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_published) throw new InvalidOperationException("This model has already been saved.");
        PackingPathGuard.EnsureNoLinks(_outputDirectory);
        PackingPathGuard.EnsureNoLinks(StagingDirectory);
        var destination = Path.Combine(_outputDirectory,
            "prepared-" + _slug + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.Move(StagingDirectory, destination);
        _published = true;
        return destination;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_published && Directory.Exists(StagingDirectory)) Directory.Delete(StagingDirectory, true);
    }
}
