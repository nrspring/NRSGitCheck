using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NRSGitCheck.Models;

namespace NRSGitCheck.Services;

/// <summary>
/// Pure, UI-free diff computation (FR-18, FR-21..23). Produces a unified line
/// diff grouped into hunks, with word-level segments on modified line pairs.
/// Uses Myers' O(ND) shortest-edit-script algorithm at both line and token level.
/// </summary>
public static class DiffEngine
{
    private const int DefaultContextLines = 3;

    private static readonly Regex TokenRegex =
        new(@"\w+|\s+|[^\w\s]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private enum Op { Equal, Delete, Insert }

    private readonly record struct Edit(Op Op, int OldIndex, int NewIndex);

    public static DiffDocument Compute(string oldText, string newText, int contextLines = DefaultContextLines)
    {
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);

        var edits = Myers(oldLines, newLines, StringComparer.Ordinal);

        var lines = new List<DiffLine>(edits.Count);
        var added = 0;
        var removed = 0;

        foreach (var edit in edits)
        {
            switch (edit.Op)
            {
                case Op.Equal:
                    lines.Add(new DiffLine(DiffLineKind.Context, edit.OldIndex + 1, edit.NewIndex + 1, newLines[edit.NewIndex]));
                    break;
                case Op.Delete:
                    lines.Add(new DiffLine(DiffLineKind.Removed, edit.OldIndex + 1, null, oldLines[edit.OldIndex]));
                    removed++;
                    break;
                case Op.Insert:
                    lines.Add(new DiffLine(DiffLineKind.Added, null, edit.NewIndex + 1, newLines[edit.NewIndex]));
                    added++;
                    break;
            }
        }

        AssignWordSegments(lines);

        var hunks = BuildHunks(lines, contextLines);

        return new DiffDocument
        {
            Hunks = hunks,
            LinesAdded = added,
            LinesRemoved = removed,
        };
    }

    // --- Line splitting -----------------------------------------------------

    /// <summary>
    /// Splits into logical lines, normalizing CRLF/CR to LF so a line-ending-only
    /// change does not register as a difference (FR: CRLF/encoding handling).
    /// </summary>
    internal static string[] SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<string>();

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var parts = normalized.Split('\n');

        // A trailing newline yields a spurious empty final element; drop it.
        if (normalized.EndsWith('\n') && parts.Length > 0)
            Array.Resize(ref parts, parts.Length - 1);

