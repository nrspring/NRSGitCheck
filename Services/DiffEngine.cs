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

    public static DiffDocument Compute(string oldText, string newText, int contextLines = DefaultContextLines, bool wholeFile = false)
    {
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);

        var lines = new List<DiffLine>();
        var added = 0;
        var removed = 0;

        // Word segments are attached per changed block during enumeration, so no
        // global pass is needed here.
        foreach (var line in EnumerateDiff(oldLines, newLines))
        {
            lines.Add(line);
            if (line.Kind == DiffLineKind.Added) added++;
            else if (line.Kind == DiffLineKind.Removed) removed++;
        }

        var hunks = wholeFile ? BuildWholeFileHunks(lines) : BuildHunks(lines, contextLines);

        return new DiffDocument
        {
            Hunks = hunks,
            LinesAdded = added,
            LinesRemoved = removed,
        };
    }

    /// <summary>
    /// Produces hunks lazily, in document order, so a consumer can render the top
    /// of a file while the rest is still being diffed. The line-level diff runs
    /// region-by-region (see <see cref="EnumerateDiff"/>); hunks are cut at points
    /// guaranteed to fall between hunks, so the output is identical to the eager
    /// <see cref="Compute"/> path — just streamed.
    /// </summary>
    public static IEnumerable<DiffHunk> ComputeHunkStream(
        string oldText, string newText, int contextLines = DefaultContextLines, bool wholeFile = false)
    {
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);

        if (wholeFile)
        {
            var all = new List<DiffLine>();
            foreach (var line in EnumerateDiff(oldLines, newLines))
                all.Add(line);
            foreach (var hunk in BuildWholeFileHunks(all))
                yield return hunk;
            yield break;
        }

        var context = contextLines;
        var buffer = new List<DiffLine>();
        var trailingContext = 0;
        var hasChange = false;

        foreach (var line in EnumerateDiff(oldLines, newLines))
        {
            buffer.Add(line);
            if (line.Kind == DiffLineKind.Context)
            {
                trailingContext++;
            }
            else
            {
                trailingContext = 0;
                hasChange = true;
            }

            // A run of more than 2*context unchanged lines can never sit inside a
            // hunk, so it is a safe place to flush everything before it.
            if (trailingContext == 2 * context + 1)
            {
                if (hasChange)
                {
                    // Keep `context` trailing lines with the flushed segment and
                    // retain `context` lead-in lines for the next one.
                    var segment = buffer.GetRange(0, buffer.Count - context - 1);
                    foreach (var hunk in BuildHunks(segment, context))
                        yield return hunk;
                }

                buffer = buffer.GetRange(buffer.Count - context, context);
                trailingContext = context;
                hasChange = false;
            }
        }

        if (hasChange)
            foreach (var hunk in BuildHunks(buffer, context))
                yield return hunk;
    }

    // --- Anchored, region-based line diff -----------------------------------

    private static DiffLine Context(string[] newLines, int oldIndex, int newIndex) =>
        new(DiffLineKind.Context, oldIndex + 1, newIndex + 1, newLines[newIndex]);

    private static DiffLine Removed(string[] oldLines, int oldIndex) =>
        new(DiffLineKind.Removed, oldIndex + 1, null, oldLines[oldIndex]);

    private static DiffLine Added(string[] newLines, int newIndex) =>
        new(DiffLineKind.Added, null, newIndex + 1, newLines[newIndex]);

    /// <summary>
    /// Yields the full unified line diff in document order. The expensive Myers
    /// pass is confined to small inter-anchor regions, so only what a consumer
    /// pulls is computed.
    /// </summary>
    internal static IEnumerable<DiffLine> EnumerateDiff(string[] oldLines, string[] newLines) =>
        DiffRange(oldLines, newLines, 0, oldLines.Length, 0, newLines.Length);

    private static IEnumerable<DiffLine> DiffRange(
        string[] o, string[] n, int oLo, int oHi, int nLo, int nHi)
    {
        // Trim the common prefix (cheap, emitted immediately).
        while (oLo < oHi && nLo < nHi && o[oLo] == n[nLo])
        {
            yield return Context(n, oLo, nLo);
            oLo++;
            nLo++;
        }

        // Locate the common suffix; emit it only after the middle is processed.
        int oHiSuffix = oHi, nHiSuffix = nHi;
        while (oHiSuffix > oLo && nHiSuffix > nLo && o[oHiSuffix - 1] == n[nHiSuffix - 1])
        {
            oHiSuffix--;
            nHiSuffix--;
        }

        foreach (var line in DiffMiddle(o, n, oLo, oHiSuffix, nLo, nHiSuffix))
            yield return line;

        for (var k = 0; oHiSuffix + k < oHi; k++)
            yield return Context(n, oHiSuffix + k, nHiSuffix + k);
    }

    private static IEnumerable<DiffLine> DiffMiddle(
        string[] o, string[] n, int oLo, int oHi, int nLo, int nHi)
    {
        var oLen = oHi - oLo;
        var nLen = nHi - nLo;

        if (oLen == 0 && nLen == 0)
            yield break;

        if (oLen == 0)
        {
            for (var j = nLo; j < nHi; j++)
                yield return Added(n, j);
            yield break;
        }

        if (nLen == 0)
        {
            for (var i = oLo; i < oHi; i++)
                yield return Removed(o, i);
            yield break;
        }

        var anchors = FindAnchors(o, n, oLo, oHi, nLo, nHi);
        if (anchors.Count == 0)
        {
            foreach (var line in MyersBlock(o, n, oLo, oHi, nLo, nHi))
                yield return line;
            yield break;
        }

        // Diff each region between consecutive anchors independently; the anchor
        // lines themselves are common context.
        var oPos = oLo;
        var nPos = nLo;
        foreach (var (oi, nj) in anchors)
        {
            foreach (var line in DiffRange(o, n, oPos, oi, nPos, nj))
                yield return line;
            yield return Context(n, oi, nj);
            oPos = oi + 1;
            nPos = nj + 1;
        }

        foreach (var line in DiffRange(o, n, oPos, oHi, nPos, nHi))
            yield return line;
    }

    /// <summary>Runs Myers on a single region and attaches word segments to it.</summary>
    private static List<DiffLine> MyersBlock(
        string[] o, string[] n, int oLo, int oHi, int nLo, int nHi)
    {
        var a = new ArraySegment<string>(o, oLo, oHi - oLo);
        var b = new ArraySegment<string>(n, nLo, nHi - nLo);
        var edits = Myers(a, b, StringComparer.Ordinal);

        var block = new List<DiffLine>(edits.Count);
        foreach (var edit in edits)
        {
            switch (edit.Op)
            {
                case Op.Equal:
                    block.Add(Context(n, oLo + edit.OldIndex, nLo + edit.NewIndex));
                    break;
                case Op.Delete:
                    block.Add(Removed(o, oLo + edit.OldIndex));
                    break;
                case Op.Insert:
                    block.Add(Added(n, nLo + edit.NewIndex));
                    break;
            }
        }

        AssignWordSegments(block);
        return block;
    }

    /// <summary>
    /// Patience-style anchors: lines that occur exactly once on each side, reduced
    /// to the longest subsequence that is increasing in both files.
    /// </summary>
    private static List<(int OldIndex, int NewIndex)> FindAnchors(
        string[] o, string[] n, int oLo, int oHi, int nLo, int nHi)
    {
        var oldCount = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = oLo; i < oHi; i++)
            oldCount[o[i]] = oldCount.TryGetValue(o[i], out var c) ? c + 1 : 1;

        var newCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var newFirst = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var j = nLo; j < nHi; j++)
        {
            newCount[n[j]] = newCount.TryGetValue(n[j], out var c) ? c + 1 : 1;
            if (!newFirst.ContainsKey(n[j]))
                newFirst[n[j]] = j;
        }

        // Candidates are unique-in-both lines, naturally ascending by old index.
        var candidates = new List<(int OldIndex, int NewIndex)>();
        for (var i = oLo; i < oHi; i++)
        {
            var v = o[i];
            if (oldCount[v] == 1 && newCount.TryGetValue(v, out var nc) && nc == 1)
                candidates.Add((i, newFirst[v]));
        }

        if (candidates.Count <= 1)
            return candidates;

        return LongestIncreasingByNewIndex(candidates);
    }

    /// <summary>Longest subsequence strictly increasing in new index (O(k log k)).</summary>
    private static List<(int OldIndex, int NewIndex)> LongestIncreasingByNewIndex(
        List<(int OldIndex, int NewIndex)> candidates)
    {
        var k = candidates.Count;
        var prev = new int[k];
        var pileTopIndex = new List<int>();   // candidate index at each pile's top
        var pileTopValue = new List<int>();   // new index at each pile's top (increasing)

        for (var i = 0; i < k; i++)
        {
            var nj = candidates[i].NewIndex;

            int lo = 0, hi = pileTopValue.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (pileTopValue[mid] < nj) lo = mid + 1;
                else hi = mid;
            }

            prev[i] = lo > 0 ? pileTopIndex[lo - 1] : -1;

            if (lo == pileTopValue.Count)
            {
                pileTopValue.Add(nj);
                pileTopIndex.Add(i);
            }
            else
            {
                pileTopValue[lo] = nj;
                pileTopIndex[lo] = i;
            }
        }

        var result = new List<(int, int)>();
        for (var cur = pileTopIndex[^1]; cur != -1; cur = prev[cur])
            result.Add(candidates[cur]);
        result.Reverse();
        return result;
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

    /// <summary>
    /// Emits the entire file as a single hunk so both sides render in full with the
    /// diff highlighting intact. Returns no hunks when nothing changed, keeping the
    /// "no differences" path consistent with the windowed mode.
    /// </summary>
    private static List<DiffHunk> BuildWholeFileHunks(List<DiffLine> lines)
    {
        var hunks = new List<DiffHunk>();
        if (lines.Count == 0)
            return hunks;

        var hasChange = false;
        foreach (var line in lines)
        {
            if (line.Kind != DiffLineKind.Context)
            {
                hasChange = true;
                break;
            }
        }

        if (hasChange)
            hunks.Add(BuildHunk(lines, 0, lines.Count - 1));

        return hunks;
    }

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
