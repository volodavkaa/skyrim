using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace SkyrimModPacker.Tests;

public sealed class SmartMaterialResearchTests
{
    [Fact]
    public void AvailableVanillaMaterialRecipesHaveRecordedSources()
    {
        var data = Environment.GetEnvironmentVariable("SKYRIM_SE_DATA"); if (string.IsNullOrEmpty(data)) return;
        var sources = new List<ISkyrimModDisposableGetter>();
        try
        {
            foreach (var name in new[] { "Skyrim.esm", "Dawnguard.esm" }.Where(n => File.Exists(Path.Combine(data, n))))
                sources.Add(SkyrimMod.CreateFromBinaryOverlay(Path.Combine(data, name), SkyrimRelease.SkyrimSE));
            var weapons = sources.SelectMany(s => s.Weapons).Where(w => w.ObjectEffect.IsNull && w.EditorID != null &&
                (w.EditorID.StartsWith("Steel") || w.EditorID.StartsWith("Ebony") || w.EditorID.Contains("Dragonbone"))).ToArray();
            var result = weapons.Select(w => new
            {
                id = w.EditorID, key = w.FormKey.ToString(), animation = w.Data?.AnimationType.ToString(), damage = w.BasicStats?.Damage,
                keywords = w.Keywords?.Select(k => sources.SelectMany(s => s.Keywords).FirstOrDefault(r => r.FormKey == k.FormKey)?.EditorID).ToArray(),
                recipes = sources.SelectMany(s => s.ConstructibleObjects).Where(r => r.CreatedObject.FormKey == w.FormKey).Select(r => new
                {
                    id = r.EditorID, key = r.FormKey.ToString(), bench = sources.SelectMany(s => s.Keywords).FirstOrDefault(k => k.FormKey == r.WorkbenchKeyword.FormKey)?.EditorID,
                    ingredients = r.Items?.Select(i => new { key = i.Item.Item.FormKey.ToString(), i.Item.Count }),
                    conditions = r.Conditions.Select(c => new { data = c.Data.GetType().Name, op = c.CompareOperator.ToString(), flags = c.Flags.ToString(), value = (c as IConditionFloatGetter)?.ComparisonValue })
                }).ToArray()
            }).ToArray();
            Assert.NotEmpty(result);
            var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/audit")); Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "smart-material-source-research.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { foreach (var source in sources) source.Dispose(); }
    }
}
