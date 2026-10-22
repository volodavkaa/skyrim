using SkyrimModPacker.Core.Models;
using Xunit;

namespace SkyrimModPacker.Tests;

public class FileTreeBuilderTests
{
    [Fact]
    public void BuildTree_BuildsHierarchy_AndCalculatesSizes()
    {
        var files = new List<ScannedFile>
        {
            new()
            {
                AbsolutePath = @"C:\Games\Skyrim\Data\meshes\weapons\MyAxe\blade.nif",
                RelativePath = @"meshes\weapons\MyAxe\blade.nif",
                SizeBytes = 100,
                IsSelected = true,
                Category = FileCategory.Mesh
            },
            new()
            {
                AbsolutePath = @"C:\Games\Skyrim\Data\meshes\weapons\MyAxe\handle.nif",
                RelativePath = @"meshes\weapons\MyAxe\handle.nif",
                SizeBytes = 200,
                IsSelected = true,
                Category = FileCategory.Mesh
            },
            new()
            {
                AbsolutePath = @"C:\Games\Skyrim\Data\textures\weapons\MyAxe\blade_d.dds",
                RelativePath = @"textures\weapons\MyAxe\blade_d.dds",
                SizeBytes = 500,
                IsSelected = true,
                Category = FileCategory.Texture
            },
            new()
            {
                AbsolutePath = @"C:\Games\Skyrim\Data\MyAxe.esp",
                RelativePath = @"MyAxe.esp",
                SizeBytes = 50,
                IsSelected = true,
                Category = FileCategory.Plugin
            }
        };

        var rootNodes = FileTreeBuilder.BuildTree(files);

        // Roots should be directories "meshes", "textures", and file "MyAxe.esp"
        Assert.Equal(3, rootNodes.Count);

        var meshesNode = rootNodes.Find(n => n.Name == "meshes");
        Assert.NotNull(meshesNode);
        Assert.True(meshesNode.IsDirectory);
        Assert.Equal(300, meshesNode.SizeBytes);
        Assert.Equal(2, meshesNode.TotalFileCount);

        var texturesNode = rootNodes.Find(n => n.Name == "textures");
        Assert.NotNull(texturesNode);
        Assert.True(texturesNode.IsDirectory);
        Assert.Equal(500, texturesNode.SizeBytes);
        Assert.Equal(1, texturesNode.TotalFileCount);

        var pluginNode = rootNodes.Find(n => n.Name == "MyAxe.esp");
        Assert.NotNull(pluginNode);
        Assert.False(pluginNode.IsDirectory);
        Assert.Equal(50, pluginNode.SizeBytes);
        Assert.Equal(1, pluginNode.TotalFileCount);
    }

    [Fact]
    public void CheckboxCascading_UncheckingParent_UnchecksDescendants()
    {
        var files = new List<ScannedFile>
        {
            new()
            {
                AbsolutePath = @"C:\Data\meshes\weapons\MyAxe\blade.nif",
                RelativePath = @"meshes\weapons\MyAxe\blade.nif",
                SizeBytes = 100,
                IsSelected = true,
                Category = FileCategory.Mesh
            },
            new()
            {
                AbsolutePath = @"C:\Data\meshes\weapons\MyAxe\handle.nif",
                RelativePath = @"meshes\weapons\MyAxe\handle.nif",
                SizeBytes = 200,
                IsSelected = true,
                Category = FileCategory.Mesh
            }
        };

        var rootNodes = FileTreeBuilder.BuildTree(files);
        var meshesNode = rootNodes.First(n => n.Name == "meshes");

        Assert.True(meshesNode.IsChecked);
        Assert.Equal(2, meshesNode.SelectedFileCount);
        Assert.Equal(300, meshesNode.SelectedSizeBytes);

        // Uncheck the top folder
        meshesNode.IsChecked = false;

        // All descendants and underlying files must be unchecked
        Assert.Equal(0, meshesNode.SelectedFileCount);
        Assert.Equal(0, meshesNode.SelectedSizeBytes);
        Assert.All(files, f => Assert.False(f.IsSelected));
    }

    [Fact]
    public void CheckboxCascading_TogglingOneChild_MakesParentIndeterminate()
    {
        var files = new List<ScannedFile>
        {
            new()
            {
                AbsolutePath = @"C:\Data\meshes\weapons\MyAxe\blade.nif",
                RelativePath = @"meshes\weapons\MyAxe\blade.nif",
                SizeBytes = 100,
                IsSelected = true,
                Category = FileCategory.Mesh
            },
            new()
            {
                AbsolutePath = @"C:\Data\meshes\weapons\MyAxe\handle.nif",
                RelativePath = @"meshes\weapons\MyAxe\handle.nif",
                SizeBytes = 200,
                IsSelected = true,
                Category = FileCategory.Mesh
            }
        };

        var rootNodes = FileTreeBuilder.BuildTree(files);
        var meshesNode = rootNodes.First(n => n.Name == "meshes");
        var weaponsNode = meshesNode.Children.First(n => n.Name == "weapons");
        var myAxeNode = weaponsNode.Children.First(n => n.Name == "MyAxe");
        var bladeNode = myAxeNode.Children.First(n => n.Name == "blade.nif");

        // Uncheck one leaf file
        bladeNode.IsChecked = false;

        // Parents should now be indeterminate (null)
        Assert.Null(myAxeNode.IsChecked);
        Assert.Null(weaponsNode.IsChecked);
        Assert.Null(meshesNode.IsChecked);

        // Selected count and size should reflect only the remaining checked file
        Assert.Equal(1, meshesNode.SelectedFileCount);
        Assert.Equal(200, meshesNode.SelectedSizeBytes);
    }
}
