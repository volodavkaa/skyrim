using System.Text.Json;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure.Persistence;
using Xunit;

namespace SkyrimModPacker.Tests;

public sealed class WeaponPresetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WeaponPresetTests", Guid.NewGuid().ToString("N"));
    private WeaponPreset Preset => new() { Name = "Personal sword", Kind = WeaponKind.OneHandSword, Damage = 20, Weight = 3.5f, Value = 100, Speed = 1.2f, Reach = 1.1f };
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Theory]
    [InlineData(WeaponKind.OneHandSword)] [InlineData(WeaponKind.Dagger)] [InlineData(WeaponKind.WarAxe)] [InlineData(WeaponKind.Mace)]
    [InlineData(WeaponKind.Greatsword)] [InlineData(WeaponKind.BattleaxeWarhammer)] [InlineData(WeaponKind.Bow)] [InlineData(WeaponKind.Crossbow)]
    public async Task Presets_RoundTripForEverySupportedType_AndDelete(WeaponKind kind)
    {
        var repository = new JsonWeaponPresetRepository(_root);
        var preset = Preset with { Kind = kind, Keywords = [new("Skyrim.esm", 123)], Sound = new("Skyrim.esm", 456) };
        await repository.SaveAsync(preset);
        var restored = Assert.Single(await new JsonWeaponPresetRepository(_root).LoadAsync());
        Assert.Equal(JsonSerializer.Serialize(preset), JsonSerializer.Serialize(restored));
        await repository.DeleteAsync(preset.Id); Assert.Empty(await repository.LoadAsync());
    }

    [Theory]
    [InlineData("name")] [InlineData("kind")] [InlineData("weight")] [InlineData("speed")] [InlineData("reach")] [InlineData("charge")] [InlineData("version")]
    public async Task Presets_InvalidParametersCannotReplaceSavedSets(string field)
    {
        var repository = new JsonWeaponPresetRepository(_root); var valid = Preset; await repository.SaveAsync(valid);
        var invalid = field switch { "name" => valid with { Name = "" }, "kind" => valid with { Kind = (WeaponKind)8 }, "weight" => valid with { Weight = float.NaN },
            "speed" => valid with { Speed = 0 }, "reach" => valid with { Reach = float.PositiveInfinity }, "charge" => valid with { Charge = 1 }, _ => valid with { Version = 2 } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveAsync(invalid));
        Assert.Equal(valid.Id, Assert.Single(await repository.LoadAsync()).Id);
    }

    [Fact]
    public async Task Presets_DuplicateNameAndCorruptJsonAreNotSilentlyOverwritten()
    {
        var repository = new JsonWeaponPresetRepository(_root); await repository.SaveAsync(Preset);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveAsync(Preset with { Name = "PERSONAL SWORD" }));
        var path = Path.Combine(_root, "weapon-presets.json"); await File.WriteAllTextAsync(path, "broken JSON");
        await Assert.ThrowsAsync<JsonException>(() => repository.SaveAsync(Preset));
        Assert.Equal("broken JSON", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Presets_ConcurrentAddsAreRetained_AndCancellationPreservesFile()
    {
        var first = new JsonWeaponPresetRepository(_root); var second = new JsonWeaponPresetRepository(_root);
        await Task.WhenAll(first.SaveAsync(Preset with { Name = "A" }), second.SaveAsync(Preset with { Name = "B" }));
        Assert.Equal(2, (await first.LoadAsync()).Count);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.SaveAsync(Preset, cts.Token));
        Assert.Equal(2, (await first.LoadAsync()).Count);
    }
}
