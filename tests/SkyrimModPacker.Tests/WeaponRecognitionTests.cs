using SkyrimModPacker.Core.Models.PluginBuilder;
using SkyrimModPacker.Infrastructure.Preview;
using Xunit;

namespace SkyrimModPacker.Tests;

public sealed class WeaponRecognitionTests
{
    private static WeaponFolderSource Model(string path, string? error = null) => new(Path.Combine("C:/scan", path), path.Replace('\\', '/'), 10, "SkyrimLe", error);
    [Theory]
    [InlineData("Sword.nif", WeaponKind.OneHandSword)]
    [InlineData("DarkSister_HD_2h.nif", WeaponKind.Greatsword)]
    [InlineData("SteelGreatSword.nif", WeaponKind.Greatsword)]
    [InlineData("dagger.nif", WeaponKind.Dagger)]
    [InlineData("dagger_1h.nif", WeaponKind.Dagger)]
    [InlineData("weapons/dagger/custom.nif", WeaponKind.Dagger)]
    [InlineData("War_Axe.nif", WeaponKind.WarAxe)]
    [InlineData("mace.nif", WeaponKind.Mace)]
    [InlineData("warhammer_2h.nif", WeaponKind.BattleaxeWarhammer)]
    [InlineData("BattleAxe.nif", WeaponKind.BattleaxeWarhammer)]
    [InlineData("crossbow.nif", WeaponKind.Crossbow)]
    public void FilenameEvidenceIsAlwaysASuggestion(string path, WeaponKind kind)
    {
        var proposal = Assert.Single(WeaponModelRecognition.Analyze(new("C:/scan", [Model(path)], 0)));
        Assert.Equal(kind, proposal.Kind); Assert.Equal(ModelRecognitionStatus.Suggested, proposal.Status); Assert.True(proposal.Included);
        if (path.Contains("warhammer")) Assert.Equal(SmartWeaponSubtype.Warhammer, proposal.Subtype);
    }
    [Theory]
    [InlineData("Mystery.nif", true)]
    [InlineData("sword_dagger.nif", true)]
    [InlineData("sword_scabbard.nif", false)]
    [InlineData("Sword_1st.nif", false)]
    public void UnknownConflictingAndAuxiliaryModelsStayVisible(string path, bool included)
    {
        var p = Assert.Single(WeaponModelRecognition.Analyze(new("C:/scan", [Model(path)], 0)));
        Assert.Null(p.Kind); Assert.Equal(ModelRecognitionStatus.NeedsChoice, p.Status); Assert.Equal(included, p.Included);
    }
    [Fact]
    public void PairingRequiresUniqueSiblingAndNeverConsumesTwoHandedVariant()
    {
        var scan = new WeaponFolderScan("C:/scan", [Model("A/Sword.nif"), Model("A/Sword_1st.nif"), Model("A/Sword_2h.nif"), Model("B/Sword_1st.nif")], 0);
        var proposals = WeaponModelRecognition.Analyze(scan);
        Assert.Equal(3, proposals.Count); Assert.Equal("A/Sword_1st.nif", proposals.Single(p => p.Model.RelativePath == "A/Sword.nif").FirstPerson!.RelativePath);
        Assert.Contains(proposals, p => p.Kind == WeaponKind.Greatsword); Assert.Contains(proposals, p => p.Evidence == "UnpairedFirstPerson");
        var ambiguous = WeaponModelRecognition.Analyze(scan with { Files = scan.Files.Append(Model("A/Sword_firstperson.nif")).ToArray() });
        Assert.Equal(5, ambiguous.Count); Assert.All(ambiguous, p => Assert.Null(p.FirstPerson));
    }
    [Fact]
    public void PluginHintsRequireExactMeshPathAndUniqueRecord()
    {
        var scan = new WeaponFolderScan("C:/scan", [Model("Package/meshes/custom/sword.nif")], 0) {
            RecordHints = [new("Example.esp", "CustomDagger", "Named dagger", WeaponKind.Dagger, "custom/sword.nif")] };
        var proposal = Assert.Single(WeaponModelRecognition.Analyze(scan)); Assert.Equal(ModelRecognitionStatus.Record, proposal.Status); Assert.Equal(WeaponKind.Dagger, proposal.Kind); Assert.Equal("Named dagger", proposal.Name);
        Assert.Equal(ModelRecognitionStatus.Suggested, Assert.Single(WeaponModelRecognition.Analyze(scan with { RecordHints = [scan.RecordHints[0] with { ModelPath = "other/sword.nif" }] })).Status);
        Assert.Equal(ModelRecognitionStatus.NeedsChoice, Assert.Single(WeaponModelRecognition.Analyze(scan with { RecordHints = [scan.RecordHints[0], scan.RecordHints[0] with { EditorId = "Other" }] })).Status);
        Assert.False(Assert.Single(WeaponModelRecognition.Analyze(scan with { Files = [scan.Files[0] with { Error = "Truncated NIF" }] })).Included);
    }
    [Fact]
    public void ExplicitFirstPersonSubdirectoryCanBePairedWithoutCrossingOtherFolders()
    {
        var proposals = WeaponModelRecognition.Analyze(new("C:/scan", [Model("meshes/Custom/Sword.nif"), Model("meshes/Custom/1stperson/Sword.nif"), Model("meshes/Other/1stperson/Sword.nif")], 0));
        Assert.Equal(2, proposals.Count); Assert.Equal("meshes/Custom/1stperson/Sword.nif", proposals.Single(p => p.Included).FirstPerson!.RelativePath);
        Assert.Single(proposals, p => !p.Included);
    }
    [Fact]
    public async Task PreImportPreviewReadsBc7FromExactArchivePathWithoutModifyingFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "Recognition", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "pack/textures/custom")); Directory.CreateDirectory(Path.Combine(root, "pack/meshes"));
            var nif = PreviewFixtures.Nif(diffuse: "textures/custom/blade.dds"); var dds = PreviewFixtures.Bc7();
            var model = Path.Combine(root, "pack/meshes/Sword.nif"); var texture = Path.Combine(root, "pack/textures/custom/blade.dds");
            File.WriteAllBytes(model, nif); File.WriteAllBytes(texture, dds);
            var scan = new WeaponFolderScan(root, [new(model, "pack/meshes/Sword.nif", nif.Length, "SSE", null), new(texture, "pack/textures/custom/blade.dds", dds.Length, "BC7", null)], 0);
            var preview = await new FolderModelPreviewService(scan).LoadAsync("", "pack/meshes/Sword.nif", ""); Assert.Single(preview.Textures); Assert.NotEmpty(preview.Meshes);
            var ambiguous = await new FolderModelPreviewService(scan with { Files = scan.Files.Append(scan.Files[1] with { RelativePath = "another/textures/custom/blade.dds" }).ToArray() }).LoadAsync("", "pack/meshes/Sword.nif", "");
            Assert.Empty(ambiguous.Textures); Assert.Contains(ambiguous.Warnings, w => w.Contains("Ambiguous"));
            Assert.Equal(nif, File.ReadAllBytes(model)); Assert.Equal(dds, File.ReadAllBytes(texture));
        }
        finally { Directory.Delete(root, true); }
    }
}
