using System.IO.Compression;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SkyrimModPacker.Application.Services;
using SkyrimModPacker.Core.Models;
using SkyrimModPacker.Infrastructure.PluginBuilder;
using Xunit;

namespace SkyrimModPacker.Tests;

public partial class WeaponProjectTests
{
    public static IEnumerable<object[]> InvalidModelPackingCases() =>
        from failure in new[] { "missing-first", "missing-third", "empty-first", "empty-third", "unsafe-path", "archive-conflict" }
        from staging in new[] { false, true }
        from esl in new[] { false, true }
        select new object[] { failure, staging, esl };

    [Theory]
    [MemberData(nameof(InvalidModelPackingCases))]
    public async Task Models_InvalidAfterSuccessfulPreview_BlocksEveryPackingMode_AndKeepsPreviousZip(string failure, bool staging, bool esl)
    {
        AddVanillaFirstPerson();
        var p = (await Service.CreateAsync(Request())).Value;
        var first = (await Service.ImportModelAsync(p.ProjectFilePath, await AssetAsync(@"meshes\Custom\First.nif", "first"), Data)).Value;
        var third = (await Service.ImportModelAsync(p.ProjectFilePath, await AssetAsync(@"meshes\Custom\Third.nif", "third"), Data)).Value;
        Assert.True((await CloneModelAsync(p, first: first, third: third)).IsSuccess);
        using var provider = PackingProvider();
        var pipeline = provider.GetRequiredService<PackingPipeline>();
        var settings = new AppSettings { SkyrimDataPath = Data, OutputDirectory = Path.Combine(_root, "archives"), UseTempDirectory = staging };
        var profile = PackingProfile(p, esl);
        var good = await pipeline.ExecuteAsync(profile, settings);
        Assert.True(good.IsSuccess, string.Join("; ", good.Errors));
        var archive = File.ReadAllBytes(good.OutputArchivePath);
        Assert.True((await Service.GetResourceCatalogAsync(p.ProjectFilePath, Data)).Value.CanPack);
        var folder = Path.GetDirectoryName(p.PluginPath)!;
        if (failure.StartsWith("missing-") || failure.StartsWith("empty-"))
        {
            var target = Path.Combine(folder, "meshes", failure.EndsWith("first") ? first : third);
            if (failure.StartsWith("missing-")) File.Delete(target);
            else File.WriteAllBytes(target, []);
        }
        else
        {
            var json = JsonNode.Parse(File.ReadAllText(p.ProjectFilePath))!.AsObject();
            if (failure == "unsafe-path") json["Resources"]!.AsArray().Add("meshes/../../outside.nif");
            else
            {
                // Both registrations are valid individually; together a ZIP file would also be a directory.
                var nested = Path.Combine(folder, "meshes", "Custom", "parent.nif", "child.nif");
                Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
                File.WriteAllText(nested, "nested");
                json["Resources"]!.AsArray().Add("meshes/Custom/parent.nif");
                json["Resources"]!.AsArray().Add("meshes/Custom/parent.nif/child.nif");
            }
            File.WriteAllText(p.ProjectFilePath, json.ToJsonString());
        }
        var plugin = File.ReadAllBytes(p.PluginPath);
        var metadata = File.ReadAllBytes(p.ProjectFilePath);
        var master = File.ReadAllBytes(Path.Combine(Data, "Skyrim.esm"));
        var result = await pipeline.ExecuteAsync(profile, settings);
        Assert.False(result.IsSuccess);
        Assert.NotEmpty(result.Errors);
        Assert.Equal(archive, File.ReadAllBytes(good.OutputArchivePath));
        Assert.Single(Directory.GetFiles(settings.OutputDirectory, "*.zip"));
        Assert.Equal(plugin, File.ReadAllBytes(p.PluginPath));
        Assert.Equal(metadata, File.ReadAllBytes(p.ProjectFilePath));
        Assert.Equal(master, File.ReadAllBytes(Path.Combine(Data, "Skyrim.esm")));
        Assert.Empty(Directory.GetDirectories(folder, ".assets-*"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Models_RejectedAndCancelledImport_ReopenAndPackOriginalSharedBytes(bool staging, bool esl)
    {
        AddVanillaFirstPerson();
        var p = (await Service.CreateAsync(Request())).Value;
        var source = await AssetAsync(@"one\meshes\Custom\Shared.nif", "original shared model");
        var shared = (await Service.ImportModelAsync(p.ProjectFilePath, source, Data)).Value;
        Assert.True((await CloneModelAsync(p, first: shared, third: shared)).IsSuccess);
        var plugin = File.ReadAllBytes(p.PluginPath);
        var metadata = File.ReadAllBytes(p.ProjectFilePath);
        var conflicting = await AssetAsync(@"two\meshes\Custom\SHARED.nif", "conflicting bytes");
        var collision = await Service.ImportModelAsync(p.ProjectFilePath, conflicting, Data);
        Assert.True(collision.IsFailure);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.ImportModelAsync(p.ProjectFilePath, conflicting, Data, cancelled.Token));
        Assert.Equal(plugin, File.ReadAllBytes(p.PluginPath));
        Assert.Equal(metadata, File.ReadAllBytes(p.ProjectFilePath));
        Assert.Equal("conflicting bytes", File.ReadAllText(conflicting));
        var opened = await Service.OpenAsync(p.ProjectFilePath, Data);
        Assert.True(opened.IsSuccess, opened.Error);
        var catalog = await Service.GetResourceCatalogAsync(p.ProjectFilePath, Data);
        Assert.True(catalog.IsSuccess, catalog.Error);
        Assert.True(catalog.Value.CanPack);
        Assert.Single(catalog.Value.Entries);
        using var provider = PackingProvider();
        var result = await provider.GetRequiredService<PackingPipeline>().ExecuteAsync(PackingProfile(opened.Value, esl),
            new AppSettings { SkyrimDataPath = Data, OutputDirectory = Path.Combine(_root, "archives"), UseTempDirectory = staging });
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        using var zip = ZipFile.OpenRead(result.OutputArchivePath);
        Assert.Equal(new[] { "MySword.esp", "meshes/Custom/Shared.nif" }, zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal));
        using var reader = new StreamReader(zip.GetEntry("meshes/Custom/Shared.nif")!.Open());
        Assert.Equal("original shared model", await reader.ReadToEndAsync());
        Assert.Equal(plugin, File.ReadAllBytes(p.PluginPath));
        Assert.Equal(metadata, File.ReadAllBytes(p.ProjectFilePath));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(p.PluginPath)!, ".assets-*"));
    }
}
