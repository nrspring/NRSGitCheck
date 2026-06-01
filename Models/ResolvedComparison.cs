namespace NRSGitCheck.Models;

/// <summary>
/// The outcome of resolving a comparison target to a concrete base commit (FR-7..9).
/// Either <see cref="Found"/> with a <see cref="Sha"/>, or unresolved with a
/// human-readable <see cref="Label"/>/<see cref="Error"/> explaining why.
/// </summary>
public sealed class ResolvedComparison
{
    public bool Found { get; private init; }

    /// <summary>The resolved base commit SHA (full), when <see cref="Found"/>.</summary>
    public string? Sha { get; private init; }

    /// <summary>Short, human-readable description of the target shown in the UI (FR-11).</summary>
    public string Label { get; private init; } = string.Empty;

    /// <summary>Why the target could not be resolved, when not <see cref="Found"/>.</summary>
    public string? Error { get; private init; }

    public static ResolvedComparison Resolved(string sha, string label) =>
        new() { Found = true, Sha = sha, Label = label };

    public static ResolvedComparison Unresolved(string error) =>
        new() { Found = false, Label = error, Error = error };
}
