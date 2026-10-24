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

    /// <summary>Optional absolute path to a workspace plugin outside Data; archived by filename at the ZIP root.</summary>
    public string PluginSourcePath { get; set; } = string.Empty;

    /// <summary>Optional project manifest; when set, pack its registered resources instead of scanning Data keywords.</summary>
    public string WeaponProjectPath { get; set; } = string.Empty;

    /// <summary>Apply ESL to compatible staged plugins before archiving; disabled for existing profiles.</summary>
    public bool EnableEsl { get; set; }
    public bool IncludeExportReport { get; set; }

    /// <summary>Timestamp when the profile was created.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Timestamp when the profile was last modified.</summary>
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
}
