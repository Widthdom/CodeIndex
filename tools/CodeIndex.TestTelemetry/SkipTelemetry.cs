using System.Globalization;
using System.Text;

namespace CodeIndex.TestTelemetry;

public static class SkipTelemetry
{
    public const int MaxTop = 200;
    public const int MaxSourceFiles = 4096;
    public const int MaxTraversalDirectories = 512;
    public const int MaxTraversalEntries = 16384;
    public const long MaxSourceFileBytes = 8 * 1024 * 1024;

    public static SkipTelemetrySummary Load(string testsDirectory, int top)
    {
        if (top <= 0 || top > MaxTop)
            throw new TelemetryException($"Top count must be between 1 and {MaxTop}.");

        if (!Directory.Exists(testsDirectory))
        {
            return new SkipTelemetrySummary(
                TestsDirectory: testsDirectory,
                CSharpFileCount: 0,
                SkippedTests: 0,
                WithOwner: 0,
                WithExpiry: 0,
                WithScenario: 0,
                DisplayLimit: top,
                Entries: [],
                ByArea: [],
                ByScenario: [],
                ByReason: [],
                Warnings: [$"Tests directory not found: {testsDirectory}"]);
        }

        var warnings = new List<string>();
        var sourceFiles = EnumerateSourceFiles(testsDirectory, warnings);
        var entries = new List<SkipTelemetryEntry>();

        foreach (var path in sourceFiles)
        {
            if (!CanReadSourceFile(testsDirectory, path, warnings))
                continue;

            try
            {
                var source = File.ReadAllText(path);
                entries.AddRange(CSharpSkipScanner.Scan(testsDirectory, path, source));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Could not read {FormatSourcePath(testsDirectory, path)}: {GetWarningReason(ex)}");
            }
        }

        entries.Sort(CompareEntries);

        return new SkipTelemetrySummary(
            TestsDirectory: testsDirectory,
            CSharpFileCount: sourceFiles.Count,
            SkippedTests: entries.Count,
            WithOwner: entries.Count(entry => entry.HasOwner),
            WithExpiry: entries.Count(entry => entry.HasExpiry),
            WithScenario: entries.Count(entry => entry.HasScenario),
            DisplayLimit: top,
            Entries: entries,
            ByArea: BuildCounts(entries, entry => entry.Area),
            ByScenario: BuildCounts(entries, entry => entry.Scenario),
            ByReason: BuildCounts(entries, entry => entry.Reason),
            Warnings: warnings);
    }

