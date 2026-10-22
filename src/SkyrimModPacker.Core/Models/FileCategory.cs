namespace SkyrimModPacker.Core.Models;

/// <summary>
/// Categorizes a scanned file by its type within the mod structure.
/// </summary>
public enum FileCategory
{
    /// <summary>Mesh file (.nif) from the meshes directory.</summary>
    Mesh,

    /// <summary>Texture file (.dds, .png) from the textures directory.</summary>
    Texture,

    /// <summary>Plugin file (.esp, .esl, .esm).</summary>
    Plugin,

    /// <summary>Other file types (sound, SKSE, etc.) — reserved for future use.</summary>
    Other
}
