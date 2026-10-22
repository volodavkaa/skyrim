namespace SkyrimModPacker.Core.Models;

/// <summary>
/// Represents a mod profile containing all information needed to scan and pack a mod.
/// </summary>
public sealed class ModProfile
{
    /// <summary>Unique identifier for the profile.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Display name of the mod (e.g. "MyAxe").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Mod version string (e.g. "1.0").</summary>
    public string Version { get; set; } = "1.0";

    /// <summary>
    /// Keywords used to match folders/files in meshes and textures directories.
    /// </summary>
    public List<string> Keywords { get; set; } = [];

    /// <summary>Plugin filename with extension (.esp, .esl, .esm).</summary>
    public string PluginFileName { get; set; } = string.Empty;

    /// <summary>Timestamp when the profile was created.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Timestamp when the profile was last modified.</summary>
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
}
