using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SkyrimModPacker.Core.Models;

/// <summary>
/// Represents a node in the file tree preview (either a folder or a file).
/// Supports three-state checkboxes (checked, unchecked, indeterminate) with cascading updates and WPF binding notifications.
/// </summary>
public sealed class FileTreeNode : INotifyPropertyChanged
{
    private bool? _isChecked = true;
    private long _sizeBytes;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Display name of the file or folder.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Full relative path from Skyrim Data directory (e.g. "meshes\weapons\MyAxe\blade.nif").</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>Whether this node is a directory.</summary>
    public bool IsDirectory { get; set; }

    /// <summary>File category (Mesh, Texture, Plugin, Other).</summary>
    public FileCategory? Category { get; set; }

    /// <summary>Size of this file in bytes, or total size of all child files if a directory.</summary>
    public long SizeBytes
    {
        get => _sizeBytes;
        set
        {
            if (_sizeBytes != value)
            {
                _sizeBytes = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FormattedSize));
            }
        }
    }

    /// <summary>Human-readable formatted size (e.g. "2.4 MB").</summary>
    public string FormattedSize => FormatBytes(SizeBytes);

    /// <summary>Reference to the underlying scanned file if this node is a leaf file.</summary>
    public ScannedFile? File { get; set; }

    /// <summary>Parent node in the hierarchy.</summary>
    public FileTreeNode? Parent { get; set; }

    /// <summary>Child nodes (subfolders and files).</summary>
    public List<FileTreeNode> Children { get; } = [];

    /// <summary>Total number of leaf files within this node and all descendants.</summary>
    public int TotalFileCount => IsDirectory ? Children.Sum(c => c.TotalFileCount) : 1;

    /// <summary>Number of selected leaf files within this node and all descendants.</summary>
    public int SelectedFileCount => IsDirectory ? Children.Sum(c => c.SelectedFileCount) : (IsChecked == true ? 1 : 0);

    /// <summary>Total size in bytes of selected files within this subtree.</summary>
    public long SelectedSizeBytes => IsDirectory
        ? Children.Sum(c => c.SelectedSizeBytes)
        : (IsChecked == true ? SizeBytes : 0L);

    /// <summary>Checkbox state: true = all selected, false = none selected, null = partially selected.</summary>
    public bool? IsChecked
    {
        get => _isChecked;
        set => SetChecked(value, updateChildren: true, updateParent: true);
    }

    /// <summary>
    /// Updates the checkbox state with optional propagation to children and parent.
    /// </summary>
    public void SetChecked(bool? value, bool updateChildren, bool updateParent)
    {
        if (_isChecked == value)
            return;

        _isChecked = value;
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(SelectedFileCount));
        OnPropertyChanged(nameof(SelectedSizeBytes));

        if (File != null)
        {
            File.IsSelected = value == true;
        }

        if (updateChildren && IsDirectory && value.HasValue)
        {
            foreach (var child in Children)
            {
                child.SetChecked(value, updateChildren: true, updateParent: false);
            }
        }

        if (updateParent && Parent != null)
        {
            Parent.UpdateStateFromChildren();
        }
    }

    /// <summary>
    /// Re-evaluates this directory's checked state based on the states of its children.
    /// </summary>
    public void UpdateStateFromChildren()
    {
        if (!IsDirectory || Children.Count == 0)
            return;

        var allChecked = true;
        var allUnchecked = true;

        foreach (var child in Children)
        {
            if (child.IsChecked == true)
            {
                allUnchecked = false;
            }
            else if (child.IsChecked == false)
            {
                allChecked = false;
            }
            else
            {
                allChecked = false;
                allUnchecked = false;
            }
        }

        bool? newState = allChecked ? true : allUnchecked ? false : null;

        if (_isChecked != newState)
        {
            _isChecked = newState;
            OnPropertyChanged(nameof(IsChecked));
            OnPropertyChanged(nameof(SelectedFileCount));
            OnPropertyChanged(nameof(SelectedSizeBytes));
            Parent?.UpdateStateFromChildren();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1024 * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        if (bytes >= 1024 * 1024)
            return $"{bytes / (1024.0 * 1024):F2} MB";
        if (bytes >= 1024)
            return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }
}
