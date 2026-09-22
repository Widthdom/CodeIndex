using System.Text;
using System.Text.RegularExpressions;

namespace CodeIndex.Database;

public partial class DbReader
{
    private sealed partial class IndexedFindPipeline
    {
        private bool ScanFileWindows(IndexedFindScanRequest request, IFindScanCollector collector,
            FindScanState state, FindCandidateFile file, Regex matcher)
        {
            var settings = request.Window!;
            var budget = state.WindowBudget!;
            using var source = EnumerateWindowLines(request, state, file).GetEnumerator();
            var lines = new List<IndexedLine>(settings.Lines);
            var exhausted = false;
            var nextLine = file.FirstEligibleLine;
            var nextColumn = 0;
            var resumeBytes = string.Equals(file.Path, request.Resume.Path, StringComparison.Ordinal)
                ? request.Resume.ByteOffset ?? 0 : 0;
            var first = true;
            while (true)
            {
                request.CancellationToken.ThrowIfCancellationRequested();
                while (lines.Count < settings.Lines && !exhausted)
                {
                    if (!source.MoveNext()) { exhausted = true; break; }
                    lines.Add(source.Current);
                    if (lines.Sum(item => Encoding.UTF8.GetByteCount(item.Text)) + lines.Count - 1 > settings.Bytes)
                    {
                        state.StopWindow("multiline_window_bytes");
                        return true;
                    }
                }
                if (state.Truncated || lines.Count == 0)
                    return state.Truncated;
                var line = lines[0];
                if (first)
                {
                    first = false;
                    nextColumn = DecodeWindowColumn(line.Text, resumeBytes);
                }
                if (line.Number < nextLine || line.Number == nextLine && nextColumn > line.Text.Length)
                {
                    lines.RemoveAt(0);
                    continue;
                }
                var startColumn = line.Number == nextLine ? nextColumn : 0;
                if (ReachedLineScanLimit(request, file, line, state))
                {
                    state.SetWindowResumeColumn(Encoding.UTF8.GetByteCount(line.Text.AsSpan(0, startColumn)));
                    return true;
                }
                if (budget.Lines >= FindWindowOptions.QueryLines)
                {
                    state.StopWindow("multiline_query_lines");
                    return true;
                }
                var bytes = lines.Sum(item => Encoding.UTF8.GetByteCount(item.Text)) + lines.Count - 1;
                if (bytes > settings.Bytes)
                {
                    state.StopWindow("multiline_window_bytes");
                    return true;
                }
                budget.BytesMatched += bytes;
                if (budget.BytesMatched > FindWindowOptions.QueryBytes)
                {
                    state.StopWindow("multiline_query_bytes");
                    return true;
                }
                if (WindowTimeExceeded(state)) return true;
                var text = string.Join('\n', lines.Select(item => item.Text));
                budget.Lines++;
                RecordScannedLine(request, collector, state, eligibleForMatch: true);
                collector.ObserveLine(line);
                var stop = false;
                // Each start belongs to one physical line; consumed spans suppress later starts.
                // 開始位置は単一の物理行に属し、消費済み範囲内の開始位置は除外する。
                for (var match = matcher.Match(text, startColumn); match.Success && match.Index <= line.Text.Length;)
                {
                    request.CancellationToken.ThrowIfCancellationRequested();
                    if (WindowTimeExceeded(state)) return true;
                    var end = WindowPosition(lines, match.Index + match.Length);
                    if (collector.AcceptMatch(file, line,
                        new FindLineMatch(match.Index, match.Length, EndLine: end.Line, EndColumn: end.Column + 1), 0))
                    {
                        stop = true;
                        break;
                    }
                    var following = match.Index + Math.Max(1, match.Length);
                    if (following > text.Length)
                    {
                        nextLine = lines[^1].Number;
                        nextColumn = lines[^1].Text.Length + 1;
                        break;
                    }
                    var next = WindowPosition(lines, following);
                    nextLine = next.Line;
                    nextColumn = next.Column;
                    if (nextLine != line.Number) break;
                    match = matcher.Match(text, following);
                }
                collector.CompleteLine(file, line);
                if (collector.CanStopAfterLine(stop)) return stop;
                lines.RemoveAt(0);
            }
        }

