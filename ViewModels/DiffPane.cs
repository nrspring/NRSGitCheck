namespace NRSGitCheck.ViewModels;

/// <summary>Which pane a copy came from, so a side-by-side row copies the right side.</summary>
public enum DiffPane
{
    /// <summary>The unified list, or the old side of a side-by-side pair.</summary>
    Left,

    /// <summary>The new side of a side-by-side pair.</summary>
    Right,
}
