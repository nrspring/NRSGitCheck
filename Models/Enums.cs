namespace NRSGitCheck.Models;

/// <summary>How the application chooses its light/dark appearance (FR-29..31).</summary>
public enum ThemeMode
{
    /// <summary>Follow the operating system setting (default).</summary>
    System,
    Light,
    Dark,
}

/// <summary>Which reference point the working tree is compared against (FR-7).</summary>
public enum ComparisonMode
{
    /// <summary>The tip commit of the current branch (HEAD).</summary>
    LastCommit,

    /// <summary>Another local branch chosen by the user.</summary>
    OtherBranch,

    /// <summary>The merge-base where the current branch diverged from its parent.</summary>
    BranchBase,
}

/// <summary>How the diff is rendered (FR-19).</summary>
public enum DiffLayout
{
    SideBySide,
    Inline,
}
