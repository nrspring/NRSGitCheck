using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NRSGitCheck.Models;
using NRSGitCheck.Services;
using NRSGitCheck.ViewModels;
using Xunit;
using ChangeKind = NRSGitCheck.Models.ChangeKind;

namespace NRSGitCheck.Tests;

/// <summary>
/// Phase 6 exit check: known languages produce foreground colors; unknown types
/// fall back to plain; and colors compose through the diff into render segments.
/// </summary>
public sealed class SyntaxHighlighterTests
{
    private sealed class StubGitService : IGitService
    {
        private readonly FileContent _content;
        public StubGitService(FileContent content) => _content = content;
        public FileContent GetFileContent(string baseCommitSha, FileChange change) => _content;
        public RepositorySnapshot OpenRepository(string path) => throw new System.NotSupportedException();
        public ResolvedComparison ResolveComparison(ComparisonMode mode, string? a, string? b) => throw new System.NotSupportedException();
        public IReadOnlyList<FileChange> GetChanges(string baseCommitSha) => throw new System.NotSupportedException();
    }

    private sealed class StubSettings : ISettingsService
    {
        public AppSettings Settings { get; } = new();
        public void Load() { }
        public void Save() { }
        public void AddRecentRepository(string repositoryPath) { }
        public void RemoveRecentRepository(string repositoryPath) { }
    }

    [Fact]
    public void Csharp_file_gets_foreground_colors()
    {
        var hl = new SyntaxHighlighter();

        var colors = hl.Highlight("Foo.cs", "public class Foo { }\n");

        Assert.NotNull(colors);
        Assert.Contains(colors!, line => line.Any(span => span.Foreground is not null));
    }

    [Fact]
    public void Unknown_extension_returns_null()
    {
        var hl = new SyntaxHighlighter();

        Assert.Null(hl.Highlight("data.zzunknown", "anything here\n"));
    }

    [Fact]
    public void Diff_service_attaches_foreground_for_known_language()
    {
        var git = new StubGitService(new FileContent("public class A { }\n", "public class B { }\n", false));
        var svc = new DiffService(git, new SyntaxHighlighter());

        var doc = svc.BuildDiff("base", new FileChange("A.cs", null, ChangeKind.Modified, 1, 1, false));

        var anyForeground = doc.Hunks.SelectMany(h => h.Lines).Any(l => l.Foreground is { Count: > 0 });
        Assert.True(anyForeground);
    }

    [Fact]
    public async Task Render_segments_carry_both_color_and_word_highlight()
    {
        // A modified line gives word-level segments; the C# grammar gives colors.
        var git = new StubGitService(new FileContent("var x = 1;\n", "var x = 2;\n", false));
        var doc = new DiffService(git, new SyntaxHighlighter())
            .BuildDiff("base", new FileChange("A.cs", null, ChangeKind.Modified, 1, 1, false));

        var vm = new DiffViewModel(new ConstDiff(doc), new StubSettings());
        await vm.LoadAsync("base", new FileChange("A.cs", null, ChangeKind.Modified, 1, 1, false));

        var segments = vm.InlineRows.OfType<InlineDiffRow>().SelectMany(r => r.Segments).ToList();
        Assert.Contains(segments, s => s.Foreground is not null);                 // syntax color present
        Assert.Contains(segments, s => s.Highlight != WordSegmentKind.Unchanged);  // word highlight present
    }

    private sealed class ConstDiff : IDiffService
    {
        private readonly DiffDocument _doc;
        public ConstDiff(DiffDocument doc) => _doc = doc;
        public DiffDocument BuildDiff(string baseCommitSha, FileChange change, int contextLines = 3) => _doc;
    }
}
