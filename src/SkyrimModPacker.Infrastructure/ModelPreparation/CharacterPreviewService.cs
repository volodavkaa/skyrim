using System.Numerics;
using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using SkyrimModPacker.Core.Common;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Infrastructure.Preview;

namespace SkyrimModPacker.Infrastructure.ModelPreparation;

public sealed record CharacterPose(IReadOnlyList<PreviewMesh> Meshes, Matrix4x4 WeaponTransform,
    IReadOnlyList<PreviewTexture> Textures, IReadOnlyList<string> Warnings, bool IsSheathed = false,
    string AttachmentNode = "WEAPON");

public static class CharacterPreviewService
{
    public static async Task<CharacterPose> LoadAsync(string gameData, string blender, string addon,
        string gender, string animation, CancellationToken token = default, string? scriptPath = null,
        string weaponType = "sword1h")
    {
        if (gender is not ("male" or "female") || animation is not ("1hm_idle" or "2hm_idle" or "sneak1hm_idle" or "sheathed"))
            throw new ArgumentException("Unsupported character or pose.");
        var attachment = GetAttachmentNode(weaponType, animation == "sheathed");
        if (!Directory.Exists(gameData) || !File.Exists(blender) || !Directory.Exists(Path.Combine(addon, "io_scene_nifly")))
            throw new InvalidOperationException("Set Skyrim Data, Blender and PyNifly paths first.");
        var script = scriptPath ?? Path.Combine(AppContext.BaseDirectory, "ModelPreparation", "character_hand_preview.py");
        if (!File.Exists(script)) throw new FileNotFoundException("Character preview script is missing", script);
        var temp = Path.Combine(Path.GetTempPath(), "skyrim-character-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var basePath = "meshes/actors/character/character assets/";
            var resources = new[] { (basePath + "skeleton.nif", "skeleton.nif"),
                (basePath + gender + "body_1.nif", gender + "body_1.nif"),
                (basePath + gender + "hands_1.nif", gender + "hands_1.nif"),
                (basePath + gender + "feet_1.nif", gender + "feet_1.nif"),
                (basePath + gender + "head.nif", gender + "head.nif"),
                (basePath + "skeleton.hkx", "skeleton.hkx"),
                (animation == "sheathed" ? "meshes/actors/character/animations/" + gender + "/mt_idle.hkx" :
                    "meshes/actors/character/animations/" + animation + ".hkx", animation + ".hkx") };
            await Task.Run(() => Extract(gameData, temp, resources, token), token);
            var output = Path.Combine(temp, "pose.json");
            await ModelPreparationRunner.RunBlenderAsync(blender, script, token, addon, temp, output, gender, animation, attachment);
            if (!File.Exists(output) || new FileInfo(output).Length > 32 * 1024 * 1024)
                throw new InvalidDataException("Character pose output is missing or too large.");
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(output, token));
            var meshes = new List<PreviewMesh>(); int totalVertices = 0, totalIndices = 0;
            foreach (var row in json.RootElement.GetProperty("meshes").EnumerateArray())
            {
                var vertices = row.GetProperty("positions").EnumerateArray().Select(value => new Vector3(
                    value[0].GetSingle(), value[1].GetSingle(), value[2].GetSingle())).ToArray();
                var indices = row.GetProperty("indices").EnumerateArray().Select(value => value.GetInt32()).ToArray();
                totalVertices += vertices.Length; totalIndices += indices.Length;
                if (totalVertices > 50000 || totalIndices > 150000 || vertices.Length == 0 || indices.Length % 3 != 0 ||
                    vertices.Any(v => !float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z)) ||
                    indices.Any(index => index < 0 || index >= vertices.Length))
                    throw new InvalidDataException("Invalid character preview geometry.");
                var uv = row.GetProperty("uv").EnumerateArray().Select(value => new Vector2(value[0].GetSingle(), value[1].GetSingle())).ToArray();
                if (uv.Length != vertices.Length || uv.Any(value => !float.IsFinite(value.X) || !float.IsFinite(value.Y)))
                    throw new InvalidDataException("Invalid character UV coordinates.");
                var diffuse = row.GetProperty("diffuse").ValueKind == JsonValueKind.String ? row.GetProperty("diffuse").GetString() : null;
                meshes.Add(new("Character: " + row.GetProperty("name").GetString(), vertices, uv, indices, diffuse));
            }
            if (meshes.Count == 0 || meshes.Count > 64) throw new InvalidDataException("Character preview is empty.");
            var m = json.RootElement.GetProperty("weapon_matrix");
            var matrix = new Matrix4x4(m[0][0].GetSingle(), m[1][0].GetSingle(), m[2][0].GetSingle(), 0,
                m[0][1].GetSingle(), m[1][1].GetSingle(), m[2][1].GetSingle(), 0,
                m[0][2].GetSingle(), m[1][2].GetSingle(), m[2][2].GetSingle(), 0,
                m[0][3].GetSingle(), m[1][3].GetSingle(), m[2][3].GetSingle(), 1);
            if (!float.IsFinite(matrix.M41) || !float.IsFinite(matrix.M42) || !float.IsFinite(matrix.M43) ||
                !float.IsFinite(matrix.GetDeterminant()) || Math.Abs(matrix.GetDeterminant()) < 0.001)
                throw new InvalidDataException("Invalid weapon attachment transform.");
            var textures = new List<PreviewTexture>(); var warnings = new List<string>(); long bytes = 0;
            foreach (var path in meshes.Select(mesh => mesh.DiffuseTexture).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var texture = DdsPreviewDecoder.Decode(path, ModelPreviewService.ReadGameTexture(gameData, path, token), token);
                    bytes += texture.BgraPixels.Length;
                    if (bytes > 64 * 1024 * 1024) throw new InvalidDataException("Character texture preview budget exceeded.");
                    textures.Add(texture);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                { warnings.Add("Character texture " + path + ": " + error.Message); }
            }
            return new CharacterPose(meshes, matrix, textures, warnings, animation == "sheathed", attachment);
        }
        finally { await ModelPreparationTextures.DeleteTemporaryDirectoryAsync(temp); }
    }

    public static string GetAttachmentNode(string weaponType, bool sheathed)
    {
        _ = WeaponTemplateCatalog.GetExportable(weaponType);
        if (!sheathed) return "WEAPON";
        return weaponType switch
        {
            "sword1h" => "WeaponSword", "dagger" => "WeaponDagger",
            "axe1h" => "WeaponAxe", "mace" => "WeaponMace",
            "sword2h" or "axe2h" or "hammer" => "WeaponBack",
            _ => throw new ArgumentException("Unsupported weapon type.", nameof(weaponType))
        };
    }

    private static void Extract(string data, string destination, (string source, string name)[] resources, CancellationToken token)
    {
        var remaining = resources.ToDictionary(item => item.source.Replace('/', '\\'), item => item.name, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in remaining.ToArray())
        {
            var loose = PackingPathGuard.ResolveUnderRoot(data, pair.Key.Replace('\\', '/'));
            if (!File.Exists(loose)) continue;
            using var input = File.OpenRead(loose); WriteBounded(input, Path.Combine(destination, pair.Value), token);
            remaining.Remove(pair.Key);
        }
        foreach (var bsa in Directory.EnumerateFiles(data, "Skyrim - *.bsa").Where(path =>
            Path.GetFileName(path).StartsWith("Skyrim - Meshes", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path).Equals("Skyrim - Animations.bsa", StringComparison.OrdinalIgnoreCase)))
        {
            if (remaining.Count == 0) break;
            token.ThrowIfCancellationRequested(); PackingPathGuard.EnsureNoLinks(bsa);
            var archive = Archive.CreateReader(GameRelease.SkyrimSE, bsa);
            try
            {
                foreach (var pair in remaining.ToArray())
                {
                    if (!archive.TryGetFolder(Path.GetDirectoryName(pair.Key)!, out var folder)) continue;
                    var file = folder.Files.FirstOrDefault(item => item.Path.Replace('/', '\\').Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
                    if (file == null) continue;
                    using var input = file.AsStream(); WriteBounded(input, Path.Combine(destination, pair.Value), token);
                    remaining.Remove(pair.Key);
                }
            }
            finally { (archive as IDisposable)?.Dispose(); }
        }
        if (remaining.Count != 0) throw new FileNotFoundException("Missing Skyrim character resources: " + string.Join(", ", remaining.Keys));
    }

    private static void WriteBounded(Stream input, string destination, CancellationToken token)
    {
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
        var buffer = new byte[81920]; long total = 0; int count;
        while ((count = input.Read(buffer)) > 0)
        {
            token.ThrowIfCancellationRequested(); total += count;
            if (total > 32 * 1024 * 1024) throw new InvalidDataException("Character resource exceeds size limit.");
            output.Write(buffer, 0, count);
        }
        if (total == 0) throw new InvalidDataException("Character resource is empty.");
    }
}
