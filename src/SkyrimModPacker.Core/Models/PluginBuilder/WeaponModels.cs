namespace SkyrimModPacker.Core.Models.PluginBuilder;

public sealed record WeaponModels(string ThirdPersonPath, PluginRecordReference? FirstPersonReference,
    string? FirstPersonPath, string? Error = null);

public enum ModelAvailability { Found, Missing, CheckFailed }
public enum ModelOrigin { Vanilla, Project, Unknown }
public sealed record ModelResourceInfo(string Path, ModelOrigin Origin, ModelAvailability Availability,
    bool IncludedInZip, string? Detail = null);
