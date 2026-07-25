using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{

    private static HashSet<SymbolKindNameIdentity> BuildSymbolKindNameIdentities(IReadOnlyList<SymbolRecord> symbols)
    {
        var existing = new HashSet<SymbolKindNameIdentity>(symbols.Count);
        foreach (var symbol in symbols)
            existing.Add(new SymbolKindNameIdentity(symbol.Kind, symbol.Name));
        return existing;
    }

    private static IReadOnlyList<SymbolRecord> BuildPropertySymbolSnapshot(IReadOnlyList<SymbolRecord> symbols, int lineCount)
    {
        List<SymbolRecord>? properties = null;
        foreach (var symbol in symbols)
        {
            if (symbol.Kind == "property"
                && symbol.Line >= 1
                && symbol.Line <= lineCount)
            {
                (properties ??= []).Add(symbol);
            }
        }

        if (properties is null)
            return Array.Empty<SymbolRecord>();

        return properties;
    }

    private static void ExtractPhpPropertyHookSupplementalSymbols(
        long fileId,
        string[] lines,
        string[] structuralLines,
        List<SymbolRecord> symbols)
    {
        var properties = BuildPropertySymbolSnapshot(symbols, lines.Length);
        if (properties.Count == 0)
            return;

        var existing = BuildSymbolLineIdentities(symbols);

        foreach (var property in properties)
        {
            var lineIndex = property.Line - 1;
            var openBraceColumn = structuralLines[lineIndex].IndexOf('{', StringComparison.Ordinal);
            if (openBraceColumn < 0)
                continue;

            var closeBraceLine = FindBraceRangeEndLine(structuralLines, lineIndex, openBraceColumn);
            if (closeBraceLine <= lineIndex)
                continue;

            var sawAccessor = false;
            for (var accessorLine = lineIndex + 1; accessorLine <= closeBraceLine; accessorLine++)
            {
                var accessorMatch = PhpPropertyHookAccessorRegex.Match(structuralLines[accessorLine]);
                if (!accessorMatch.Success)
                    continue;

                var accessorName = accessorMatch.Groups["name"].Value;
                var symbolName = $"{property.Name}.{accessorName}";
                var identity = new SymbolLineIdentity(fileId, accessorLine + 1, "accessor", symbolName);
                if (!existing.Add(identity))
                    continue;

                var accessorBodyEndLine = accessorLine;
                var accessorNameEnd = accessorMatch.Groups["name"].Index + accessorMatch.Groups["name"].Length;
                var accessorOpenBraceColumn = structuralLines[accessorLine].IndexOf('{', accessorNameEnd);
                if (accessorOpenBraceColumn >= 0)
                {
                    var accessorCloseBraceLine = FindBraceRangeEndLine(structuralLines, accessorLine, accessorOpenBraceColumn);
                    if (accessorCloseBraceLine > accessorLine && accessorCloseBraceLine <= closeBraceLine)
                        accessorBodyEndLine = accessorCloseBraceLine;
                }

                sawAccessor = true;
                symbols.Add(new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "accessor",
                    Name = symbolName,
                    Line = accessorLine + 1,
                    StartLine = accessorLine + 1,
                    StartColumn = accessorMatch.Groups["name"].Index,
                    EndLine = accessorBodyEndLine + 1,
                    BodyStartLine = accessorLine + 1,
                    BodyEndLine = accessorBodyEndLine + 1,
                    Signature = lines[accessorLine].Trim(),
                    ContainerKind = "property",
                    ContainerName = property.Name,
                    ContainerQualifiedName = property.ContainerQualifiedName,
                });
            }

            if (sawAccessor)
            {
                property.SubKind = CombineSubKinds(property.SubKind, "php_property_hook");
                property.EndLine = Math.Max(property.EndLine, closeBraceLine + 1);
                property.BodyStartLine = lineIndex + 1;
                property.BodyEndLine = closeBraceLine + 1;
            }
        }
    }

    private static void ExtractSwiftPropertySupplementalSymbols(
        long fileId,
        string[] lines,
        string[] structuralLines,
        List<SymbolRecord> symbols)
    {
        var properties = BuildPropertySymbolSnapshot(symbols, lines.Length);
        if (properties.Count == 0)
            return;

        var existing = BuildSymbolLineIdentities(symbols);

        foreach (var property in properties)
        {
            var lineIndex = property.Line - 1;
            var propertyLine = lines[lineIndex];
            var propertyStructuralLine = structuralLines[lineIndex];
            if (propertyLine.IndexOf('@', StringComparison.Ordinal) < 0
                && propertyStructuralLine.IndexOf('{') < 0)
            {
                continue;
            }

            var declarationMatch = SwiftPropertyDeclarationRegex.Match(propertyLine);
            if (!declarationMatch.Success)
                continue;

            var attributes = declarationMatch.Groups["attributes"].Value;
            if (HasSwiftPropertyWrapperAttribute(attributes))
            {
                property.SubKind = CombineSubKinds(property.SubKind, "swift_wrapped_property");
                AddSwiftProjectedValueSymbol(fileId, lines, symbols, existing, property, propertyLine);
            }

            var openBraceLine = lineIndex;
            var openBraceColumn = structuralLines[lineIndex].IndexOf('{', declarationMatch.Index + declarationMatch.Length);
            if (openBraceColumn < 0)
                continue;

            var closeBraceLine = FindBraceRangeEndLine(structuralLines, openBraceLine, openBraceColumn);
            if (closeBraceLine < openBraceLine)
                continue;

            var sawAccessor = false;
            for (var accessorLine = openBraceLine; accessorLine <= closeBraceLine; accessorLine++)
            {
                var accessorStructuralLine = structuralLines[accessorLine];
                if (!MayContainSwiftAccessorDeclaration(accessorStructuralLine))
                    continue;

                foreach (Match accessorMatch in SwiftAccessorDeclarationRegex.Matches(accessorStructuralLine))
                {
                    if (!IsSwiftTopLevelAccessor(structuralLines, openBraceLine, openBraceColumn, accessorLine, accessorMatch.Index))
                        continue;

                    var accessorName = accessorMatch.Groups["name"].Value;
                    var symbolName = $"{property.Name}.{accessorName}";
                    var identity = new SymbolLineIdentity(fileId, accessorLine + 1, "accessor", symbolName);
                    if (!existing.Add(identity))
                        continue;

                    sawAccessor = true;
                    symbols.Add(new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "accessor",
                        Name = symbolName,
                        Line = accessorLine + 1,
                        StartLine = accessorLine + 1,
                        StartColumn = accessorMatch.Index,
                        EndLine = accessorLine + 1,
                        Signature = lines[accessorLine].Trim(),
                        ContainerKind = "property",
                        ContainerName = property.Name,
                        ContainerQualifiedName = property.ContainerQualifiedName,
                    });
                }
            }

            if (sawAccessor)
            {
                property.SubKind = CombineSubKinds(property.SubKind, "swift_computed_property");
                property.EndLine = Math.Max(property.EndLine, closeBraceLine + 1);
                property.BodyStartLine = openBraceLine + 1;
                property.BodyEndLine = closeBraceLine + 1;
            }
        }
    }

    private static bool MayContainSwiftAccessorDeclaration(string line)
        => line.IndexOf("get", StringComparison.Ordinal) >= 0
           || line.IndexOf("set", StringComparison.Ordinal) >= 0
           || line.IndexOf("willSet", StringComparison.Ordinal) >= 0
           || line.IndexOf("didSet", StringComparison.Ordinal) >= 0;

    private static void AddSwiftProjectedValueSymbol(
        long fileId,
        string[] lines,
        List<SymbolRecord> symbols,
        HashSet<SymbolLineIdentity> existing,
        SymbolRecord property,
        string propertyLine)
    {
        var projectedName = "$" + property.Name.Trim('`');
        var identity = new SymbolLineIdentity(fileId, property.Line, "property", projectedName);
        if (!existing.Add(identity))
            return;

        symbols.Add(new SymbolRecord
        {
            FileId = fileId,
            Kind = "property",
            SubKind = "swift_projected_value",
            Name = projectedName,
            Line = property.Line,
            StartLine = property.StartLine,
            StartColumn = property.StartColumn,
            EndLine = property.EndLine,
            BodyStartLine = property.BodyStartLine,
            BodyEndLine = property.BodyEndLine,
            Signature = propertyLine.Trim(),
            ContainerKind = property.ContainerKind,
            ContainerName = property.ContainerName,
            ContainerQualifiedName = property.ContainerQualifiedName,
            Visibility = property.Visibility,
            ReturnType = property.ReturnType,
        });
    }

    private static bool HasSwiftPropertyWrapperAttribute(string attributes)
    {
        if (attributes.IndexOf('@') < 0)
            return false;

        foreach (Match match in SwiftPropertyWrapperAttributeRegex.Matches(attributes))
        {
            var name = match.Groups["name"].Value;
            var shortNameStart = name.LastIndexOf('.') + 1;
            var shortName = shortNameStart > 0 ? name[shortNameStart..] : name;
            if (!SwiftNonWrapperPropertyAttributes.Contains(shortName))
                return true;
        }

        return false;
    }

    private static int FindBraceRangeEndLine(string[] structuralLines, int openBraceLine, int openBraceColumn)
    {
        var depth = 0;
        for (var lineIndex = openBraceLine; lineIndex < structuralLines.Length; lineIndex++)
        {
            var line = structuralLines[lineIndex];
            var column = lineIndex == openBraceLine ? openBraceColumn : 0;
            for (; column < line.Length; column++)
            {
                if (line[column] == '{')
                {
                    depth++;
                }
                else if (line[column] == '}')
                {
                    depth--;
                    if (depth == 0)
                        return lineIndex;
                }
            }
        }

        return -1;
    }

    private static bool IsSwiftTopLevelAccessor(
        string[] structuralLines,
        int openBraceLine,
        int openBraceColumn,
        int accessorLine,
        int accessorColumn)
    {
        var depth = 0;
        for (var lineIndex = openBraceLine; lineIndex <= accessorLine; lineIndex++)
        {
            var line = structuralLines[lineIndex];
            var startColumn = lineIndex == openBraceLine ? openBraceColumn : 0;
            var endColumn = lineIndex == accessorLine ? accessorColumn : line.Length;
            for (var column = startColumn; column < endColumn; column++)
            {
                if (line[column] == '{')
                    depth++;
                else if (line[column] == '}')
                    depth--;
            }
        }

        return depth == 1;
    }

    private static string CombineSubKinds(string? current, string addition)
    {
        if (string.IsNullOrWhiteSpace(current))
            return addition;
        return ContainsSubKind(current, addition)
            ? current
            : current + "|" + addition;
    }

    private static bool ContainsSubKind(string current, string addition)
    {
        var remaining = current.AsSpan();
        while (!remaining.IsEmpty)
        {
            var separatorIndex = remaining.IndexOf('|');
            var candidate = separatorIndex < 0 ? remaining : remaining[..separatorIndex];
            if (!candidate.IsEmpty && candidate.Equals(addition.AsSpan(), StringComparison.Ordinal))
                return true;
            if (separatorIndex < 0)
                break;
            remaining = remaining[(separatorIndex + 1)..];
        }

        return false;
    }

}