        return parts;
    }

    // --- Word-level pairing -------------------------------------------------

    /// <summary>
    /// Finds runs of removed lines immediately followed by added lines and pairs
    /// them index-by-index, attaching word-level segments to each paired line.
    /// </summary>
    private static void AssignWordSegments(List<DiffLine> lines)
    {
        var i = 0;
        while (i < lines.Count)
        {
            if (lines[i].Kind != DiffLineKind.Removed)
            {
                i++;
                continue;
            }

            var removedStart = i;
            while (i < lines.Count && lines[i].Kind == DiffLineKind.Removed)
                i++;
            var removedEnd = i; // exclusive

            var addedStart = i;
            while (i < lines.Count && lines[i].Kind == DiffLineKind.Added)
                i++;
            var addedEnd = i; // exclusive

            var pairs = Math.Min(removedEnd - removedStart, addedEnd - addedStart);
            for (var p = 0; p < pairs; p++)
            {
                var removedLine = lines[removedStart + p];
                var addedLine = lines[addedStart + p];
                var (oldSegs, newSegs) = WordDiff(removedLine.Text, addedLine.Text);
                removedLine.Segments = oldSegs;
                addedLine.Segments = newSegs;
            }
        }
    }

    private static (IReadOnlyList<WordSegment> oldSegments, IReadOnlyList<WordSegment> newSegments)
        WordDiff(string oldLine, string newLine)
    {
        var oldTokens = Tokenize(oldLine);
        var newTokens = Tokenize(newLine);
        var edits = Myers(oldTokens, newTokens, StringComparer.Ordinal);

        var oldSegs = new List<WordSegment>();
        var newSegs = new List<WordSegment>();

        foreach (var edit in edits)
        {
            switch (edit.Op)
            {
                case Op.Equal:
                    Append(oldSegs, oldTokens[edit.OldIndex], WordSegmentKind.Unchanged);
                    Append(newSegs, newTokens[edit.NewIndex], WordSegmentKind.Unchanged);
                    break;
                case Op.Delete:
                    Append(oldSegs, oldTokens[edit.OldIndex], WordSegmentKind.Removed);
                    break;
                case Op.Insert:
                    Append(newSegs, newTokens[edit.NewIndex], WordSegmentKind.Added);
                    break;
            }
        }

        return (oldSegs, newSegs);
    }

    /// <summary>Appends a token, merging into the previous segment if same kind.</summary>
    private static void Append(List<WordSegment> segments, string text, WordSegmentKind kind)
    {
        if (segments.Count > 0 && segments[^1].Kind == kind)
            segments[^1] = new WordSegment(segments[^1].Text + text, kind);
        else
            segments.Add(new WordSegment(text, kind));
    }

    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        foreach (Match m in TokenRegex.Matches(line))
            tokens.Add(m.Value);
        return tokens;
    }

    // --- Hunk grouping ------------------------------------------------------

    private static List<DiffHunk> BuildHunks(List<DiffLine> lines, int context)
    {
        var hunks = new List<DiffHunk>();

        var changed = new List<int>();
        for (var i = 0; i < lines.Count; i++)
            if (lines[i].Kind != DiffLineKind.Context)
                changed.Add(i);

        if (changed.Count == 0)
            return hunks;

        var clusterStart = changed[0];
        var clusterEnd = changed[0];

        void Flush()
        {
            var start = Math.Max(0, clusterStart - context);
            var end = Math.Min(lines.Count - 1, clusterEnd + context);
            hunks.Add(BuildHunk(lines, start, end));
        }

        for (var idx = 1; idx < changed.Count; idx++)
        {
            // Merge clusters whose context windows would touch or overlap.
            if (changed[idx] - clusterEnd <= context * 2 + 1)
            {
                clusterEnd = changed[idx];
            }
            else
            {
                Flush();
                clusterStart = changed[idx];
                clusterEnd = changed[idx];
            }
        }

        Flush();
        return hunks;
    }

    private static DiffHunk BuildHunk(List<DiffLine> lines, int start, int end)
    {
        var hunkLines = new List<DiffLine>(end - start + 1);
        int oldStart = 0, newStart = 0, oldCount = 0, newCount = 0;

        for (var i = start; i <= end; i++)
        {
            var line = lines[i];
            hunkLines.Add(line);

            if (line.OldLineNumber is { } oln)
            {
                if (oldStart == 0) oldStart = oln;
                oldCount++;
            }

            if (line.NewLineNumber is { } nln)
            {
                if (newStart == 0) newStart = nln;
                newCount++;
            }
        }

        var header = $"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@";
        return new DiffHunk(oldStart, oldCount, newStart, newCount, header, hunkLines);
    }

    // --- Myers O(ND) shortest edit script -----------------------------------

    private static List<Edit> Myers<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, IEqualityComparer<T> cmp)
    {
        int n = a.Count, m = b.Count;
        var edits = new List<Edit>();

        if (n == 0 && m == 0)
            return edits;

        var max = n + m;
        var offset = max;
        var v = new int[2 * max + 1];
        var trace = new List<int[]>();

        var found = false;
        for (var d = 0; d <= max && !found; d++)
        {
            trace.Add((int[])v.Clone());

            for (var k = -d; k <= d; k += 2)
            {
                int x;
                if (k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]))
                    x = v[offset + k + 1];           // move down  -> insertion
                else
                    x = v[offset + k - 1] + 1;       // move right -> deletion

                var y = x - k;
                while (x < n && y < m && cmp.Equals(a[x], b[y]))
                {
                    x++;
                    y++;
                }

                v[offset + k] = x;

                if (x >= n && y >= m)
                {
                    found = true;
                    break;
                }
            }
        }

        // Backtrack through the recorded V snapshots.
        int px = n, py = m;
        for (var d = trace.Count - 1; d >= 0; d--)
        {
            var vv = trace[d];
            var k = px - py;

            int prevK;
            if (k == -d || (k != d && vv[offset + k - 1] < vv[offset + k + 1]))
                prevK = k + 1;
            else
                prevK = k - 1;

            var prevX = vv[offset + prevK];
            var prevY = prevX - prevK;

            while (px > prevX && py > prevY)
            {
                edits.Add(new Edit(Op.Equal, px - 1, py - 1));
                px--;
                py--;
            }

            if (d > 0)
            {
                if (px == prevX)
                    edits.Add(new Edit(Op.Insert, -1, py - 1)); // came from down
                else
                    edits.Add(new Edit(Op.Delete, px - 1, -1)); // came from right

                px = prevX;
                py = prevY;
            }
        }

        edits.Reverse();
        return edits;
    }
}
