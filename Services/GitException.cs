using System;

namespace NRSGitCheck.Services;

/// <summary>
/// Raised for Git operations that fail in a way worth surfacing to the user
/// (e.g. opening a folder that is not a repository). The <see cref="Exception.Message"/>
/// is intended to be displayed directly.
/// </summary>
public sealed class GitException : Exception
{
    public GitException(string message) : base(message) { }
}
