using System.Numerics;
using SkyrimModPacker.Core.Models;
using Xunit;

namespace SkyrimModPacker.Tests;

public sealed class ModelPreparationTransformTests
{
    [Fact]
    public void SuggestedFitAlignsLongAxisAndExplicitGrip()
    {
        var source = ModelGeometryBounds.Measure([new(0, 0, -1), new(0, 0, 9)]);
        var template = ModelGeometryBounds.Measure([new(0, -12, 0), new(0, 68, 0)]);
        var fit = ModelPreparationTransform.Suggest(source, template,
            new(0, 0, -1), new(0, -12, 0), Vector3.UnitZ, Vector3.UnitY);

        Assert.Equal(8, fit.Scale);
        Assert.True(Vector3.Distance(fit.Apply(new(0, 0, -1)), new(0, -12, 0)) < 0.0001f);
        Assert.True(Vector3.Distance(fit.Apply(new(0, 0, 9)), new(0, 68, 0)) < 0.0001f);
    }

    [Fact]
    public void OppositeDirectionsDoNotProduceNan()
    {
        var bounds = ModelGeometryBounds.Measure([Vector3.Zero, Vector3.UnitY]);
        var fit = ModelPreparationTransform.Suggest(bounds, bounds,
            Vector3.Zero, Vector3.One, Vector3.UnitY, -Vector3.UnitY);
        Assert.True(Vector3.Distance(fit.Apply(Vector3.Zero), Vector3.One) < 0.0001f);
        Assert.True(Vector3.Distance(fit.Apply(Vector3.UnitY), Vector3.One - Vector3.UnitY) < 0.0001f);
    }

    [Fact]
    public void InvalidMeasurementsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => ModelGeometryBounds.Measure([]));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModelGeometryBounds.Measure([new(float.NaN, 0, 0)]));
        var bounds = ModelGeometryBounds.Measure([Vector3.Zero, Vector3.UnitZ]);
        Assert.Throws<ArgumentException>(() => ModelPreparationTransform.Suggest(bounds, bounds,
            Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.UnitZ));
    }
}
