using System.Globalization;
using CodeIndex.Indexer;

namespace CodeIndex.Database;

public partial class DbReader
{
    private const int MaxSameSymbolGuardSymbols = 512;
    private const int MaxSameSymbolGuardLines = 2048;
    private const int MaxSameSymbolGuardCharacters = 262144;
    private sealed record SameSymbolGuardRange(long SymbolId, string Name, int StartLine, int EndLine, string Kind);

    private void ValidateSameSymbolGuardContract()
    {
        if (!GetPersistedIndexCompletion().IndexComplete)
            throw SameSymbolGuardUnavailable("index_incomplete");
        if (!_symbolColumns.Contains("start_line") || !_symbolColumns.Contains("end_line") ||
            !string.Equals(GetMetaString(DbContext.GetSymbolExtractorVersionMetaKey("csharp")),
                SymbolExtractor.CSharpContractVersion.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            throw SameSymbolGuardUnavailable("symbol_ranges_stale_or_missing");
    }

    private static CodeIndexException SameSymbolGuardUnavailable(string reason)
        => new("same_symbol_scope_unavailable", CodeIndexExceptionCategory.Database,
            $"same_symbol_scope_unavailable: {reason}. No window fallback was applied.",
            hint: "Refresh the complete index with the current binary, narrow the query to supported C# callable ranges, or explicitly select --guard-scope window.");

    private List<SameSymbolGuardRange> ReadSameSymbolGuardContainers(string path)
    {
        ValidateSameSymbolGuardSource(path);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.id, substr(s.name, 1, 128), s.kind, s.start_line, s.end_line
            FROM symbols s JOIN files f ON f.id = s.file_id
            WHERE f.path = @path
              AND s.kind IN ('function', 'method', 'test.method', 'property', 'accessor', 'lambda')
            LIMIT @limit
            """;
        SqliteCommandPolicy.Add(cmd, "@path", path);
        SqliteCommandPolicy.Add(cmd, "@limit", MaxSameSymbolGuardSymbols + 1);
        var containers = new List<SameSymbolGuardRange>();
        using var rows = cmd.ExecuteTrackedReader();
        while (rows.TrackedRead())
        {
            ThrowIfCancellationRequested();
            if (containers.Count == MaxSameSymbolGuardSymbols)
                throw SameSymbolGuardUnavailable("symbol_range_budget_exceeded");
            if (rows.IsDBNull(3) || rows.IsDBNull(4) || rows.GetInt32(3) < 1 || rows.GetInt32(4) < rows.GetInt32(3))
                throw SameSymbolGuardUnavailable("symbol_ranges_invalid");
            containers.Add(new SameSymbolGuardRange(rows.GetInt64(0), rows.GetString(1),
                rows.GetInt32(3), rows.GetInt32(4), rows.GetString(2)));
        }
        return containers;
    }

    private void ValidateSameSymbolGuardSource(string path)
    {
        var root = GetIndexedProjectRoot();
        if (string.IsNullOrWhiteSpace(root) || Path.IsPathRooted(path))
            throw SameSymbolGuardUnavailable("source_root_unavailable");
        var absolute = Path.GetFullPath(Path.Combine(root, path));
        var relative = Path.GetRelativePath(root, absolute);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw SameSymbolGuardUnavailable("source_path_unavailable");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT {GetFileColumnSql("checksum")} FROM files f WHERE f.path = @path";
        SqliteCommandPolicy.Add(cmd, "@path", path);
        var checksum = cmd.ExecuteScalar() as string;
        try
        {
            var current = new FileContentLoader(4 * 1024 * 1024).Load(absolute, path, path, _cancellation).Checksum;
            if (string.IsNullOrEmpty(checksum) || !string.Equals(checksum, current, StringComparison.OrdinalIgnoreCase))
                throw SameSymbolGuardUnavailable("source_stale_missing_or_over_budget");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
            or FileIndexer.FileTooLargeSkippedException or FileIndexer.BinaryFileSkippedException)
        {
            throw SameSymbolGuardUnavailable("source_stale_missing_or_over_budget");
        }
    }

    private static SameSymbolGuardRange? ResolveSameSymbolGuardContainer(List<SameSymbolGuardRange> containers, int line)
    {
        SameSymbolGuardRange? selected = null;
        foreach (var candidate in containers)
        {
            if (line < candidate.StartLine || line > candidate.EndLine)
                continue;
            if (selected == null)
            {
                selected = candidate;
                continue;
            }
            if (candidate.StartLine > selected.StartLine && candidate.EndLine < selected.EndLine)
                selected = candidate;
            else if (!(candidate.StartLine < selected.StartLine && candidate.EndLine > selected.EndLine))
                throw SameSymbolGuardUnavailable("symbol_ranges_ambiguous");
        }
        return selected;
    }

    private SearchGuardEvaluation FindSameSymbolGuardEvidence(
        string path, SearchPrimaryMatch primaryMatch, SearchGuardFilter filter, int guardWindow, string? lang,
        Dictionary<SearchGuardLineWindowKey, SortedDictionary<int, string>> lineWindowCache,
        Dictionary<string, List<SameSymbolGuardRange>> symbolGuardFiles)
    {
        if (!string.Equals(lang, "csharp", StringComparison.OrdinalIgnoreCase))
            throw SameSymbolGuardUnavailable("language_unsupported");
        if (!symbolGuardFiles.TryGetValue(path, out var containers))
        {
            if (symbolGuardFiles.Count >= MaxSearchGuardLineWindowCacheEntries)
                throw SameSymbolGuardUnavailable("file_range_budget_exceeded");
            containers = ReadSameSymbolGuardContainers(path);
            symbolGuardFiles.Add(path, containers);
        }
        var container = ResolveSameSymbolGuardContainer(containers, primaryMatch.LineNumber)
            ?? throw SameSymbolGuardUnavailable("enclosing_symbol_missing");
        if (container.EndLine - container.StartLine >= MaxSameSymbolGuardLines)
            throw SameSymbolGuardUnavailable("symbol_source_budget_exceeded");

        // Line-only ranges cannot establish ownership across an unindexed anonymous
        // function or two declarations sharing a boundary line.
        var lines = ReadSameSymbolGuardLines(path, container, lineWindowCache);
        if (lines.Count != container.EndLine - container.StartLine + 1)
            throw SameSymbolGuardUnavailable("symbol_source_incomplete");
        if (lines.Values.Sum(line => (long)line.Length + 1) > MaxSameSymbolGuardCharacters)
            throw SameSymbolGuardUnavailable("symbol_source_budget_exceeded");
        var masked = MaskCSharpNonCode(string.Join('\n', lines.Values));
        var maskedLines = masked.Split('\n');
        for (var i = 0; i < maskedLines.Length; i++)
        {
            if (ResolveSameSymbolGuardContainer(containers, container.StartLine + i)?.SymbolId != container.SymbolId)
                maskedLines[i] = new string(' ', maskedLines[i].Length);
        }
        masked = string.Join('\n', maskedLines);
        // The lexical mask hides interpolation expressions, including anonymous
        // functions inside them. Their surviving '$' prefix makes the whole
        // selected callable unavailable until interpolation ownership is reliable.
        if (masked.Contains('$'))
            throw SameSymbolGuardUnavailable("interpolated_scope_unsupported");
        if (container.Kind == "lambda" || ContainsAnonymousDelegateKeyword(masked))
            throw SameSymbolGuardUnavailable("anonymous_scope_unsupported");
        var open = masked.IndexOf('{');
        var arrow = masked.IndexOf("=>", StringComparison.Ordinal);
        if (arrow >= 0 && (open < 0 || arrow < open))
        {
            if (open >= 0 || masked.IndexOf("=>", arrow + 2, StringComparison.Ordinal) >= 0 || !masked.TrimEnd().EndsWith(';'))
                throw SameSymbolGuardUnavailable("expression_scope_ambiguous");
        }
        else
        {
            if (arrow >= 0)
                throw SameSymbolGuardUnavailable("anonymous_scope_unsupported");
            var close = open < 0 ? -1 : FindMatchingDelimiter(masked, open, '{', '}');
            if (open < 0 || close < 0 || masked[(close + 1)..].Trim().Length != 0)
                throw SameSymbolGuardUnavailable("symbol_body_ambiguous_or_incomplete");
        }

        var start = filter.Direction == SearchGuardDirection.Before
            ? Math.Max(container.StartLine, primaryMatch.LineNumber - guardWindow)
            : primaryMatch.LineNumber + 1;
        var end = filter.Direction == SearchGuardDirection.Before
            ? primaryMatch.LineNumber - 1
            : Math.Min(container.EndLine, primaryMatch.LineNumber + guardWindow);
        foreach (var (line, text) in lines)
        {
            if (line < start || line > end)
                continue;
            // A nested local function owns its lines; it cannot guard its parent.
            if (ResolveSameSymbolGuardContainer(containers, line)?.SymbolId != container.SymbolId)
                continue;
            var query = NormalizeGuardQuery(filter.Query, lang);
            var candidate = CSharpVerbatimNameNormalizer.Normalize(text);
            if (query.Length == 0 || !candidate.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;
            var (index, length) = FindGuardMatchSpan(text, filter.Query, query, candidate);
            var column = index + 1;
            length = Math.Max(1, Math.Min(length, text.Length - index));
            var evidence = new SearchGuardEvidence
            {
                Role = FormatSearchGuardRole(filter.Role),
                Direction = FormatSearchGuardDirection(filter.Direction),
                Scope = "same_symbol",
                Query = filter.Query,
                Name = FormatSearchGuardName(filter),
                Pattern = filter.Query,
                Relationship = FormatSearchGuardDirection(filter.Direction),
                Container = container.Name,
                Line = line,
                Column = column,
                Length = length,
                Text = text,
                Span = new SearchGuardSpan { Line = line, Column = column, Length = length },
                Origin = SearchMatchClassifier.Classify(path, lang, line, text, column, length).Origin,
            };
            return new SearchGuardEvaluation(start, end, evidence, SymbolStartLine: container.StartLine, SymbolEndLine: container.EndLine);
        }
        return new SearchGuardEvaluation(start, end, null, SymbolStartLine: container.StartLine, SymbolEndLine: container.EndLine);
    }

    private static bool ContainsAnonymousDelegateKeyword(string masked)
    {
        for (var offset = 0; offset < masked.Length;)
        {
            var index = masked.IndexOf("delegate", offset, StringComparison.Ordinal);
            if (index < 0)
                return false;
            var end = index + "delegate".Length;
            if ((index == 0 || !(char.IsLetterOrDigit(masked[index - 1]) || masked[index - 1] is '_' or '@')) &&
                (end == masked.Length || !(char.IsLetterOrDigit(masked[end]) || masked[end] == '_')))
                return true;
            offset = end;
        }
        return false;
    }

    private SortedDictionary<int, string> ReadSameSymbolGuardLines(string path, SameSymbolGuardRange container,
        Dictionary<SearchGuardLineWindowKey, SortedDictionary<int, string>> cache)
    {
        var key = new SearchGuardLineWindowKey(path, container.StartLine, container.EndLine);
        if (cache.TryGetValue(key, out var cached))
            return cached;
        var lines = new SortedDictionary<int, string>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT c.start_line, substr(c.content, 1, @characters)
            FROM chunks c JOIN files f ON f.id = c.file_id
            WHERE f.path = @path AND c.end_line >= @start AND c.start_line <= @end
            ORDER BY c.start_line, c.id LIMIT 129
            """;
        SqliteCommandPolicy.Add(cmd, "@path", path);
        SqliteCommandPolicy.Add(cmd, "@start", container.StartLine);
        SqliteCommandPolicy.Add(cmd, "@end", container.EndLine);
        SqliteCommandPolicy.Add(cmd, "@characters", MaxSameSymbolGuardCharacters + 1);
        using var rows = cmd.ExecuteTrackedReader();
        var chunkCount = 0;
        long characters = 0;
        while (rows.TrackedRead())
        {
            ThrowIfCancellationRequested();
            var text = rows.GetString(1);
            characters += text.Length;
            if (++chunkCount > 128 || characters > MaxSameSymbolGuardCharacters)
                throw SameSymbolGuardUnavailable("symbol_source_budget_exceeded");
            foreach (var (offset, line) in EnumerateContentLines(text))
            {
                var number = rows.GetInt32(0) + offset;
                if (number < container.StartLine || number > container.EndLine)
                    continue;
                if (lines.TryGetValue(number, out var prior) && prior != line)
                    throw SameSymbolGuardUnavailable("symbol_source_inconsistent");
                lines[number] = line;
            }
        }
        if (cache.Count < MaxSearchGuardLineWindowCacheEntries)
            cache[key] = lines;
        return lines;
    }
}
