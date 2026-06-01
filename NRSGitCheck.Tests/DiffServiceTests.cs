using System;
using System.Collections.Generic;
using NRSGitCheck.Models;
using NRSGitCheck.Services;
using Xunit;
using ChangeKind = NRSGitCheck.Models.ChangeKind;

namespace NRSGitCheck.Tests;

/// <summary>
/// Orchestration tests for <see cref="DiffService"/> using a stub Git service, so
/// binary / large-file / text handling is covered without a real repository.
/// </summary>
public sealed class DiffServiceTests
{
    private sealed class StubGitService : IGitService
    {
        private readonly FileContent _content;
        public StubGitService(FileContent content) => _content = content;

        public FileContent GetFileContent(string baseCommitSha, FileChange change) => _content;

        // Unused by these tests.
        public RepositorySnapshot OpenRepository(string path) => throw new NotSupportedException();
        public ResolvedComparison ResolveComparison(ComparisonMode mode, string? otherBranch, string? parentBranch) => throw new NotSupportedException();
        public IReadOnlyList<FileChange> GetChanges(string baseCommitSha) => throw new NotSupportedException();
        public IReadOnlyDictionary<string, FileStats> GetChangeStats(string baseCommitSha) => throw new NotSupportedException();
    }

    private static FileChange Change(ChangeKind kind, bool isBinary = false) =>
        new("file.txt", null, kind, 0, 0, isBinary);

    [Fact]
    public void Binary_change_short_circuits_without_calling_engine()
    {
        var svc = new DiffService(new StubGitService(new FileContent("", "", true)), new NullSyntaxHighlighter());

        var doc = svc.BuildDiff("base", Change(ChangeKind.Modified, isBinary: true));

        Assert.True(doc.IsBinary);
        Assert.False(doc.HasChanges);
    }

    [Fact]
    public void Binary_detected_during_retrieval_is_reported()
    {
        var svc = new DiffService(new StubGitService(new FileContent("", "", true)), new NullSyntaxHighlighter());

        var doc = svc.BuildDiff("base", Change(ChangeKind.Modified));

        Assert.True(doc.IsBinary);
    }

    [Fact]
    public void Oversized_file_is_flagged_too_large()
    {
        var huge = string.Join("\n", System.Linq.Enumerable.Range(0, 25_000));
        var svc = new DiffService(new StubGitService(new FileContent("", huge, false)), new NullSyntaxHighlighter());

        var doc = svc.BuildDiff("base", Change(ChangeKind.Added));

        Assert.True(doc.IsTooLarge);
        Assert.False(doc.HasChanges);
    }

    [Fact]
    public void Text_change_is_diffed()
    {
        var svc = new DiffService(new StubGitService(new FileContent("a\nb\n", "a\nB\n", false)), new NullSyntaxHighlighter());

        var doc = svc.BuildDiff("base", Change(ChangeKind.Modified));

        Assert.False(doc.IsBinary);
        Assert.False(doc.IsTooLarge);
        Assert.True(doc.HasChanges);
        Assert.Equal(1, doc.LinesAdded);
        Assert.Equal(1, doc.LinesRemoved);
    }
}
