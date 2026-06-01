using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NRSGitCheck.ViewModels;

/// <summary>
/// Base node for the changed-files tree (FR-12). Files are grouped under the
/// folders that contain them, nested to match the repo's directory structure.
/// </summary>
public abstract partial class FileTreeNode : ViewModelBase
{
    protected FileTreeNode(string name, FolderNode? parent)
    {
        Name = name;
        Parent = parent;
    }

    public string Name { get; }
    public FolderNode? Parent { get; }

    /// <summary>
    /// Only meaningful for folders, but declared on the base type so the
    /// <c>TreeViewItem</c> style can bind <c>IsExpanded</c> uniformly without
    /// producing binding errors on file rows.
    /// </summary>
    [ObservableProperty]
    private bool _isExpanded = true;
}

/// <summary>A directory grouping one or more changed files, possibly nested.</summary>
public sealed class FolderNode(string name, FolderNode? parent) : FileTreeNode(name, parent)
{
    public ObservableCollection<FileTreeNode> Children { get; } = new();

    /// <summary>Number of changed files anywhere beneath this folder.</summary>
    public int ChangedCount { get; set; }

    public string ChangedCountText => ChangedCount.ToString();
}

/// <summary>A leaf node wrapping a single changed file.</summary>
public sealed class FileNode(FileChangeViewModel file, FolderNode? parent)
    : FileTreeNode(file.FileName, parent)
{
    public FileChangeViewModel File { get; } = file;
}
