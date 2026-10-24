namespace SkyrimModPacker.Core.Interfaces;

public interface IReportedArchiveService
{
    Task CreateAsync(IArchiver archiver, PackingContext context, IReadOnlyList<(string SourcePath, string EntryPath)> files, CancellationToken token = default);
}
