using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NRSGitCheck.Models;

namespace NRSGitCheck.Services;

/// <summary>
/// Reads a one-line health check — branch, uncommitted work, unpushed commits —
/// for each repository listed on the Repositories tab. Strictly read-only and
/// stateless: every call opens (and closes) its own Git handle, so reads for
/// different repositories can run concurrently.
/// </summary>
public interface IRepositoryStatusService
{
    /// <summary>
    /// Reads one repository. Never throws for the ordinary failures (missing
    /// folder, not a repository, locked index) — those come back as an invalid
    /// <see cref="RepositoryStatus"/> carrying the message.
    /// </summary>
    RepositoryStatus Read(string path);

    /// <summary>Reads several repositories off the UI thread, preserving input order.</summary>
    Task<IReadOnlyList<RepositoryStatus>> ReadAllAsync(
        IEnumerable<string> paths, CancellationToken ct = default);
}
