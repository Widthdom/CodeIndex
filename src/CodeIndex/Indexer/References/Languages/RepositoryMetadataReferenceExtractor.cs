using System.Text;
using System.Xml;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static class RepositoryMetadataReferenceExtractor
{
    private const int MaxMetadataArrayContinuationLines = 256;
    private readonly record struct QuotedMetadataValue(string RawValue, char Quote);

    internal static List<ReferenceRecord> Extract(
        long fileId,
        string language,
        string[] lines,
        IReadOnlyList<SymbolRecord> symbols,
        int? maxReferenceCount)
    {
        var references = ReferenceExtractor.CreateReferenceList(maxReferenceCount, Math.Min(lines.Length, 64));
        var seen = new ReferenceDedupeSet();
        var containers = BuildLineContainerMap(
            symbols,
            preferredKind: language is "gitattributes" or "config" ? "rule" : null);
        var pendingArrayDepth = 0;
        var pendingArrayLineCount = 0;
        SymbolRecord? pendingArrayContainer = null;

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var trimmed = line.AsSpan().Trim();
            if (trimmed.IsEmpty)
                continue;

            var lineNumber = lineIndex + 1;
            containers.TryGetValue(lineNumber, out var container);
            if (pendingArrayDepth > 0)
            {
                AddAssignmentPathReferences(
                    references,
                    seen,
                    fileId,
                    line,
                    line,
                    lineNumber,
                    pendingArrayContainer,
                    language);
                UpdateMetadataArrayDepth(line, ref pendingArrayDepth);
                pendingArrayLineCount++;
                if (pendingArrayDepth <= 0
                    || pendingArrayLineCount >= MaxMetadataArrayContinuationLines)
                {
                    pendingArrayDepth = 0;
                    pendingArrayLineCount = 0;
                    pendingArrayContainer = null;
                }

                if (ReferenceExtractor.ReferenceLimitReached(references))
                    break;
                continue;
            }

            if (language is "gitignore" or "dockerignore")
            {
                if (!TryNormalizeIgnorePattern(trimmed, out var pattern, out var sourceIndex))
                    continue;

                var trimmedOffset = line.IndexOf(trimmed.ToString(), StringComparison.Ordinal);
                AddPathReference(
                    references,
                    seen,
                    fileId,
                    pattern,
                    Math.Max(0, trimmedOffset) + sourceIndex,
                    line,
                    lineNumber,
                    container,
                    language);
            }
            else if (language == "gitattributes")
            {
                if (!SymbolExtractor.TryGetGitAttributesTokens(
                    trimmed,
                    out var rawPattern,
                    out _)
                    || rawPattern.StartsWith("[attr]", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!TryNormalizeRepositoryPath(
                    rawPattern,
                    allowBarePattern: true,
                    allowRootAnchoredPattern: true,
                    allowWhitespace: true,
                    out var pattern))
                    continue;

                var sourceIndex = line.IndexOf(rawPattern, StringComparison.Ordinal);
                AddPathReference(references, seen, fileId, pattern, Math.Max(0, sourceIndex), line, lineNumber, container, language);
            }
            else if (language == "editorconfig")
            {
                if (!TryGetEditorConfigSection(trimmed, out var pattern))
                    continue;

                var sourceIndex = line.IndexOf(pattern, StringComparison.Ordinal);
                AddPathReference(references, seen, fileId, pattern, Math.Max(0, sourceIndex), line, lineNumber, container, language);
            }
            else if (language is "toml" or "config"
                     && SymbolExtractor.TryGetMetadataAssignment(line, allowColon: false, out _, out var value))
            {
                AddAssignmentPathReferences(
                    references,
                    seen,
                    fileId,
                    value,
                    line,
                    lineNumber,
                    container,
                    language);
                if (TryGetOpenMetadataArrayDepth(value, out pendingArrayDepth))
                {
                    pendingArrayLineCount = 0;
                    pendingArrayContainer = container;
                }
            }

            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;
        }

        return references;
    }

    internal static List<ReferenceRecord> ExtractApplicationManifest(
        long fileId,
        string content,
        string[] lines,
        int? maxReferenceCount)
    {
        var references = ReferenceExtractor.CreateReferenceList(maxReferenceCount, Math.Min(lines.Length, 64));
        var seen = new ReferenceDedupeSet();
        var dependencyAncestorDepth = 0;
        var elementCount = 0;
        SymbolRecord? manifestContainer = null;

        try
        {
            using var reader = XmlReader.Create(
                new StringReader(content),
                SymbolExtractor.CreateExtractionXmlReaderSettings(DtdProcessing.Ignore));
            while (reader.Read() && !ReferenceExtractor.ReferenceLimitReached(references))
            {
                if (reader.NodeType == XmlNodeType.EndElement)
                {
                    if (IsManifestDependencyElement(reader.LocalName))
                        dependencyAncestorDepth--;
                    continue;
                }

                if (reader.NodeType != XmlNodeType.Element)
                    continue;

                elementCount++;
                if (elementCount > SymbolExtractor.XmlExtractionMaxElements
                    || reader.Depth + 1 > SymbolExtractor.XmlExtractionMaxDepth)
                {
                    break;
                }

                var lineNumber = reader is IXmlLineInfo lineInfo && lineInfo.HasLineInfo()
                    ? lineInfo.LineNumber
                    : 1;
                var context = GetLine(lines, lineNumber);
                var elementName = reader.LocalName;

                var isDependencyIdentity = elementName.Equals("assemblyIdentity", StringComparison.OrdinalIgnoreCase)
                    && dependencyAncestorDepth > 0;
                if (isDependencyIdentity)
                {
                    AddManifestDependency(
                        references,
                        seen,
                        fileId,
                        reader.GetAttribute("name"),
                        context,
                        lineNumber,
                        manifestContainer);
                }
                else if (elementName.Equals("assemblyIdentity", StringComparison.OrdinalIgnoreCase))
                {
                    var assemblyName = reader.GetAttribute("name");
                    if (!string.IsNullOrWhiteSpace(assemblyName))
                    {
                        manifestContainer = new SymbolRecord
                        {
                            Kind = "assembly",
                            Name = assemblyName,
                        };
                    }
                }

                if (elementName.Equals("file", StringComparison.OrdinalIgnoreCase))
                {
                    AddManifestPath(
                        references,
                        seen,
                        fileId,
                        reader.GetAttribute("name"),
                        context,
                        lineNumber,
                        manifestContainer);
                }
                else if (elementName.Equals("codeBase", StringComparison.OrdinalIgnoreCase))
                {
                    AddManifestPath(
                        references,
                        seen,
                        fileId,
                        reader.GetAttribute("href"),
                        context,
                        lineNumber,
                        manifestContainer);
                }
                else if (elementName.Equals("probing", StringComparison.OrdinalIgnoreCase))
                {
                    var privatePath = reader.GetAttribute("privatePath");
                    if (!string.IsNullOrWhiteSpace(privatePath))
                    {
                        foreach (var path in new DelimitedSpanEnumerable(
                                     privatePath.AsSpan(),
                                     ';',
                                     trimEntries: true,
                                     removeEmptyEntries: true))
                        {
                            AddManifestPath(
                                references,
                                seen,
                                fileId,
                                path.ToString(),
                                context,
                                lineNumber,
                                manifestContainer);
                        }
                    }
                }

                if (!reader.IsEmptyElement
                    && IsManifestDependencyElement(elementName))
                {
                    dependencyAncestorDepth++;
                }
            }
        }
        catch (XmlException)
        {
            return references;
        }

        return references;
    }

    private static bool IsManifestDependencyElement(string elementName)
        => elementName.Equals("dependentAssembly", StringComparison.OrdinalIgnoreCase)
            || elementName.Equals("dependency", StringComparison.OrdinalIgnoreCase);

    private static void AddAssignmentPathReferences(
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string value,
        string context,
        int lineNumber,
        SymbolRecord? container,
        string language)
    {
        var valueSpan = value.AsSpan();
        var commentIndex = FindUnquotedComment(valueSpan);
        if (commentIndex >= 0)
            valueSpan = valueSpan[..commentIndex];
        var referenceValue = valueSpan.ToString();

        var candidates = EnumerateQuotedValues(referenceValue);
        foreach (var candidate in candidates)
        {
            if (!TryDecodeQuotedPathCandidate(candidate, out var decodedCandidate))
                continue;
            if (!TryNormalizeRepositoryPath(
                decodedCandidate,
                allowBarePattern: false,
                allowRootAnchoredPattern: false,
                allowWhitespace: false,
                out var normalized))
                continue;

            var sourceIndex = context.IndexOf(candidate.RawValue, StringComparison.Ordinal);
            AddPathReference(
                references,
                seen,
                fileId,
                normalized,
                Math.Max(0, sourceIndex),
                context,
                lineNumber,
                container,
                language);
            if (ReferenceExtractor.ReferenceLimitReached(references))
                return;
        }
    }

    private static bool TryGetOpenMetadataArrayDepth(string value, out int depth)
    {
        depth = 0;
        var valueSpan = value.AsSpan().TrimStart();
        if (valueSpan.IsEmpty || valueSpan[0] != '[')
            return false;

        UpdateMetadataArrayDepth(value, ref depth);
        return depth > 0;
    }

    private static void UpdateMetadataArrayDepth(string value, ref int depth)
    {
        var quote = '\0';
        var escaped = false;
        foreach (var character in value)
        {
            if (quote != '\0')
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (character == '\\' && quote == '"')
                    escaped = true;
                else if (character == quote)
                    quote = '\0';
                continue;
            }

            if (character is '"' or '\'')
                quote = character;
            else if (character == '#')
                break;
            else if (character == '[')
                depth++;
            else if (character == ']')
                depth--;
        }
    }

    private static IEnumerable<QuotedMetadataValue> EnumerateQuotedValues(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var quote = value[index];
            if (quote is not ('"' or '\''))
                continue;

            var start = index + 1;
            var escaped = false;
            for (index = start; index < value.Length; index++)
            {
                var character = value[index];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (character == '\\' && quote == '"')
                {
                    escaped = true;
                    continue;
                }

                if (character != quote)
                    continue;

                yield return new QuotedMetadataValue(value[start..index], quote);
                break;
            }
        }
    }

    private static bool TryDecodeQuotedPathCandidate(
        QuotedMetadataValue candidate,
        out string decoded)
    {
        decoded = candidate.RawValue;
        if (candidate.Quote == '\'' || !candidate.RawValue.Contains('\\'))
            return true;

        var builder = new StringBuilder(candidate.RawValue.Length);
        for (var index = 0; index < candidate.RawValue.Length; index++)
        {
            var character = candidate.RawValue[index];
            if (character != '\\')
            {
                builder.Append(character);
                continue;
            }

            if (index + 1 >= candidate.RawValue.Length
                || candidate.RawValue[index + 1] != '\\')
            {
                decoded = string.Empty;
                return false;
            }

            builder.Append('\\');
            index++;
        }

        decoded = builder.ToString();
        return true;
    }

    private static int FindUnquotedComment(ReadOnlySpan<char> value)
    {
        var quote = '\0';
        var escaped = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (quote != '\0')
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (character == '\\' && quote == '"')
                    escaped = true;
                else if (character == quote)
                    quote = '\0';
                continue;
            }

            if (character is '"' or '\'')
                quote = character;
            else if (character == '#')
                return index;
        }

        return -1;
    }

    private static bool TryNormalizeIgnorePattern(
        ReadOnlySpan<char> trimmed,
        out string pattern,
        out int sourceIndex)
    {
        pattern = string.Empty;
        sourceIndex = 0;
        if (trimmed.IsEmpty || trimmed[0] == '#')
            return false;

        if (trimmed.StartsWith(@"\#") || trimmed.StartsWith(@"\!"))
        {
            trimmed = trimmed[1..];
            sourceIndex++;
        }
        else if (trimmed[0] == '!')
        {
            trimmed = trimmed[1..].TrimStart();
            sourceIndex++;
        }

        return TryNormalizeRepositoryPath(
            trimmed.TrimEnd().ToString(),
            allowBarePattern: true,
            allowRootAnchoredPattern: true,
            allowWhitespace: false,
            out pattern);
    }

    private static bool TryGetEditorConfigSection(ReadOnlySpan<char> trimmed, out string pattern)
    {
        pattern = string.Empty;
        if (!SymbolExtractor.TryGetBracketSection(
            trimmed,
            allowDoubleBrackets: false,
            out var candidate,
            out _))
        {
            return false;
        }
        return TryNormalizeRepositoryPath(
            candidate,
            allowBarePattern: true,
            allowRootAnchoredPattern: true,
            allowWhitespace: false,
            out pattern);
    }

    private static bool TryNormalizeRepositoryPath(
        string candidate,
        bool allowBarePattern,
        bool allowRootAnchoredPattern,
        bool allowWhitespace,
        out string normalized)
    {
        normalized = string.Empty;
        var value = candidate.Trim();
        if (value.Length == 0
            || value.Length > SymbolExtractor.StructuredDataMaxPathLength
            || value.Contains("://", StringComparison.Ordinal)
            || !allowWhitespace && SpanCharacterSearch.ContainsWhitespace(value)
            || SpanCharacterSearch.ContainsControl(value))
        {
            return false;
        }

        value = value.Replace('\\', '/');
        while (value.StartsWith("./", StringComparison.Ordinal))
            value = value[2..];
        if (value.StartsWith("/", StringComparison.Ordinal))
        {
            if (!allowRootAnchoredPattern)
                return false;
        }
        if (allowRootAnchoredPattern)
            value = value.Trim('/');

        if (value.Length == 0
            || value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':'
            || !allowRootAnchoredPattern && value.Contains(':')
            || value.StartsWith("$", StringComparison.Ordinal)
            || value.StartsWith("%", StringComparison.Ordinal)
            || value.StartsWith("~", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var segment in new DelimitedSpanEnumerable(value.AsSpan(), '/'))
        {
            if (segment.IsEmpty
                || segment.SequenceEqual(".")
                || segment.SequenceEqual(".."))
                return false;
        }

        if (!allowBarePattern
            && !value.Contains('/', StringComparison.Ordinal)
            && value.AsSpan().IndexOfAny("*?[{") < 0
            && !HasFileLikeExtension(value))
        {
            return false;
        }

        normalized = value;
        return true;
    }

    private static bool HasFileLikeExtension(string value)
    {
        var extension = Path.GetExtension(value);
        if (extension.Length is < 2 or > 17)
            return false;

        return extension.AsSpan(1).ContainsAnyInRange('A', 'Z')
            || extension.AsSpan(1).ContainsAnyInRange('a', 'z');
    }

    private static void AddManifestDependency(
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string? name,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Length > SymbolExtractor.StructuredDataMaxPathLength
            || name.Contains("://", StringComparison.Ordinal)
            || name.Contains('/')
            || name.Contains('\\')
            || SpanCharacterSearch.ContainsControl(name))
            return;

        var sourceIndex = context.IndexOf(name, StringComparison.Ordinal);
        ReferenceExtractor.AddReference(
            references,
            seen,
            fileId,
            name,
            Math.Max(0, sourceIndex),
            "dependency",
            context,
            lineNumber,
            container,
            "app_manifest");
    }

    private static void AddManifestPath(
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string? path,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !TryNormalizeRepositoryPath(
                path,
                allowBarePattern: true,
                allowRootAnchoredPattern: false,
                allowWhitespace: false,
                out var normalized))
        {
            return;
        }

        var sourceIndex = context.IndexOf(path, StringComparison.Ordinal);
        AddPathReference(
            references,
            seen,
            fileId,
            normalized,
            Math.Max(0, sourceIndex),
            context,
            lineNumber,
            container,
            "app_manifest");
    }

    private static void AddPathReference(
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string path,
        int sourceIndex,
        string context,
        int lineNumber,
        SymbolRecord? container,
        string language)
    {
        ReferenceExtractor.AddReference(
            references,
            seen,
            fileId,
            path,
            sourceIndex,
            "project_reference",
            context,
            lineNumber,
            container,
            language);
    }

    private static Dictionary<int, SymbolRecord> BuildLineContainerMap(
        IReadOnlyList<SymbolRecord> symbols,
        string? preferredKind)
    {
        var containers = new Dictionary<int, SymbolRecord>(Math.Min(symbols.Count, 4096));
        for (var index = 0; index < symbols.Count; index++)
        {
            var symbol = symbols[index];
            if (symbol.Line > 0
                && (preferredKind == null || symbol.Kind == preferredKind))
            {
                containers[symbol.Line] = symbol;
            }
        }

        return containers;
    }

    private static string GetLine(string[] lines, int lineNumber) =>
        lineNumber >= 1 && lineNumber <= lines.Length ? lines[lineNumber - 1] : string.Empty;
}
