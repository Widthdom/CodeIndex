using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeIndex.Models;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

internal static class JsonReferenceExtractor
{
    private static readonly Regex RepositoryLocalPathRegex = new(
        @"(?:(?<![A-Za-z0-9_.:/\\-])|(?<=\)/))(?<path>(?:\./)?(?:[A-Za-z0-9_.-]+[\\/])+[A-Za-z0-9_.-]+\.[A-Za-z0-9]{1,16})(?![A-Za-z0-9_.:/\\-])",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    internal static List<ReferenceRecord> Extract(
        long fileId,
        string content,
        string[] lines,
        int? maxReferenceCount)
    {
        var references = ReferenceExtractor.CreateReferenceList(maxReferenceCount, Math.Min(lines.Length, 64));
        if (content.Length > SymbolExtractor.StructuredDataMaxJsonParseChars)
            return references;

        var utf8 = Encoding.UTF8.GetBytes(content);
        if (utf8.Length > SymbolExtractor.StructuredDataMaxJsonParseUtf8Bytes)
            return references;

        var seen = new ReferenceDedupeSet();
        var lineStarts = BuildUtf8LineStarts(utf8);
        var reader = new Utf8JsonReader(
            utf8,
            new JsonReaderOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = SymbolExtractor.StructuredDataMaxJsonDepth + 2,
            });

        try
        {
            while (reader.Read() && !ReferenceExtractor.ReferenceLimitReached(references))
            {
                if (reader.TokenType != JsonTokenType.String)
                    continue;

                var value = reader.GetString();
                if (string.IsNullOrEmpty(value))
                    continue;

                var tokenStart = checked((int)reader.TokenStartIndex);
                var lineIndex = FindLineIndex(lineStarts, tokenStart);
                var tokenColumn = Encoding.UTF8.GetCharCount(
                    utf8.AsSpan(lineStarts[lineIndex], tokenStart - lineStarts[lineIndex]));

                foreach (Match match in Regex.EnumerateMatches(RepositoryLocalPathRegex, value))
                {
                    var rawPath = match.Groups["path"].Value;
                    var normalizedPath = rawPath.Replace('\\', '/');
                    if (normalizedPath.StartsWith("./", StringComparison.Ordinal))
                        normalizedPath = normalizedPath[2..];
                    if (!IsSafeRepositoryLocalPath(normalizedPath))
                        continue;

                    ReferenceExtractor.AddReference(
                        references,
                        seen,
                        fileId,
                        normalizedPath,
                        tokenColumn + 1 + MapDecodedIndexToSourceCharacterOffset(
                            reader.ValueSpan,
                            match.Groups["path"].Index),
                        "project_reference",
                        lines[lineIndex],
                        lineIndex + 1,
                        container: null,
                        "json");
                    if (ReferenceExtractor.ReferenceLimitReached(references))
                        break;
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return references;
    }

    internal static List<ReferenceRecord> ExtractJsonLines(
        long fileId,
        string[] lines,
        IReadOnlyList<SymbolRecord> symbols,
        int? maxReferenceCount)
    {
        var references = ReferenceExtractor.CreateReferenceList(maxReferenceCount, Math.Min(lines.Length, 64));
        var recordContainers = BuildJsonLinesRecordContainerMap(symbols);
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var record = lines[lineIndex];
            if (string.IsNullOrWhiteSpace(record))
                continue;

            var recordReferences = Extract(fileId, record, [record], maxReferenceCount);
            recordContainers.TryGetValue(lineIndex + 1, out var container);
            foreach (var reference in recordReferences)
            {
                reference.Line = lineIndex + 1;
                reference.Context = record;
                reference.ContainerKind = container?.Kind;
                reference.ContainerName = container?.Name;
                if (!ReferenceExtractor.TryAddReference(references, reference))
                    return references;
            }
        }

        return references;
    }

    private static Dictionary<int, SymbolRecord> BuildJsonLinesRecordContainerMap(
        IReadOnlyList<SymbolRecord> symbols)
    {
        var containers = new Dictionary<int, SymbolRecord>(Math.Min(symbols.Count, 4096));
        for (var index = 0; index < symbols.Count; index++)
        {
            var symbol = symbols[index];
            if (symbol.Line > 0 && symbol.Kind == "record")
                containers[symbol.Line] = symbol;
        }

        return containers;
    }

    private static int MapDecodedIndexToSourceCharacterOffset(ReadOnlySpan<byte> rawValue, int decodedIndex)
    {
        var rawByteIndex = 0;
        var sourceCharacterOffset = 0;
        var decodedCharacterOffset = 0;
        while (rawByteIndex < rawValue.Length && decodedCharacterOffset < decodedIndex)
        {
            if (rawValue[rawByteIndex] == (byte)'\\')
            {
                var escapedCharacterCount = rawByteIndex + 1 < rawValue.Length && rawValue[rawByteIndex + 1] == (byte)'u'
                    ? 6
                    : 2;
                rawByteIndex += escapedCharacterCount;
                sourceCharacterOffset += escapedCharacterCount;
                decodedCharacterOffset++;
                continue;
            }

            var utf8SequenceLength = GetUtf8SequenceLength(rawValue[rawByteIndex]);
            var utf16CharacterCount = Encoding.UTF8.GetCharCount(rawValue.Slice(rawByteIndex, utf8SequenceLength));
            rawByteIndex += utf8SequenceLength;
            sourceCharacterOffset += utf16CharacterCount;
            decodedCharacterOffset += utf16CharacterCount;
        }

        return sourceCharacterOffset;
    }

    private static int GetUtf8SequenceLength(byte leadingByte) => leadingByte switch
    {
        < 0x80 => 1,
        < 0xE0 => 2,
        < 0xF0 => 3,
        _ => 4,
    };

    private static bool IsSafeRepositoryLocalPath(string path)
    {
        if (path.Length == 0 || path.Length > SymbolExtractor.StructuredDataMaxPathLength || path.StartsWith("/", StringComparison.Ordinal))
            return false;

        var segmentStart = 0;
        for (var index = 0; index <= path.Length; index++)
        {
            if (index < path.Length && path[index] != '/')
                continue;

            var segmentLength = index - segmentStart;
            if (segmentLength == 0
                || segmentLength == 1 && path[segmentStart] == '.'
                || segmentLength == 2 && path[segmentStart] == '.' && path[segmentStart + 1] == '.')
            {
                return false;
            }

            segmentStart = index + 1;
        }

        return true;
    }

    private static int[] BuildUtf8LineStarts(ReadOnlySpan<byte> utf8)
    {
        var starts = new List<int>(Math.Min(utf8.Length / 32 + 1, 4096)) { 0 };
        for (var index = 0; index < utf8.Length; index++)
        {
            if (utf8[index] == (byte)'\n')
                starts.Add(index + 1);
        }

        return starts.ToArray();
    }

    private static int FindLineIndex(int[] lineStarts, int byteOffset)
    {
        var index = Array.BinarySearch(lineStarts, byteOffset);
        return index >= 0 ? index : ~index - 1;
    }
}
