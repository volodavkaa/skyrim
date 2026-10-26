namespace SkyrimModPacker.Infrastructure.ModelPreparation;

/// <summary>Offsets applied after automatic placement, around the template grip.</summary>
public sealed record ModelPreparationAdjustments(
    double ScaleMultiplier = 1,
    double RotationXDegrees = 0,
    double RotationYDegrees = 0,
    double RotationZDegrees = 0,
    double OffsetX = 0,
    double OffsetY = 0,
    double OffsetZ = 0,
    double SourceGripFraction = 0.11,
    bool ReverseLongAxis = false)
{
    public void Validate()
    {
        if (!double.IsFinite(ScaleMultiplier) || ScaleMultiplier is < 0.25 or > 4)
            throw new ArgumentOutOfRangeException(nameof(ScaleMultiplier), "Scale multiplier must be between 0.25 and 4.");
        foreach (var value in new[] { RotationXDegrees, RotationYDegrees, RotationZDegrees })
            if (!double.IsFinite(value) || value is < -180 or > 180)
                throw new ArgumentOutOfRangeException(nameof(RotationXDegrees), "Rotation must be between -180 and 180 degrees.");
        foreach (var value in new[] { OffsetX, OffsetY, OffsetZ })
            if (!double.IsFinite(value) || value is < -100 or > 100)
                throw new ArgumentOutOfRangeException(nameof(OffsetX), "Offset must be between -100 and 100 NIF units.");
        if (!double.IsFinite(SourceGripFraction) || SourceGripFraction is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(SourceGripFraction), "Grip fraction must be between 0 and 1.");
    }
}
