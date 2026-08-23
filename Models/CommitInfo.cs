using System;

namespace NRSGitCheck.Models;

/// <summary>
/// A commit on the current branch as exposed to the commit picker (FR-7). Plain
/// values only, so it is safe to hand to view models off the Git thread.
/// </summary>
public sealed record CommitInfo(
    string Sha,
    string ShortSha,
    string Summary,
    string Author,
    DateTimeOffset When,
    bool IsBranchStart)
{
    /// <summary>Primary line in the picker: short SHA plus the commit subject.</summary>
    public string Label => $"{ShortSha}  {Summary}";

    /// <summary>Secondary line: who and when, plus a marker for the branch point.</summary>
    public string Detail => IsBranchStart
        ? $"{Author} · {When.LocalDateTime:g} · branch start"
        : $"{Author} · {When.LocalDateTime:g}";
}
