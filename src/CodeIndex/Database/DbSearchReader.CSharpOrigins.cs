namespace CodeIndex.Database;

public partial class DbReader
{
    private int _originPasses = 1;
    internal Action<int>? OriginWindowStartingForTesting { get; set; }
    internal int OriginPasses
    {
        get => _originPasses;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, SearchMatchClassifier.CSharpOriginPassLimit);
            _originPasses = value;
        }
    }

    private (long Local, long External) ReadOriginGeneration()
    {
        ThrowIfCancellationRequested();
        using var command = _conn.CreateCommand();
        command.CommandText = "SELECT total_changes(), data_version FROM pragma_data_version";
        using var reader = command.ExecuteTrackedReader();
        reader.TrackedRead();
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private SearchMatchClassifier.CSharpOriginWindow ReadCSharpOriginWindow(
        string path, int firstLine, (long Local, long External) generation, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        OriginWindowStartingForTesting?.Invoke(firstLine);
        cancellation.ThrowIfCancellationRequested();
        var lines = new Dictionary<int, string>();
        if (ReadOriginGeneration() != generation)
            return new(lines, "indexed_generation_changed");
        var endLine = firstLine + SearchMatchClassifier.CSharpContextLineLimit - 1;
        var budget = SearchMatchClassifier.CSharpContextCharacterLimit;
        var chunks = 0;
        var conflictLine = int.MaxValue;
        var reason = "indexed_prefix_unavailable";
        using var command = _conn.CreateCommand();
        command.CommandText = @"
            SELECT c.start_line, c.end_line, substr(c.content, 1, @characters + 1)
            FROM chunks c JOIN files f ON c.file_id = f.id
            WHERE f.path = @path AND c.start_line <= @endLine AND c.end_line >= @firstLine
            ORDER BY c.start_line, c.id LIMIT @chunks";
        SqliteCommandPolicy.Add(command, "@path", path);
        SqliteCommandPolicy.Add(command, "@firstLine", firstLine);
        SqliteCommandPolicy.Add(command, "@endLine", endLine);
        SqliteCommandPolicy.Add(command, "@characters", budget);
        SqliteCommandPolicy.Add(command, "@chunks", SearchMatchClassifier.CSharpContextChunkLimit);
        using var reader = command.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            cancellation.ThrowIfCancellationRequested();
            ThrowIfCancellationRequested();
            chunks++;
            if (reader.IsDBNull(2))
                break;
            var start = reader.GetInt32(0);
            var last = Math.Min(endLine, reader.GetInt32(1));
            if (start < 1 || last < start)
                break;
            var content = reader.GetString(2);
            var truncated = content.Length > budget;
            if (truncated)
            {
                // Charge overlap too; a partial physical line never becomes evidence.
                var newline = budget == 0 ? -1 : content.LastIndexOf('\n', budget - 1, budget);
                content = newline < 0 ? string.Empty : content[..(newline + 1)];
                reason = "character_budget_exhausted";
            }
            budget -= content.Length;
            var lastOffset = truncated ? Math.Min(last - start, content.Count(ch => ch == '\n') - 1) : last - start;
            foreach (var (offset, value) in EnumerateContentLines(content, Math.Max(0, firstLine - start), lastOffset))
            {
                cancellation.ThrowIfCancellationRequested();
                if (!lines.TryAdd(start + offset, value) && lines[start + offset] != value)
                    conflictLine = Math.Min(conflictLine, start + offset);
            }
            if (truncated)
                break;
        }
        if (conflictLine != int.MaxValue)
        {
            lines.Remove(conflictLine);
            reason = "indexed_text_mismatch";
        }
        else if (chunks == SearchMatchClassifier.CSharpContextChunkLimit && reason != "character_budget_exhausted")
            reason = "chunk_budget_exhausted";
        // A hole inside materialized evidence cannot be repaired by skipping to a later window.
        var contiguousEnd = firstLine;
        while (lines.ContainsKey(contiguousEnd))
            contiguousEnd++;
        if (lines.Keys.Any(line => line > contiguousEnd) && conflictLine == int.MaxValue)
            reason = "indexed_prefix_unavailable";
        return new(lines, reason);
    }
}
