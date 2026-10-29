namespace SkyrimModPacker.Infrastructure.ModelPreparation;

public sealed record ModelPreparationShaderSettings(double? EnvironmentStrength = null,
    double? SpecularStrength = null, double Red = 1, double Green = 1, double Blue = 1)
{
    public void Validate()
    {
        if (EnvironmentStrength is double env && (!double.IsFinite(env) || env is < 0 or > 4))
            throw new ArgumentOutOfRangeException(nameof(EnvironmentStrength));
        if (SpecularStrength is double spec && (!double.IsFinite(spec) || spec is < 0 or > 4))
            throw new ArgumentOutOfRangeException(nameof(SpecularStrength));
        foreach (var component in new[] { Red, Green, Blue })
            if (!double.IsFinite(component) || component is < 0 or > 1)
                throw new ArgumentOutOfRangeException(nameof(Red), "Specular color components must be between 0 and 1.");
    }
}
