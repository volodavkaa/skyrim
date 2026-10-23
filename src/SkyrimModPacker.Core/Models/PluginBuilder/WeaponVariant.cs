namespace SkyrimModPacker.Core.Models.PluginBuilder;

public sealed record WeaponVariant(uint FormId, uint ParentFormId, string Label, string MaterialNotes)
{
    public string DisplayName => $"{ParentFormId:X6} → {FormId:X6}: {Label} — {MaterialNotes}";
}
