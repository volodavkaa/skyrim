namespace SkyrimModPacker.Core.Models.PluginBuilder;

/// <summary>Duplicates the saved weapon and its recipes with fresh owned identities.</summary>
public sealed record DuplicateWeaponRequest(string PluginPath, string GameDataPath, uint FormId, string? ProjectFilePath = null);
