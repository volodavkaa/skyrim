namespace SkyrimModPacker.Core.Models;

/// <summary>
/// Represents a file discovered during the scan phase.
/// Stores its absolute path, relative path (from Data\), and selection state.
/// </summary>
public sealed class ScannedFile
{
    /// <summary>Absolute path to the file on disk.</summary>
    public string AbsolutePath { get; set; } = string.Empty;

    /// <summary>Relative path from the Skyrim Data folder (e.g. "meshes\weapons\MyAxe\blade.nif").</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>File size in bytes.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Whether the file is selected for packing.</summary>
    public bool IsSelected { get; set; } = true;

    /// <summary>Category of the file (Mesh, Texture, Plugin, Other).</summary>
    public FileCategory Category { get; set; }
}
