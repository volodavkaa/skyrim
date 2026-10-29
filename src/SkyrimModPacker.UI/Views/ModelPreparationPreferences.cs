using System.IO;
using System.Text.Json;

namespace SkyrimModPacker.UI.Views;

internal sealed record ModelPreparationPreferences(
    string Archive = "", string GameData = "", string Blender = "", string Addon = "",
    string Output = "", string Texconv = "", string LastPreparedFolder = "",
    string GameCubemap = "", bool IncludeScabbard = true, bool InvertNormalY = false,
    bool DefaultShader = false, bool UseVertexColor = false, double VertexColor = 100,
    bool UseEnvironmentStrength = false, double EnvironmentStrength = 1,
    bool UseSpecular = false, double SpecularStrength = 1,
    double SpecularRed = 1, double SpecularGreen = 1, double SpecularBlue = 1,
    int MaxTextureSize = 0);

internal static class ModelPreparationPreferencesStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkyrimModPacker");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "model-preparation.json");

    public static ModelPreparationPreferences Load()
    {
        try
        {
            if (!File.Exists(FilePath) || new FileInfo(FilePath).Length > 64 * 1024)
                return new ModelPreparationPreferences();
            return JsonSerializer.Deserialize<ModelPreparationPreferences>(File.ReadAllText(FilePath)) ??
                new ModelPreparationPreferences();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return new ModelPreparationPreferences(); }
    }

    public static void Save(ModelPreparationPreferences preferences)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(preferences,
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, FilePath, overwrite: true);
    }
}
