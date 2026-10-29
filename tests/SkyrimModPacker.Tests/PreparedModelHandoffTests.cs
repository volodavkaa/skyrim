using System.Text.Json;
using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.UI.Services;
using Xunit;

namespace SkyrimModPacker.Tests;

public sealed class PreparedModelHandoffTests
{
    [Theory]
    [InlineData("sword1h", WeaponKind.OneHandSword, null)]
    [InlineData("dagger", WeaponKind.Dagger, null)]
    [InlineData("sword2h", WeaponKind.Greatsword, null)]
    [InlineData("axe1h", WeaponKind.WarAxe, null)]
    [InlineData("mace", WeaponKind.Mace, null)]
    [InlineData("axe2h", WeaponKind.BattleaxeWarhammer, SmartWeaponSubtype.Battleaxe)]
    [InlineData("hammer", WeaponKind.BattleaxeWarhammer, SmartWeaponSubtype.Warhammer)]
    public void PreparedPairFlowsIntoOneImportRequest(string weaponType, WeaponKind expectedKind,
        SmartWeaponSubtype? expectedSubtype)
    {
        var root = Path.Combine(Path.GetTempPath(), "smp-handoff-" + Guid.NewGuid().ToString("N"));
        try
        {
            var meshes = Path.Combine(root, "meshes", "sample");
            Directory.CreateDirectory(meshes);
            File.WriteAllBytes(Path.Combine(meshes, "third.nif"), [1]);
            File.WriteAllBytes(Path.Combine(meshes, "first.nif"), [2]);
            File.WriteAllText(Path.Combine(root, "report.json"), JsonSerializer.Serialize(new
            {
                weapon_type = weaponType, model = "sample_weapon",
                views = new[] { new { output_nif = "meshes/sample/third.nif" },
                    new { output_nif = "meshes/sample/first.nif" } }
            }));

            var package = PreparedModelHandoff.Read(root);
            var request = package.Request(Path.Combine(root, "project", "mod-project.json"),
                "Sample.esp", true);
            Assert.Equal(expectedKind, package.Kind);
            Assert.Equal(expectedSubtype, package.Subtype);
            Assert.Equal(Path.Combine(meshes, "third.nif"), request.ThirdPersonSource);
            Assert.Equal(Path.Combine(meshes, "first.nif"), request.FirstPersonSource);
            Assert.False(request.ShareModel);
            Assert.Single(request.Items);
            Assert.Equal("sample weapon", request.Items[0].Name);
            Assert.Equal(expectedSubtype, request.Items[0].Subtype);
            Assert.StartsWith("SMP_", request.Items[0].EditorId);
            Assert.Empty(package.Request(request.ProjectPath, null, false).Items);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void PreparedReportCannotSelectModelOutsideItsFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "smp-handoff-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "report.json"), JsonSerializer.Serialize(new
            {
                weapon_type = "sword1h", model = "sample",
                views = new[] { new { output_nif = "../other.nif" },
                    new { output_nif = "meshes/sample/first.nif" } }
            }));
            Assert.Throws<InvalidDataException>(() => PreparedModelHandoff.Read(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
