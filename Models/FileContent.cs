namespace NRSGitCheck.Models;

/// <summary>
/// The old (base) and new (working-tree) text for a file, used as input to the
/// diff engine. Either side may be empty (added/deleted files). When the file is
/// detected as binary, the text sides are empty and <see cref="IsBinary"/> is set.
/// </summary>
public sealed record FileContent(string OldText, string NewText, bool IsBinary);
