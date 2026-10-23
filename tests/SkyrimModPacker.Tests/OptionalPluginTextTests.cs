using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkyrimModPacker.Infrastructure.PluginBuilder;
using SkyrimModPacker.Core.Models.PluginBuilder;
using Xunit;

namespace SkyrimModPacker.Tests;

public sealed class OptionalPluginTextTests
{
    [Fact]
    public async Task RemovingOptionalSoundAndEnchantment_OmitsSubrecordsInsteadOfWritingNullLinks()
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(OptionalPluginTextTests), Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "Game", "Data");
        Directory.CreateDirectory(data);
        try
        {
            var key = ModKey.FromNameAndExtension("Skyrim.esm");
            var master = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
            var template = new Weapon(new FormKey(key, 0x900), SkyrimRelease.SkyrimSE)
            {
                EditorID = "TemplateSword", Name = "Sword", BasicStats = new WeaponBasicStats { Damage = 7, Weight = 9, Value = 25 },
                Data = new WeaponData { AnimationType = WeaponAnimationType.OneHandSword, Speed = 1, Reach = 1 },
                Model = new Model { File = @"Weapons\Iron\Sword.nif" }, EnchantmentAmount = 1000
            };
            template.AttackSound.SetTo(new FormKey(key, 0x901));
            template.ObjectEffect.SetTo(new FormKey(key, 0x902));
            master.Weapons.Add(template);
            master.SoundDescriptors.Add(new SoundDescriptor(new FormKey(key, 0x901), SkyrimRelease.SkyrimSE));
            master.ObjectEffects.Add(new ObjectEffect(new FormKey(key, 0x902), SkyrimRelease.SkyrimSE));
            master.WriteToBinary(Path.Combine(data, "Skyrim.esm"));
            var builder = new PluginBuilderService();
            var created = await builder.CreateAsync(new CreatePluginRequest { PluginFileName = "Sword.esp", OutputDirectory = Path.Combine(root, "work"), GameDataPath = data, Masters = ["Skyrim.esm"] });
            Assert.True(created.IsSuccess, created.Error);
            var cloned = await builder.CloneSwordAsync(new CloneSwordRequest { PluginPath = created.Value, GameDataPath = data, TemplateFormId = 0x900, EditorId = "NewSword", Name = "Sword" });
            Assert.True(cloned.IsSuccess, cloned.Error);
            var updated = await builder.UpdateSwordAsync(new UpdateSwordRequest
            {
                PluginPath = created.Value, GameDataPath = data, FormId = cloned.Value.FormId, EditorId = "NewSword", Name = "Sword", Damage = 7, Weight = 9, Value = 25,
                Details = new WeaponDetails { ModelPath = @"Weapons\Iron\Sword.nif" }
            });
            Assert.True(updated.IsSuccess, updated.Error);
            using var saved = SkyrimMod.CreateFromBinaryOverlay(created.Value, SkyrimRelease.SkyrimSE);
            var sword = Assert.Single(saved.Weapons);
            Assert.Null(sword.AttackSound.FormKeyNullable);
            Assert.Null(sword.ObjectEffect.FormKeyNullable);
            Assert.Null(sword.EnchantmentAmount);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task EmptyWeaponDescription_CanRoundTripAsAbsentWithoutRejectingValidPlugin()
    {
        var root = Path.Combine(Path.GetTempPath(), nameof(OptionalPluginTextTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "OptionalText.esp");
            var mod = new SkyrimMod(ModKey.FromNameAndExtension("OptionalText.esp"), SkyrimRelease.SkyrimSE);
            mod.ModHeader.Stats.NextFormID = 0x801;
            mod.Weapons.Add(new Weapon(new FormKey(mod.ModKey, 0x800), SkyrimRelease.SkyrimSE)
            {
                EditorID = "OptionalTextSword", Name = "Sword", Description = string.Empty,
                BasicStats = new WeaponBasicStats { Damage = 7, Weight = 9, Value = 25 },
                Data = new WeaponData { AnimationType = WeaponAnimationType.OneHandSword, Speed = 1, Reach = 1 }
            });
            using (var transaction = await PluginFileTransaction.AcquireAsync(path, Path.Combine(root, "Game", "Data"), CancellationToken.None))
                transaction.Write(mod, temp => mod.BeginWrite.ToPath(temp).WithLoadOrder(Array.Empty<ModKey>())
                    .WithDataFolder(root).NoNextFormIDProcessing().WithUtf8Encoding().Write(), null, false, true, CancellationToken.None);
            using var saved = SkyrimMod.Create(SkyrimRelease.SkyrimSE).FromPath(path).WithUtf8Encoding().Construct();
            var sword = Assert.Single(saved.Weapons);
            Assert.Equal("Sword", sword.Name!.String);
            Assert.True(string.IsNullOrEmpty(sword.Description?.String));
            Assert.Equal(7, sword.BasicStats!.Damage);
            Assert.Empty(Directory.GetDirectories(root, ".plugin-*"));
        }
        finally { Directory.Delete(root, true); }
    }
}
