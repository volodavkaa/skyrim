namespace SkyrimModPacker.Core.Models;

/// <summary>
/// Constructs a hierarchical <see cref="FileTreeNode"/> structure from a flat list of <see cref="ScannedFile"/>.
/// </summary>
public static class FileTreeBuilder
{
    private static readonly char[] Separators = ['/', '\\'];

    /// <summary>
    /// Builds a hierarchical tree from a flat list of scanned files.
    /// </summary>
    /// <param name="files">Collection of scanned files.</param>
    /// <returns>List of root nodes (top-level folders and root files).</returns>
    public static List<FileTreeNode> BuildTree(IEnumerable<ScannedFile> files)
    {
        var rootNodes = new List<FileTreeNode>();

        foreach (var file in files)
        {
            var normalizedPath = file.RelativePath.Replace('/', '\\');
            var segments = normalizedPath.Split(Separators, StringSplitOptions.RemoveEmptyEntries);

            if (segments.Length == 0)
                continue;

            List<FileTreeNode> currentLevel = rootNodes;
            FileTreeNode? parent = null;
            var accumulatedPath = string.Empty;

            for (int i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                var isLast = i == segments.Length - 1;
                accumulatedPath = string.IsNullOrEmpty(accumulatedPath) ? segment : $"{accumulatedPath}\\{segment}";

                var existingNode = currentLevel.Find(n => string.Equals(n.Name, segment, StringComparison.OrdinalIgnoreCase));

                if (existingNode == null)
                {
                    existingNode = new FileTreeNode
                    {
                        Name = segment,
                        RelativePath = accumulatedPath,
                        IsDirectory = !isLast,
                        Parent = parent,
                        Category = isLast ? file.Category : null,
                        File = isLast ? file : null,
                        SizeBytes = isLast ? file.SizeBytes : 0
                    };

                    existingNode.SetChecked(isLast ? file.IsSelected : true, updateChildren: false, updateParent: false);

                    currentLevel.Add(existingNode);
                }

                if (!isLast)
                {
                    parent = existingNode;
                    currentLevel = existingNode.Children;
                }
            }
        }

        // Post-process: calculate directory sizes and ensure correct checked states
        foreach (var root in rootNodes)
        {
            CalculateDirectorySizes(root);
            UpdateInitialStates(root);
        }

        // Sort: directories first (alphabetical), then files (alphabetical)
        SortTree(rootNodes);

        return rootNodes;
    }

    private static long CalculateDirectorySizes(FileTreeNode node)
    {
        if (!node.IsDirectory)
            return node.SizeBytes;

        long sum = 0;
        foreach (var child in node.Children)
        {
            sum += CalculateDirectorySizes(child);
        }

        node.SizeBytes = sum;
        return sum;
    }

    private static void UpdateInitialStates(FileTreeNode node)
    {
        if (!node.IsDirectory)
            return;

        foreach (var child in node.Children)
        {
            UpdateInitialStates(child);
        }

        node.UpdateStateFromChildren();
    }

    private static void SortTree(List<FileTreeNode> nodes)
    {
        nodes.Sort((a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory)
                return a.IsDirectory ? -1 : 1;

            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        foreach (var node in nodes)
        {
            if (node.IsDirectory && node.Children.Count > 0)
            {
                SortTree(node.Children);
            }
        }
    }
}