    private static List<string> EnumerateSourceFiles(string testsDirectory, List<string> warnings)
    {
        var files = new List<string>(Math.Min(MaxSourceFiles, 128));
        var pendingDirectories = new Queue<string>();
        pendingDirectories.Enqueue(testsDirectory);
        var visitedDirectories = 0;
        var visitedEntries = 0;

        while (pendingDirectories.Count > 0)
        {
            if (visitedDirectories >= MaxTraversalDirectories)
            {
                warnings.Add($"C# source directory traversal cap reached: visited first {MaxTraversalDirectories} directories.");
                break;
            }

            var directory = pendingDirectories.Dequeue();
            visitedDirectories++;

            foreach (var entry in EnumerateDirectoryEntries(directory, warnings))
            {
                visitedEntries++;
                if (visitedEntries > MaxTraversalEntries)
                {
                    warnings.Add($"C# source entry traversal cap reached: visited first {MaxTraversalEntries} entries.");
                    files.Sort(StringComparer.Ordinal);
                    return files;
                }

                if (!TryGetAttributes(entry, warnings, out var attributes))
                    continue;

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    continue;

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pendingDirectories.Enqueue(entry);
                    continue;
                }

                if ((attributes & FileAttributes.Device) != 0)
                    continue;

                if (!string.Equals(Path.GetExtension(entry), ".cs", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (files.Count >= MaxSourceFiles)
                {
                    warnings.Add($"C# source file cap reached: using first {MaxSourceFiles} files.");
                    files.Sort(StringComparer.Ordinal);
                    return files;
                }

                files.Add(entry);
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static IEnumerable<string> EnumerateDirectoryEntries(string directory, List<string> warnings)
    {
        IEnumerator<string>? enumerator;
        try
        {
            enumerator = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not enumerate C# source directory: {GetWarningReason(ex)}");
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                string entry;
                try
                {
                    if (!enumerator.MoveNext())
                        yield break;

                    entry = enumerator.Current;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"Could not enumerate C# source directory: {GetWarningReason(ex)}");
                    yield break;
                }

                yield return entry;
            }
        }
    }

    private static bool TryGetAttributes(string path, List<string> warnings, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not inspect C# source traversal entry: {GetWarningReason(ex)}");
            attributes = default;
            return false;
        }
    }

    private static bool CanReadSourceFile(string testsDirectory, string path, List<string> warnings)
    {
        try
        {
            var file = new FileInfo(path);
            if (file.Length > MaxSourceFileBytes)
            {
                warnings.Add($"C# source file exceeds {MaxSourceFileBytes} byte cap: {FormatSourcePath(testsDirectory, path)}");
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not inspect {FormatSourcePath(testsDirectory, path)}: {GetWarningReason(ex)}");
            return false;
        }
    }

    private static IReadOnlyList<SkipTelemetryCount> BuildCounts(
        IEnumerable<SkipTelemetryEntry> entries,
        Func<SkipTelemetryEntry, string> selector) =>
        entries
            .GroupBy(selector, StringComparer.Ordinal)
            .Select(group => new SkipTelemetryCount(group.Key, group.Count()))
            .OrderByDescending(count => count.Count)
            .ThenBy(count => count.Name, StringComparer.Ordinal)
            .ToArray();

    private static int CompareEntries(SkipTelemetryEntry left, SkipTelemetryEntry right)
    {
        var file = string.Compare(left.FilePath, right.FilePath, StringComparison.Ordinal);
        if (file != 0)
            return file;

        var line = left.Line.CompareTo(right.Line);
        return line != 0
            ? line
            : string.Compare(left.TestName, right.TestName, StringComparison.Ordinal);
    }

    private static string FormatSourcePath(string testsDirectory, string path)
    {
        try
        {
            var relativePath = Path.GetRelativePath(testsDirectory, path);
            if (!IsParentTraversal(relativePath) && !Path.IsPathFullyQualified(relativePath))
                return NormalizePath(relativePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
        }

        var fileName = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(fileName) ? "<source-file>" : fileName;
    }

    private static bool IsParentTraversal(string path) =>
        path == ".." ||
        path.StartsWith("../", StringComparison.Ordinal) ||
        path.StartsWith(@"..\", StringComparison.Ordinal);

    private static string NormalizePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    private static string GetWarningReason(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "access_denied",
        IOException => "io_error",
        _ => "unknown_error"
    };

    private sealed class CSharpSkipScanner
    {
        public static IReadOnlyList<SkipTelemetryEntry> Scan(string testsDirectory, string path, string source)
        {
            var entries = new List<SkipTelemetryEntry>();
            var lineStarts = BuildLineStarts(source);
            var index = 0;

            while (index < source.Length)
            {
                if (TrySkipTriviaToken(source, index, out var triviaEnd))
                {
                    index = triviaEnd;
                    continue;
                }

                if (source[index] == '[' &&
                    TryReadAttributeBlock(source, index, out var block) &&
                    block.Skips.Count > 0)
                {
                    var testName = FindNextMethodName(source, block.End) ?? "<unknown-test>";
                    var area = ResolveArea(block.Traits, path);
                    var scenario = ResolveScenario(block.Traits);
                    var filePath = FormatSourcePath(testsDirectory, path);
                    var hasScenario = !string.Equals(scenario, "Uncategorized", StringComparison.Ordinal);

                    foreach (var skip in block.Skips)
                    {
                        var reason = NormalizeReason(skip.Reason);
                        entries.Add(new SkipTelemetryEntry(
                            FilePath: filePath,
                            Line: GetLineNumber(lineStarts, skip.Start),
                            TestName: testName,
                            Area: area,
                            Scenario: scenario,
                            Reason: reason,
                            HasOwner: ContainsGovernanceToken(reason, "owner"),
                            HasExpiry: ContainsGovernanceToken(reason, "expires") ||
                                ContainsGovernanceToken(reason, "expiry") ||
                                ContainsGovernanceToken(reason, "until"),
                            HasScenario: hasScenario));
                    }

                    index = block.End;
                    continue;
                }

                index++;
            }

            return entries;
        }

        private static bool TrySkipTriviaToken(string source, int index, out int end)
        {
            end = index;
            if (index >= source.Length)
                return false;

            if (source[index] == '"' || source[index] == '\'')
            {
                end = SkipQuotedLiteral(source, index);
                return end > index;
            }

            if (source[index] == '/' && index + 1 < source.Length)
            {
                if (source[index + 1] == '/')
                {
                    end = SkipLineComment(source, index);
                    return true;
                }

                if (source[index + 1] == '*')
                {
                    end = SkipBlockComment(source, index);
                    return true;
                }
            }

            return false;
        }

        private static bool TryReadAttributeBlock(string source, int start, out AttributeBlock block)
        {
            var attributes = new List<ParsedAttribute>();
            var current = start;

            while (current < source.Length && source[current] == '[')
            {
                if (!TryFindAttributeListEnd(source, current, out var listEnd))
                {
                    block = new AttributeBlock([], current);
                    return false;
                }

                attributes.AddRange(ParseAttributes(source, current + 1, listEnd));
                current = SkipTriviaAndDirectives(source, listEnd + 1);
            }

            var skips = attributes
                .Where(attribute => attribute.SkipReason is not null)
                .Select(attribute => new SkipAttribute(attribute.SkipReason!, attribute.Start))
                .ToArray();
            var traits = attributes
                .Where(attribute => attribute.TraitName is not null && attribute.TraitValue is not null)
                .Select(attribute => new TraitAttribute(attribute.TraitName!, attribute.TraitValue!))
                .ToArray();

            block = new AttributeBlock(skips, traits, current);
            return true;
        }

        private static bool TryFindAttributeListEnd(string source, int start, out int end)
        {
            var depth = 1;
            for (var index = start + 1; index < source.Length; index++)
            {
                if (TrySkipTriviaToken(source, index, out var skipped))
                {
                    index = skipped - 1;
                    continue;
                }

                var c = source[index];
                if (c == '[')
                {
                    depth++;
                    continue;
                }

                if (c != ']')
                    continue;

                depth--;
                if (depth == 0)
                {
                    end = index;
                    return true;
                }
            }

            end = source.Length;
            return false;
        }

        private static IReadOnlyList<ParsedAttribute> ParseAttributes(string source, int start, int end)
        {
            var attributes = new List<ParsedAttribute>();
            foreach (var segment in SplitTopLevel(source, start, end, ','))
            {
                var trimmed = TrimSegment(source, segment.Start, segment.End);
                if (trimmed.Start >= trimmed.End)
                    continue;

                var parsed = ParseAttribute(source, trimmed.Start, trimmed.End);
                if (parsed is not null)
                    attributes.Add(parsed);
            }

            return attributes;
        }

        private static ParsedAttribute? ParseAttribute(string source, int start, int end)
        {
            var nameEnd = start;
            while (nameEnd < end && IsAttributeNameChar(source[nameEnd]))
            {
                nameEnd++;
            }

            if (nameEnd == start)
                return null;

            var name = NormalizeAttributeName(source[start..nameEnd]);
            var openParen = SkipSpaces(source, nameEnd, end);
            if (openParen >= end || source[openParen] != '(')
                return new ParsedAttribute(name, null, null, null, start);

            if (!TryFindMatchingParen(source, openParen, end, out var closeParen))
                return new ParsedAttribute(name, null, null, null, start);

            var argsStart = openParen + 1;
            var argsEnd = closeParen;

            if (IsXunitTestAttribute(name))
            {
                var skipReason = TryGetNamedArgument(source, argsStart, argsEnd, "Skip");
                return new ParsedAttribute(name, skipReason, null, null, start);
            }

            if (string.Equals(name, "Trait", StringComparison.Ordinal) &&
                TryGetTrait(source, argsStart, argsEnd, out var traitName, out var traitValue))
            {
                return new ParsedAttribute(name, null, traitName, traitValue, start);
            }

            return new ParsedAttribute(name, null, null, null, start);
        }

        private static bool IsXunitTestAttribute(string name) =>
            string.Equals(name, "Fact", StringComparison.Ordinal) ||
            string.Equals(name, "Theory", StringComparison.Ordinal);

        private static string NormalizeAttributeName(string name)
        {
            var simpleName = name;
            var dot = simpleName.LastIndexOf('.');
            if (dot >= 0 && dot + 1 < simpleName.Length)
                simpleName = simpleName[(dot + 1)..];

            var colon = simpleName.LastIndexOf(':');
            if (colon >= 0 && colon + 1 < simpleName.Length)
                simpleName = simpleName[(colon + 1)..];

            const string suffix = "Attribute";
            if (simpleName.EndsWith(suffix, StringComparison.Ordinal) && simpleName.Length > suffix.Length)
                simpleName = simpleName[..^suffix.Length];

            return simpleName;
        }

        private static string? TryGetNamedArgument(string source, int start, int end, string name)
        {
            foreach (var segment in SplitTopLevel(source, start, end, ','))
            {
                if (!TryFindTopLevelEquals(source, segment.Start, segment.End, out var equals))
                    continue;

                var left = TrimSegment(source, segment.Start, equals);
                if (!SegmentEquals(source, left.Start, left.End, name))
                    continue;

                var right = TrimSegment(source, equals + 1, segment.End);
                return NormalizeExpression(source[right.Start..right.End]);
            }

            return null;
        }

        private static bool TryGetTrait(
            string source,
            int start,
            int end,
            out string traitName,
            out string traitValue)
        {
            traitName = "";
            traitValue = "";
            var args = SplitTopLevel(source, start, end, ',').ToArray();
            if (args.Length < 2)
                return false;

            var nameSegment = TrimSegment(source, args[0].Start, args[0].End);
            var valueSegment = TrimSegment(source, args[1].Start, args[1].End);
            if (!TryReadStringLiteralValue(source[nameSegment.Start..nameSegment.End], out traitName) ||
                !TryReadStringLiteralValue(source[valueSegment.Start..valueSegment.End], out traitValue))
            {
                return false;
            }

            return true;
        }

        private static IEnumerable<Segment> SplitTopLevel(string source, int start, int end, char separator)
        {
            var segmentStart = start;
            var parenDepth = 0;
            var bracketDepth = 0;
            var braceDepth = 0;

            for (var index = start; index < end; index++)
            {
                if (TrySkipTriviaToken(source, index, out var skipped))
                {
                    index = skipped - 1;
                    continue;
                }

                switch (source[index])
                {
                    case '(':
                        parenDepth++;
                        break;
                    case ')':
                        parenDepth = Math.Max(0, parenDepth - 1);
                        break;
                    case '[':
                        bracketDepth++;
                        break;
                    case ']':
                        bracketDepth = Math.Max(0, bracketDepth - 1);
                        break;
                    case '{':
                        braceDepth++;
                        break;
                    case '}':
                        braceDepth = Math.Max(0, braceDepth - 1);
                        break;
                    default:
                        if (source[index] == separator &&
                            parenDepth == 0 &&
                            bracketDepth == 0 &&
                            braceDepth == 0)
                        {
                            yield return new Segment(segmentStart, index);
                            segmentStart = index + 1;
                        }

                        break;
                }
            }

            yield return new Segment(segmentStart, end);
        }

        private static bool TryFindMatchingParen(string source, int start, int end, out int closeParen)
        {
            var depth = 1;
            for (var index = start + 1; index < end; index++)
            {
                if (TrySkipTriviaToken(source, index, out var skipped))
                {
                    index = skipped - 1;
                    continue;
                }

                if (source[index] == '(')
                {
                    depth++;
                    continue;
                }

                if (source[index] != ')')
                    continue;

                depth--;
                if (depth == 0)
                {
                    closeParen = index;
                    return true;
                }
            }

            closeParen = end;
            return false;
        }

        private static bool TryFindTopLevelEquals(string source, int start, int end, out int equals)
        {
            var parenDepth = 0;
            var bracketDepth = 0;
            var braceDepth = 0;

            for (var index = start; index < end; index++)
            {
                if (TrySkipTriviaToken(source, index, out var skipped))
                {
                    index = skipped - 1;
                    continue;
                }

                switch (source[index])
                {
                    case '(':
                        parenDepth++;
                        break;
                    case ')':
                        parenDepth = Math.Max(0, parenDepth - 1);
                        break;
                    case '[':
                        bracketDepth++;
                        break;
                    case ']':
                        bracketDepth = Math.Max(0, bracketDepth - 1);
                        break;
                    case '{':
                        braceDepth++;
                        break;
                    case '}':
                        braceDepth = Math.Max(0, braceDepth - 1);
                        break;
                    case '=':
                        if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                        {
                            equals = index;
                            return true;
                        }

                        break;
                }
            }

            equals = -1;
            return false;
        }

        private static string? FindNextMethodName(string source, int start)
        {
            var index = SkipTriviaAndDirectives(source, start);
            while (index < source.Length && source[index] == '[')
            {
                if (!TryFindAttributeListEnd(source, index, out var attributeEnd))
                    return null;

                index = SkipTriviaAndDirectives(source, attributeEnd + 1);
            }

            var lastIdentifier = "";
            while (index < source.Length)
            {
                if (TrySkipTriviaToken(source, index, out var skipped))
                {
                    index = skipped;
                    continue;
                }

                if (IsIdentifierStart(source[index]))
                {
                    var identifierStart = index;
                    index++;
                    while (index < source.Length && IsIdentifierPart(source[index]))
                    {
                        index++;
                    }

                    lastIdentifier = source[identifierStart..index];
                    continue;
                }

                if (source[index] == '(')
                    return string.IsNullOrWhiteSpace(lastIdentifier) ? null : lastIdentifier;

                if (source[index] is '{' or ';' or '=')
                    return null;

                index++;
            }

            return null;
        }

        private static int SkipTriviaAndDirectives(string source, int start)
        {
            var index = start;
            while (index < source.Length)
            {
                if (char.IsWhiteSpace(source[index]))
                {
                    index++;
                    continue;
                }

                if (source[index] == '/' && index + 1 < source.Length)
                {
                    if (source[index + 1] == '/')
                    {
                        index = SkipLineComment(source, index);
                        continue;
                    }

                    if (source[index + 1] == '*')
                    {
                        index = SkipBlockComment(source, index);
                        continue;
                    }
                }

                if (source[index] == '#' && IsDirectiveStart(source, index))
                {
                    index = SkipLine(source, index);
                    continue;
                }

                break;
            }

            return index;
        }

        private static int SkipSpaces(string source, int start, int end)
        {
            var index = start;
            while (index < end && char.IsWhiteSpace(source[index]))
            {
                index++;
            }

            return index;
        }

        private static bool IsDirectiveStart(string source, int index)
        {
            for (var i = index - 1; i >= 0; i--)
            {
                if (source[i] is '\n' or '\r')
                    return true;

                if (!char.IsWhiteSpace(source[i]))
                    return false;
            }

            return true;
        }

        private static int SkipQuotedLiteral(string source, int start)
        {
            if (source[start] == '\'')
                return SkipCharacterLiteral(source, start);

            var quoteRun = CountQuoteRun(source, start);
            if (quoteRun >= 3)
                return SkipRawStringLiteral(source, start, quoteRun);

            if (HasVerbatimPrefix(source, start))
                return SkipVerbatimStringLiteral(source, start);

            return SkipRegularStringLiteral(source, start);
        }

        private static int SkipRegularStringLiteral(string source, int start)
        {
            for (var index = start + 1; index < source.Length; index++)
            {
                if (source[index] == '\\')
                {
                    index++;
                    continue;
                }

                if (source[index] == '"')
                    return index + 1;
            }

            return source.Length;
        }

        private static int SkipVerbatimStringLiteral(string source, int start)
        {
            for (var index = start + 1; index < source.Length; index++)
            {
                if (source[index] != '"')
                    continue;

                if (index + 1 < source.Length && source[index + 1] == '"')
                {
                    index++;
                    continue;
                }

                return index + 1;
            }

            return source.Length;
        }

        private static int SkipRawStringLiteral(string source, int start, int quoteRun)
        {
            for (var index = start + quoteRun; index < source.Length; index++)
            {
                if (source[index] != '"' || CountQuoteRun(source, index) < quoteRun)
                    continue;

                return index + quoteRun;
            }

            return source.Length;
        }

        private static int SkipCharacterLiteral(string source, int start)
        {
            for (var index = start + 1; index < source.Length; index++)
            {
                if (source[index] == '\\')
                {
                    index++;
                    continue;
                }

                if (source[index] == '\'')
                    return index + 1;
            }

            return source.Length;
        }

        private static int SkipLineComment(string source, int start) => SkipLine(source, start + 2);

        private static int SkipBlockComment(string source, int start)
        {
            var end = source.IndexOf("*/", start + 2, StringComparison.Ordinal);
            return end < 0 ? source.Length : end + 2;
        }

        private static int SkipLine(string source, int start)
        {
            var newline = source.IndexOf('\n', start);
            return newline < 0 ? source.Length : newline + 1;
        }

        private static int CountQuoteRun(string source, int start)
        {
            var index = start;
            while (index < source.Length && source[index] == '"')
            {
                index++;
            }

            return index - start;
        }

        private static bool HasVerbatimPrefix(string source, int quoteIndex)
        {
            for (var index = quoteIndex - 1; index >= 0 && (source[index] == '@' || source[index] == '$'); index--)
            {
                if (source[index] == '@')
                    return true;
            }

            return false;
        }

        private static Segment TrimSegment(string source, int start, int end)
        {
            while (start < end && char.IsWhiteSpace(source[start]))
            {
                start++;
            }

            while (end > start && char.IsWhiteSpace(source[end - 1]))
            {
                end--;
            }

            return new Segment(start, end);
        }

        private static bool SegmentEquals(string source, int start, int end, string value) =>
            end - start == value.Length &&
            string.Compare(source, start, value, 0, value.Length, StringComparison.Ordinal) == 0;

        private static string ResolveArea(IReadOnlyList<TraitAttribute> traits, string path)
        {
            var area = traits.FirstOrDefault(trait =>
                string.Equals(trait.Name, "Area", StringComparison.OrdinalIgnoreCase))?.Value;
            if (!string.IsNullOrWhiteSpace(area))
                return area;

            var fileName = Path.GetFileNameWithoutExtension(path);
            return fileName.EndsWith("Tests", StringComparison.Ordinal) && fileName.Length > "Tests".Length
                ? fileName[..^"Tests".Length]
                : fileName;
        }

        private static string ResolveScenario(IReadOnlyList<TraitAttribute> traits)
        {
            var scenario = traits.FirstOrDefault(trait =>
                string.Equals(trait.Name, "Scenario", StringComparison.OrdinalIgnoreCase))?.Value;
            if (!string.IsNullOrWhiteSpace(scenario))
                return scenario;

            var category = traits.FirstOrDefault(trait =>
                string.Equals(trait.Name, "Category", StringComparison.OrdinalIgnoreCase))?.Value;
            return string.IsNullOrWhiteSpace(category) ? "Uncategorized" : category;
        }

        private static string NormalizeReason(string expression)
        {
            return TryReadStringLiteralValue(expression, out var value)
                ? CollapseWhitespace(value)
                : CollapseWhitespace(expression);
        }

        private static string NormalizeExpression(string expression) => CollapseWhitespace(expression);

        private static bool ContainsGovernanceToken(string reason, string token)
        {
            for (var index = 0; index <= reason.Length - token.Length; index++)
            {
                if (!reason.AsSpan(index, token.Length).Equals(token, StringComparison.OrdinalIgnoreCase))
                    continue;

                var beforeOk = index == 0 || !IsIdentifierPart(reason[index - 1]);
                var after = index + token.Length;
                var afterOk = after >= reason.Length ||
                    char.IsWhiteSpace(reason[after]) ||
                    reason[after] is ':' or '=';
                if (beforeOk && afterOk)
                    return true;
            }

            return false;
        }

        private static bool TryReadStringLiteralValue(string expression, out string value)
        {
            value = "";
            var trimmed = expression.Trim();
            var quote = trimmed.IndexOf('"');
            if (quote < 0)
                return false;

            var quoteRun = CountQuoteRun(trimmed, quote);
            if (quoteRun >= 3)
            {
                var close = trimmed.LastIndexOf(new string('"', quoteRun), StringComparison.Ordinal);
                if (close <= quote)
                    return false;

                value = trimmed[(quote + quoteRun)..close];
                return true;
            }

            var closeQuote = trimmed.LastIndexOf('"');
            if (closeQuote <= quote)
                return false;

            var content = trimmed[(quote + 1)..closeQuote];
            if (trimmed[..quote].Contains('@', StringComparison.Ordinal))
            {
                value = content.Replace("\"\"", "\"", StringComparison.Ordinal);
                return true;
            }

            var builder = new StringBuilder(content.Length);
            for (var index = 0; index < content.Length; index++)
            {
                if (content[index] != '\\' || index + 1 >= content.Length)
                {
                    builder.Append(content[index]);
                    continue;
                }

                index++;
                builder.Append(content[index] switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '"' => '"',
                    '\\' => '\\',
                    _ => content[index]
                });
            }

            value = builder.ToString();
            return true;
        }

        private static string CollapseWhitespace(string value)
        {
            var builder = new StringBuilder(value.Length);
            var pendingSpace = false;
            foreach (var c in value.Trim())
            {
                if (char.IsWhiteSpace(c))
                {
                    pendingSpace = builder.Length > 0;
                    continue;
                }

                if (pendingSpace)
                {
                    builder.Append(' ');
                    pendingSpace = false;
                }

                builder.Append(c);
            }

            return builder.ToString();
        }

        private static int[] BuildLineStarts(string source)
        {
            var starts = new List<int> { 0 };
            for (var index = 0; index < source.Length; index++)
            {
                if (source[index] == '\n')
                    starts.Add(index + 1);
            }

            return starts.ToArray();
        }

        private static int GetLineNumber(int[] lineStarts, int index)
        {
            var line = Array.BinarySearch(lineStarts, index);
            if (line < 0)
                line = ~line - 1;

            return Math.Max(1, line + 1);
        }

        private static bool IsAttributeNameChar(char c) =>
            char.IsLetterOrDigit(c) ||
            c is '_' or '.' or ':';

        private static bool IsIdentifierStart(char c) =>
            char.IsLetter(c) ||
            c == '_';

        private static bool IsIdentifierPart(char c) =>
            char.IsLetterOrDigit(c) ||
            c == '_';

        private sealed record AttributeBlock(
            IReadOnlyList<SkipAttribute> Skips,
            IReadOnlyList<TraitAttribute> Traits,
            int End)
        {
            public AttributeBlock(IReadOnlyList<SkipAttribute> skips, int end)
                : this(skips, [], end)
            {
            }
        }

        private sealed record ParsedAttribute(
            string Name,
            string? SkipReason,
            string? TraitName,
            string? TraitValue,
            int Start);

        private sealed record SkipAttribute(string Reason, int Start);

        private sealed record TraitAttribute(string Name, string Value);

        private readonly record struct Segment(int Start, int End);
    }
}

public static class SkipTelemetryRenderer
{
    public static string Render(SkipTelemetrySummary summary)
    {
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        writer.WriteLine("Test skip governance summary");
        writer.WriteLine($"Tests directory: {summary.TestsDirectory}");
        writer.WriteLine($"C# files scanned: {summary.CSharpFileCount}");
        writer.WriteLine($"Skipped test annotations: {summary.SkippedTests}");
        writer.WriteLine($"Governance tokens: owner: {summary.WithOwner}; expires: {summary.WithExpiry}; scenario/category trait: {summary.WithScenario}");

        foreach (var warning in summary.Warnings)
        {
            writer.WriteLine($"Warning: {warning}");
        }

        if (summary.SkippedTests == 0)
            return writer.ToString();

        WriteCounts(writer, "By area", summary.ByArea);
        WriteCounts(writer, "By scenario/category", summary.ByScenario);
        WriteCounts(writer, "By reason", summary.ByReason);

        writer.WriteLine();
        writer.WriteLine($"Skipped tests (first {Math.Min(summary.DisplayLimit, summary.Entries.Count)}):");
        foreach (var entry in summary.Entries.Take(summary.DisplayLimit))
        {
            writer.WriteLine(
                $"- {entry.FilePath}:{entry.Line} {entry.TestName} " +
                $"[area: {entry.Area}; scenario: {entry.Scenario}; owner: {FormatBool(entry.HasOwner)}; expires: {FormatBool(entry.HasExpiry)}] " +
                $"reason: {entry.Reason}");
        }

        return writer.ToString();
    }

    private static void WriteCounts(StringWriter writer, string title, IReadOnlyList<SkipTelemetryCount> counts)
    {
        if (counts.Count == 0)
            return;

        writer.WriteLine();
        writer.WriteLine($"{title}:");
        foreach (var count in counts)
        {
            writer.WriteLine($"- {count.Name}: {count.Count}");
        }
    }

    private static string FormatBool(bool value) => value ? "yes" : "no";
}

public sealed record SkipTelemetrySummary(
    string TestsDirectory,
    int CSharpFileCount,
    int SkippedTests,
    int WithOwner,
    int WithExpiry,
    int WithScenario,
    int DisplayLimit,
    IReadOnlyList<SkipTelemetryEntry> Entries,
    IReadOnlyList<SkipTelemetryCount> ByArea,
    IReadOnlyList<SkipTelemetryCount> ByScenario,
    IReadOnlyList<SkipTelemetryCount> ByReason,
    IReadOnlyList<string> Warnings);

public sealed record SkipTelemetryEntry(
    string FilePath,
    int Line,
    string TestName,
    string Area,
    string Scenario,
    string Reason,
    bool HasOwner,
    bool HasExpiry,
    bool HasScenario);

public sealed record SkipTelemetryCount(string Name, int Count);
