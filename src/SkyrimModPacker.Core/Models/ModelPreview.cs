using System.Numerics;

namespace SkyrimModPacker.Core.Models;

public sealed record PreviewMesh(string Name, Vector3[] Positions, Vector2[] TextureCoordinates, int[] Indices, string? DiffuseTexture);
public sealed record PreviewTexture(string Path, int Width, int Height, byte[] BgraPixels);
public sealed record PreviewTextureDependency(string Path, bool? Available, string? Detail = null);
public sealed record ModelPreview(IReadOnlyList<PreviewMesh> Meshes, IReadOnlyList<PreviewTexture> Textures, IReadOnlyList<string> Warnings)
{
    public IReadOnlyList<string> TexturePaths { get; init; } = [];
    public bool TextureDependenciesComplete { get; init; }
    public IReadOnlyList<PreviewTextureDependency> TextureDependencies { get; init; } = [];
}
