namespace CodeIndex.Database;

public partial class DbReader
{
    private static class FindSnippetAssembler
    {
        internal static void FlushReadyMatches(
            FindCandidateFile file,
            Queue<PendingFileFindMatch> pendingMatches,
            Dictionary<int, string> snippetLinesByNumber,
            List<FileFindResult> results,
            int maxLineWidth,
            int availableThroughLine)
        {
            while (pendingMatches.Count > 0
                   && pendingMatches.Peek().SnippetEnd <= availableThroughLine)
            {
                var pending = pendingMatches.Dequeue();
                var lineNumbers = Enumerable
                    .Range(pending.SnippetStart, pending.SnippetEnd - pending.SnippetStart + 1)
                    .Where(snippetLinesByNumber.ContainsKey)
                    .ToList();
                if (lineNumbers.Count == 0)
                    continue;

                var lines = lineNumbers.Select(line => snippetLinesByNumber[line]).ToList();
                var (snippet, truncationContext) = ClampSnippetLines(
                    lines,
                    maxLineWidth,
                    lineNumbers.IndexOf(pending.LineNumber),
                    pending.Column + 1,
                    pending.Length);
                var matchLine = snippetLinesByNumber[pending.LineNumber];
                results.Add(new FileFindResult
                {
                    Path = file.Path,
                    Lang = file.Lang,
                    Line = pending.LineNumber,
                    Column = pending.Column + 1,
                    Length = pending.Length,
                    OriginalLineLength = matchLine.Length,
                    StartLine = lineNumbers[0],
                    EndLine = lineNumbers[^1],
                    Snippet = snippet,
                    SnippetTruncated = truncationContext.LineCount > 0,
                    SnippetTruncationContext = truncationContext,
                });
            }
        }

        private static (string Text, FileFindSnippetTruncationContext Context) ClampSnippetLines(
            IReadOnlyList<string> lines,
            int maxLineWidth,
            int focusLineIndex,
            int focusColumn,
            int focusLength)
        {
            if (lines.Count == 0)
                return (string.Empty, new FileFindSnippetTruncationContext());

            var output = new string[lines.Count];
            var truncatedCharCounts = new List<int>();
            for (var index = 0; index < lines.Count; index++)
            {
                var clamped = index == focusLineIndex
                    ? LineWidthFormatter.ClampLine(lines[index], maxLineWidth, focusColumn, focusLength)
                    : LineWidthFormatter.ClampLine(lines[index], maxLineWidth);
                output[index] = clamped.Text;
                if (clamped.Truncated)
                    truncatedCharCounts.Add(clamped.TruncatedCharCount);
            }

            return (
                string.Join('\n', output),
                new FileFindSnippetTruncationContext
                {
                    LineCount = truncatedCharCounts.Count,
                    CharCounts = truncatedCharCounts,
                    TotalChars = truncatedCharCounts.Sum(),
                    Reason = truncatedCharCounts.Count > 0 ? "line_width" : null,
                });
        }

        internal static void PruneWindow(
            int currentLine,
            int before,
            Queue<PendingFileFindMatch> pendingMatches,
            Queue<IndexedLine> snippetWindow,
            Dictionary<int, string> snippetLinesByNumber)
        {
            var minLineToKeep = currentLine - before;
            if (pendingMatches.Count > 0)
                minLineToKeep = Math.Min(minLineToKeep, pendingMatches.Peek().SnippetStart);

            while (snippetWindow.Count > 0 && snippetWindow.Peek().Number < minLineToKeep)
            {
                var removed = snippetWindow.Dequeue();
                snippetLinesByNumber.Remove(removed.Number);
            }
        }
    }
}
