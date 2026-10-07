using System.Collections.Generic;
using NRSGitCheck.Models;

namespace NRSGitCheck.Services;

/// <summary>
/// Reads the local branches of one repository for the branch list. Read-only and
/// stateless, like <see cref="IRepositoryStatusService"/>: every call opens and
/// closes its own Git handle.
/// </summary>
public interface IBranchInfoService
{
    /// <summary>
    /// Every local branch, newest commit first. Throws when the folder cannot be
    /// read as a repository; the caller shows the message.
    /// </summary>
    IReadOnlyList<LocalBranchInfo> ReadLocalBranches(string path);
}