        private static (int Line, int Column) WindowPosition(List<IndexedLine> lines, int offset)
        {
            foreach (var line in lines)
            {
                if (offset <= line.Text.Length) return (line.Number, offset);
                offset -= line.Text.Length + 1;
            }
            throw new InvalidOperationException("Window coordinate exceeds its source.");
        }

        private static int DecodeWindowColumn(string text, int bytes)
        {
            var consumed = 0;
            for (var column = 0; column <= text.Length; column++)
            {
                if (consumed == bytes) return column;
                if (column == text.Length || consumed > bytes) break;
                var length = char.IsHighSurrogate(text[column]) && column + 1 < text.Length
                    && char.IsLowSurrogate(text[column + 1]) ? 2 : 1;
                // .NET zero-width matches can sit between surrogate code units.
                // .NET のゼロ幅一致は surrogate の code unit 間にも存在する。
                if (length == 2 && bytes == consumed + 3) return column + 1;
                consumed += Encoding.UTF8.GetByteCount(text.AsSpan(column, length));
                column += length - 1;
            }
            throw new FindContinuationException("cursor_malformed", "Multiline cursor is not a source position.");
        }

        private static bool WindowTimeExceeded(FindScanState state)
        {
            if (state.WindowBudget!.Clock.ElapsedMilliseconds < FindWindowOptions.QueryMilliseconds) return false;
            state.StopWindow("multiline_query_time");
            return true;
        }

        private IEnumerable<IndexedLine> EnumerateWindowLines(IndexedFindScanRequest request,
            FindScanState state, FindCandidateFile file)
        {
            using var command = _owner._conn.CreateCommand();
            // Gate source materialization in SQL; even legacy giant chunks remain bounded.
            // SQL で実体化前に制限し、古い索引の巨大チャンクも上限内に保つ。
            command.CommandText = """
                SELECT c.start_line, c.end_line, length(CAST(c.content AS BLOB)),
                    CASE WHEN length(CAST(c.content AS BLOB)) <= @chunkBytes THEN c.content END
                FROM chunks c WHERE c.file_id = @fileId AND c.end_line >= @firstLine
                ORDER BY c.start_line, c.chunk_index
                """;
            SqliteCommandPolicy.Add(command, "@fileId", file.Id);
            SqliteCommandPolicy.Add(command, "@firstLine", file.FirstEligibleLine);
            SqliteCommandPolicy.Add(command, "@chunkBytes", FindWindowOptions.ChunkBytes);
            var lastLine = file.FirstEligibleLine - 1;
            using var reader = command.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                request.CancellationToken.ThrowIfCancellationRequested();
                if (WindowTimeExceeded(state)) yield break;
                if (reader.IsDBNull(2) || reader.GetInt64(2) > FindWindowOptions.ChunkBytes)
                {
                    state.StopWindow("multiline_chunk_bytes");
                    yield break;
                }
                state.WindowBudget!.BytesRead += reader.GetInt64(2);
                if (state.WindowBudget.BytesRead > FindWindowOptions.QueryBytes)
                {
                    state.StopWindow("multiline_query_bytes");
                    yield break;
                }
                var content = reader.GetString(3);
                var start = 0;
                var number = reader.GetInt32(0);
                var endLine = Math.Min(file.TotalLines, reader.GetInt32(1));
                while (start <= content.Length && number <= endLine)
                {
                    request.CancellationToken.ThrowIfCancellationRequested();
                    var end = content.IndexOf('\n', start);
                    if (end < 0) end = content.Length;
                    if (number > lastLine)
                    {
                        if (number != lastLine + 1)
                        {
                            state.StopWindow("multiline_source_gap");
                            yield break;
                        }
                        var length = end - start;
                        if (length > 0 && content[end - 1] == '\r') length--;
                        if (Encoding.UTF8.GetByteCount(content.AsSpan(start, length)) > request.Window!.Bytes)
                        {
                            state.StopWindow("multiline_window_bytes");
                            yield break;
                        }
                        lastLine = number;
                        yield return new IndexedLine(number, content.Substring(start, length));
                    }
                    number++;
                    start = end + 1;
                }
            }
            if (lastLine < file.TotalLines)
                state.StopWindow("multiline_source_gap");
        }
    }
}
