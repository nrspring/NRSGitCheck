using System;
using System.Collections.Generic;
using System.Linq;
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
        public ResolvedComparison ResolveComparison(ComparisonMode mode, string? otherBranch, string? parentBranch, string? commitSha = null) => throw new NotSupportedException();
        public IReadOnlyList<CommitInfo> GetBranchCommits(string? mainBranch, int maxCount = 200) => throw new NotSupportedException();
        public IReadOnlyList<FileChange> GetChanges(string baseCommitSha) => throw new NotSupportedException();
        public int GetUncommittedChangeCount() => 0;

        public IReadOnlyDictionary<string, FileStats> GetChangeStats(string baseCommitSha) => throw new NotSupportedException();
    }

    private static FileChange Change(ChangeKind kind, bool isBinary = false) =>
        new("file.txt", null, kind, 0, 0, isBinary);

    /// <summary>Records whether highlighting was attempted, and for how much text.</summary>
    private sealed class CountingHighlighter : ISyntaxHighlighter
    {
        public int Calls { get; private set; }

        public IReadOnlyList<IReadOnlyList<ColorSpan>>? Highlight(string filePath, string text)
        {
            Calls++;
            var lines = text.Split('\n').Length;
            var spans = new List<IReadOnlyList<ColorSpan>>(lines);
            for (var i = 0; i < lines; i++)
                spans.Add(new[] { new ColorSpan(0, 1, "#ff0000") });
            return spans;
        }

        public void SetDark(bool isDark) { }
    }

    private static string Lines(int count, string suffix = "") =>
        string.Join("\n", Enumerable.Range(0, count).Select(i => $"line {i}{suffix}")) + "\n";

    [Fact]
    public void An_ordinary_file_is_syntax_highlighted()
    {
        var content = new FileContent(Lines(500), Lines(500, " changed"), false);
        var highlighter = new CountingHighlighter();
        var svc = new DiffService(new StubGitService(content), highlighter);

        var doc = svc.BuildDiff("base", Change(ChangeKind.Modified));

        Assert.Equal(2, highlighter.Calls);        // old side and new side
        Assert.Contains(doc.Hunks.SelectMany(h => h.Lines), l => l.Foreground is not null);
    }

    [Fact]
    public void A_very_large_file_skips_highlighting_but_still_diffs()
    {
        // Past the highlight cap: tokenizing has to finish before the first hunk can
        // be shown, so on a file this size it would stall the progressive render.
        var content = new FileContent(Lines(6_000), Lines(6_000, " changed"), false);
        var highlighter = new CountingHighlighter();
        var svc = new DiffService(new StubGitService(content), highlighter);

        var doc = svc.BuildDiff("base", Change(ChangeKind.Modified));

        Assert.Equal(0, highlighter.Calls);                    // not highlighted
        Assert.True(doc.HasChanges);                           // but still diffed
        Assert.False(doc.IsTooLarge);
        Assert.All(doc.Hunks.SelectMany(h => h.Lines), l => Assert.Null(l.Foreground));
    }

    [Fact]
    public void A_file_too_large_to_load_is_reported_rather_than_read()
    {
        var svc = new DiffService(
            new StubGitService(new FileContent("", "", false, IsTooLarge: true)),
            new CountingHighlighter());

        var doc = svc.BuildDiff("base", Change(ChangeKind.Modified));

        Assert.True(doc.IsTooLarge);
        Assert.False(doc.HasChanges);
    }

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
