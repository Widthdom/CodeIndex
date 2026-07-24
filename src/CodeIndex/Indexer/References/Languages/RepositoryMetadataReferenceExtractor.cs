using System.Xml;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static class RepositoryMetadataReferenceExtractor
{
    internal static List<ReferenceRecord> Extract(
        long fileId,
        string language,
        string[] lines,
        IReadOnlyList<SymbolRecord> symbols,
        int? maxReferenceCount)
    {
        var references = ReferenceExtractor.CreateReferenceList(maxReferenceCount, Math.Min(lines.Length, 64));
        var seen = new ReferenceDedupeSet();

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var trimmed = line.AsSpan().Trim();
            if (trimmed.IsEmpty)
                continue;

            var lineNumber = lineIndex + 1;
            var container = FindLineContainer(
                symbols,
                lineNumber,
                preferredKind: language == "gitattributes" ? "rule" : null);
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
                if (trimmed[0] == '#')
                    continue;

                var patternLength = 0;
                while (patternLength < trimmed.Length && !char.IsWhiteSpace(trimmed[patternLength]))
                    patternLength++;
                if (patternLength == 0 || patternLength == trimmed.Length)
                    continue;

                var rawPattern = trimmed[..patternLength].ToString();
                if (!TryNormalizeRepositoryPath(
                    rawPattern,
                    allowBarePattern: true,
                    allowRootAnchoredPattern: true,
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
        var ancestors = new Stack<string>();
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
                    if (ancestors.Count > 0)
                        ancestors.Pop();
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
                    && ancestors.Any(ancestor =>
                        ancestor.Equals("dependentAssembly", StringComparison.OrdinalIgnoreCase)
                        || ancestor.Equals("dependency", StringComparison.OrdinalIgnoreCase));
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
                        foreach (var path in privatePath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            AddManifestPath(references, seen, fileId, path, context, lineNumber, manifestContainer);
                    }
                }

                if (!reader.IsEmptyElement)
                    ancestors.Push(elementName);
            }
        }
        catch (XmlException)
        {
            return references;
        }

        return references;
    }

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
        var foundQuotedValue = false;
        foreach (var candidate in candidates)
        {
            foundQuotedValue = true;
            if (!TryNormalizeRepositoryPath(
                candidate,
                allowBarePattern: false,
                allowRootAnchoredPattern: false,
                out var normalized))
                continue;

            var sourceIndex = context.IndexOf(candidate, StringComparison.Ordinal);
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

        if (foundQuotedValue)
            return;

        var candidateValue = valueSpan.Trim().ToString();
        if (!TryNormalizeRepositoryPath(
            candidateValue,
            allowBarePattern: false,
            allowRootAnchoredPattern: false,
            out var normalizedValue))
            return;

        var index = context.IndexOf(candidateValue, StringComparison.Ordinal);
        AddPathReference(
            references,
            seen,
            fileId,
            normalizedValue,
            Math.Max(0, index),
            context,
            lineNumber,
            container,
            language);
    }

    private static IEnumerable<string> EnumerateQuotedValues(string value)
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

                yield return value[start..index];
                break;
            }
        }
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
            out pattern);
    }

    private static bool TryGetEditorConfigSection(ReadOnlySpan<char> trimmed, out string pattern)
    {
        pattern = string.Empty;
        if (trimmed.Length < 3 || trimmed[0] != '[')
            return false;

        var closingIndex = trimmed[1..].IndexOf(']');
        if (closingIndex < 0)
            return false;

        var candidate = trimmed.Slice(1, closingIndex).Trim().ToString();
        return TryNormalizeRepositoryPath(
            candidate,
            allowBarePattern: true,
            allowRootAnchoredPattern: true,
            out pattern);
    }

    private static bool TryNormalizeRepositoryPath(
        string candidate,
        bool allowBarePattern,
        bool allowRootAnchoredPattern,
        out string normalized)
    {
        normalized = string.Empty;
        var value = candidate.Trim();
        if (value.Length == 0
            || value.Length > SymbolExtractor.StructuredDataMaxPathLength
            || value.Contains("://", StringComparison.Ordinal)
            || value.Any(char.IsWhiteSpace))
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

        foreach (var segment in value.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
                return false;
        }

        if (!allowBarePattern
            && !value.Contains('/', StringComparison.Ordinal)
            && !value.Any(character => character is '*' or '?' or '[' or '{')
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
            || name.Any(char.IsControl))
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

    private static SymbolRecord? FindLineContainer(
        IReadOnlyList<SymbolRecord> symbols,
        int lineNumber,
        string? preferredKind)
    {
        for (var index = symbols.Count - 1; index >= 0; index--)
        {
            if (symbols[index].Line == lineNumber
                && (preferredKind == null || symbols[index].Kind == preferredKind))
                return symbols[index];
        }

        return null;
    }

    private static string GetLine(string[] lines, int lineNumber) =>
        lineNumber >= 1 && lineNumber <= lines.Length ? lines[lineNumber - 1] : string.Empty;
}
