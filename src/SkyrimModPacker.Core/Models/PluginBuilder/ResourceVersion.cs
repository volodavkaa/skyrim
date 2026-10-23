namespace SkyrimModPacker.Core.Models.PluginBuilder;

public sealed record ResourceVersion(Guid Id, string RelativePath, string Sha256, DateTime CreatedUtc)
{
    public string DisplayName => $"{CreatedUtc.ToLocalTime():g} — {RelativePath}";
}
