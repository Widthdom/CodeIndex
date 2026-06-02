using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using System.Runtime.CompilerServices;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private readonly record struct PythonImportSymbolEntry(string Name, int StartColumn);
    private readonly record struct PythonExportSymbolEntry(string Name, int LineIndex, int StartColumn);
    private static readonly Regex PythonDirectImportRegex = new(@"^import\s+(?<imports>.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PythonFromImportRegex = new(@"^from\s+(?<module>(?:\.+[\w.]*|[\w.]+))\s+import\s+(?<imports>.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PythonDynamicImportLiteralRegex = new(@"\b(?:importlib\.import_module|importlib\.util\.find_spec|__import__)\s*\(\s*(?<quote>['""])(?<module>[^'""]+)\k<quote>", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PythonAllAssignmentRegex = new(@"^\s*__all__\s*(?:\+?=)\s*(?<values>.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PythonAllAppendRegex = new(@"^\s*__all__\.append\(\s*(?<quote>['""])(?<name>[^'""]+)\k<quote>\s*\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PythonAllExtendRegex = new(@"^\s*__all__\.extend\(\s*(?<values>.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PythonClassAnnotatedAttributeRegex = new(@"^\s*(?<name>[_\p{L}]\w*)\s*:\s*[^=].*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PythonClassAssignedAttributeRegex = new(@"^\s*(?<name>[_\p{L}]\w*)\s*=(?!=).*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PythonDataclassFieldAttributeRegex = new(@"^\s*(?<name>[_\p{L}]\w*)\s*(?::\s*[^=]+)?=\s*(?:(?:dataclasses\.)?field)\s*\(", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PythonDataclassFieldMetadataRegex = new(@"\bmetadata\s*=\s*(?<values>\{)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PythonClassSlotsAssignmentRegex = new(@"^\s*__slots__\s*(?:\+?=)\s*(?<values>.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PythonClassMatchArgsAssignmentRegex = new(@"^\s*__match_args__\s*(?:\+?=)\s*(?<values>.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PythonClassAnnotationsAssignmentRegex = new(@"^\s*__annotations__\s*(?:\+?=)\s*(?<values>.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PythonWalrusAssignmentRegex = new(@"(?<![:<>=!])\b(?<name>[_\p{L}]\w*)\s*:=", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string? GetPythonPropertyAccessorSubKind(string[] lines, int defLineIndex)
    {
        for (var i = defLineIndex - 1; i >= 0; i--)
        {
            var trimmed = lines[i].Trim();
            if (!trimmed.StartsWith('@'))
                return null;

            var decorator = NormalizePythonDecoratorName(trimmed);
            if (decorator.EndsWith(".getter", StringComparison.Ordinal))
                return "getter";
            if (decorator.EndsWith(".setter", StringComparison.Ordinal))
                return "setter";
            if (decorator.EndsWith(".deleter", StringComparison.Ordinal))
                return "deleter";
        }

        return null;
    }

    private static bool HasPythonPropertyDecorator(string[] lines, int defLineIndex)
    {
        for (var i = defLineIndex - 1; i >= 0; i--)
        {
            var trimmed = lines[i].Trim();
            if (!trimmed.StartsWith('@'))
                return false;

            if (IsPythonPropertyDecorator(trimmed))
                return true;
        }

        return false;
    }

    private static bool IsPythonPropertyDecorator(string trimmedDecorator)
    {
        var decorator = NormalizePythonDecoratorName(trimmedDecorator);
        return decorator is "property" or "cached_property" or "abstractproperty"
            || decorator.EndsWith(".cached_property", StringComparison.Ordinal)
            || decorator.EndsWith(".abstractproperty", StringComparison.Ordinal)
            || decorator.EndsWith(".getter", StringComparison.Ordinal)
            || decorator.EndsWith(".setter", StringComparison.Ordinal)
            || decorator.EndsWith(".deleter", StringComparison.Ordinal);
    }

    private static string NormalizePythonDecoratorName(string trimmedDecorator)
    {
        var decorator = trimmedDecorator[1..];
        var commentIndex = decorator.IndexOf('#');
        if (commentIndex >= 0)
            decorator = decorator[..commentIndex];
        var parenIndex = decorator.IndexOf('(');
        if (parenIndex >= 0)
            decorator = decorator[..parenIndex];

        decorator = decorator.Trim();
        return decorator;
    }

    private static bool IsPythonClassHook(string name) =>
        name is "__init_subclass__" or "__class_getitem__" or "__set_name__" or "__class_subclasses__";

    private static (int EndLine, int? BodyStartLine, int? BodyEndLine) FindPythonIndentedBodyRange(string[] lines, int startLineIndex)
    {
        var declarationIndent = CountIndent(lines[startLineIndex]);
        int? firstBodyLine = null;
        var lastBodyLine = startLineIndex + 1;

        for (var i = startLineIndex + 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;

            var indent = CountIndent(lines[i]);
            if (indent <= declarationIndent)
                break;

            firstBodyLine ??= i + 1;
            lastBodyLine = i + 1;
        }

        return firstBodyLine.HasValue
            ? (lastBodyLine, firstBodyLine.Value, lastBodyLine)
            : (startLineIndex + 1, null, null);
    }

    private static string BuildPythonLogicalHeaderSignature(string[] lines, int startLineIndex, int startColumn)
    {
        var builder = new StringBuilder();
        var parenDepth = 0;
        var bracketDepth = 0;
        var inString = false;
        var quote = '\0';

        for (var i = startLineIndex; i < lines.Length; i++)
        {
            var line = lines[i];
            var column = i == startLineIndex ? startColumn : FindFirstNonWhitespaceColumn(line);
            var fragment = column < line.Length ? line[column..].Trim() : string.Empty;
            if (fragment.Length > 0)
            {
                if (builder.Length > 0)
                    builder.Append(' ');
                builder.Append(fragment.TrimEnd('\\').TrimEnd());
            }

            for (var j = column; j < line.Length; j++)
            {
                var ch = line[j];
                if (inString)
                {
                    if (ch == '\\')
                    {
                        j++;
                        continue;
                    }
                    if (ch == quote)
                        inString = false;
                    continue;
                }

                if (ch is '\'' or '"')
                {
                    inString = true;
                    quote = ch;
                    continue;
                }
                if (ch == '#')
                    break;
                if (ch == '(')
                    parenDepth++;
                else if (ch == ')' && parenDepth > 0)
                    parenDepth--;
                else if (ch == '[')
                    bracketDepth++;
                else if (ch == ']' && bracketDepth > 0)
                    bracketDepth--;
                else if (ch == ':' && parenDepth == 0 && bracketDepth == 0)
                    return builder.ToString();
            }

            if (parenDepth == 0 && bracketDepth == 0 && !line.TrimEnd().EndsWith('\\'))
                break;
        }

        return builder.ToString();
    }

    private static List<PythonImportSymbolEntry>? TryExpandPythonImportSymbols(
        string[] lines,
        int lineIndex,
        int absoluteStartColumn,
        string? pythonModulePrefix)
    {
        var line = lines[lineIndex];
        if (absoluteStartColumn < 0 || absoluteStartColumn >= line.Length)
            return null;

        var statement = line[absoluteStartColumn..].Trim();
        if (statement.Length == 0)
            return null;

        var commentIndex = statement.IndexOf('#');
        if (commentIndex >= 0)
            statement = statement[..commentIndex].TrimEnd();

        if (statement.Length == 0)
            return null;

        var entries = new List<PythonImportSymbolEntry>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in PythonDynamicImportLiteralRegex.Matches(statement))
        {
            AddPythonImportEntry(
                line,
                absoluteStartColumn,
                match.Groups["module"].Value,
                entries,
                seenNames,
                ref absoluteStartColumn);
        }

        if (entries.Count > 0)
            return entries;

        var directImportMatch = PythonDirectImportRegex.Match(statement);
        if (directImportMatch.Success)
        {
            var directImportSpecs = directImportMatch.Groups["imports"].Value;
            AddPythonImportSpecEntries(
                line,
                absoluteStartColumn,
                modulePart: null,
                directImportSpecs,
                entries,
                seenNames,
                treatAsFromImport: false,
                pythonModulePrefix);
            return entries.Count > 0 ? entries : null;
        }

        var fromImportMatch = PythonFromImportRegex.Match(statement);
        if (!fromImportMatch.Success)
            return null;

        var modulePart = fromImportMatch.Groups["module"].Value;
        var fromImportSpecs = fromImportMatch.Groups["imports"].Value;
        AddPythonImportModuleEntry(line, absoluteStartColumn, modulePart, entries, seenNames, pythonModulePrefix);
        if (TryExpandPythonMultilineParenthesizedImportBlock(
                lines,
                lineIndex,
                absoluteStartColumn + fromImportMatch.Groups["imports"].Index,
                modulePart,
                fromImportSpecs,
                entries,
                seenNames,
                pythonModulePrefix))
        {
            return entries.Count > 0 ? entries : null;
        }

        AddPythonImportSpecEntries(
            line,
            absoluteStartColumn + fromImportMatch.Groups["imports"].Index,
            modulePart,
            fromImportSpecs,
            entries,
            seenNames,
            treatAsFromImport: true,
            pythonModulePrefix);
        return entries.Count > 0 ? entries : null;
    }

    private static void ExtractPythonAllExportSymbols(
        long fileId,
        string[] lines,
        List<SymbolRecord> symbols,
        string? pythonModulePrefix)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var exports = TryExpandPythonAllExportSymbols(lines, i);
            if (exports == null)
                continue;

            foreach (var export in exports)
            {
                AddSymbolRecord(
                    symbols,
                    cssSeenSymbols: null,
                    export.LineIndex + 1,
                    new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "import",
                        Name = export.Name,
                        Line = export.LineIndex + 1,
                        StartLine = export.LineIndex + 1,
                        StartColumn = export.StartColumn,
                        EndLine = export.LineIndex + 1,
                        Signature = lines[export.LineIndex].Trim(),
                    },
                    lines[export.LineIndex]);

                if (!string.IsNullOrEmpty(pythonModulePrefix)
                    && !string.Equals(export.Name, pythonModulePrefix, StringComparison.Ordinal)
                    && !export.Name.StartsWith(pythonModulePrefix + ".", StringComparison.Ordinal))
                {
                    AddSymbolRecord(
                        symbols,
                        cssSeenSymbols: null,
                        export.LineIndex + 1,
                        new SymbolRecord
                        {
                            FileId = fileId,
                            Kind = "import",
                            Name = $"{pythonModulePrefix}.{export.Name}",
                            Line = export.LineIndex + 1,
                            StartLine = export.LineIndex + 1,
                            StartColumn = export.StartColumn,
                            EndLine = export.LineIndex + 1,
                            Signature = lines[export.LineIndex].Trim(),
                        },
                        lines[export.LineIndex]);
                }
            }
        }
    }

    private static void ExtractPythonClassAttributeSymbols(
        long fileId,
        string[] lines,
        List<SymbolRecord> symbols)
    {
        var classSymbols = symbols
            .Where(static symbol => symbol.Kind == "class" && symbol.BodyStartLine.HasValue)
            .ToList();

        foreach (var classSymbol in classSymbols)
        {
            var classLineIndex = Math.Max(0, classSymbol.Line - 1);
            var classIndent = CountIndent(lines[classLineIndex]);
            int? memberIndent = null;
            var bodyStartIndex = Math.Max(classSymbol.BodyStartLine!.Value - 1, classLineIndex + 1);
            var bodyEndIndex = Math.Min(classSymbol.EndLine, lines.Length);

            for (var i = bodyStartIndex; i < bodyEndIndex; i++)
            {
                var line = lines[i];
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('@'))
                    continue;

                var indent = CountIndent(line);
                if (indent <= classIndent)
                    break;
                memberIndent ??= indent;
                if (indent != memberIndent.Value)
                    continue;

                if (trimmed.StartsWith("def ", StringComparison.Ordinal)
                    || trimmed.StartsWith("async def ", StringComparison.Ordinal)
                    || trimmed.StartsWith("class ", StringComparison.Ordinal)
                    || trimmed.StartsWith("type ", StringComparison.Ordinal))
                {
                    continue;
                }

                var slotsMatch = PythonClassSlotsAssignmentRegex.Match(line);
                if (!slotsMatch.Success)
                    slotsMatch = PythonClassMatchArgsAssignmentRegex.Match(line);
                if (slotsMatch.Success)
                {
                    var slots = TryExpandPythonAllExportSymbolsFromValues(lines, i, slotsMatch.Groups["values"].Index);
                    if (slots != null)
                    {
                        foreach (var slot in slots)
                        {
                            AddPythonClassPropertySymbol(
                                fileId,
                                lines,
                                symbols,
                                slot.Name,
                                slot.LineIndex,
                                slot.StartColumn);
                        }
                    }

                    continue;
                }

                var annotationsMatch = PythonClassAnnotationsAssignmentRegex.Match(line);
                if (annotationsMatch.Success)
                {
                    var annotationKeys = TryExpandPythonStringDictionaryKeys(lines, i, annotationsMatch.Groups["values"].Index);
                    if (annotationKeys != null)
                    {
                        foreach (var key in annotationKeys)
                        {
                            AddPythonClassPropertySymbol(
                                fileId,
                                lines,
                                symbols,
                                key.Name,
                                key.LineIndex,
                                key.StartColumn);
                        }
                    }

                    continue;
                }

                var fieldMatch = PythonDataclassFieldAttributeRegex.Match(line);
                if (fieldMatch.Success)
                {
                    AddPythonClassPropertySymbol(
                        fileId,
                        lines,
                        symbols,
                        fieldMatch.Groups["name"].Value,
                        i,
                        fieldMatch.Groups["name"].Index,
                        subKind: "dataclass_field");

                    var metadataKeys = TryExpandPythonDataclassFieldMetadataKeys(lines, i, fieldMatch.Groups["name"].Index);
                    if (metadataKeys != null)
                    {
                        foreach (var key in metadataKeys)
                        {
                            AddPythonDataclassFieldMetadataSymbol(
                                fileId,
                                lines,
                                symbols,
                                key.Name,
                                key.LineIndex,
                                key.StartColumn);
                        }
                    }

                    continue;
                }

                var match = PythonClassAnnotatedAttributeRegex.Match(line);
                if (!match.Success)
                    match = PythonClassAssignedAttributeRegex.Match(line);
                if (!match.Success)
                    continue;

                AddPythonClassPropertySymbol(
                    fileId,
                    lines,
                    symbols,
                    match.Groups["name"].Value,
                    i,
                    match.Groups["name"].Index);
            }
        }
    }

    private static void AddPythonClassPropertySymbol(
        long fileId,
        string[] lines,
        List<SymbolRecord> symbols,
        string name,
        int lineIndex,
        int startColumn,
        string? subKind = null)
    {
        AddSymbolRecord(
            symbols,
            cssSeenSymbols: null,
            lineIndex + 1,
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                SubKind = subKind,
                Name = name,
                Line = lineIndex + 1,
                StartLine = lineIndex + 1,
                StartColumn = startColumn,
                EndLine = lineIndex + 1,
                Signature = lines[lineIndex].Trim(),
            },
            lines[lineIndex]);
    }

    private static void AddPythonDataclassFieldMetadataSymbol(
        long fileId,
        string[] lines,
        List<SymbolRecord> symbols,
        string name,
        int lineIndex,
        int startColumn)
    {
        AddSymbolRecord(
            symbols,
            cssSeenSymbols: null,
            lineIndex + 1,
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "reference",
                SubKind = "dataclass_field_metadata",
                Name = name,
                Line = lineIndex + 1,
                StartLine = lineIndex + 1,
                StartColumn = startColumn,
                EndLine = lineIndex + 1,
                Signature = lines[lineIndex].Trim(),
            },
            lines[lineIndex]);
    }

    private static void ExtractPythonWalrusSymbols(
        long fileId,
        string[] lines,
        List<SymbolRecord> symbols)
    {
        var seen = new HashSet<string>(symbols.Select(static symbol => $"{symbol.Line}:{symbol.Kind}:{symbol.Name}"), StringComparer.Ordinal);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            foreach (Match match in PythonWalrusAssignmentRegex.Matches(line))
            {
                var name = match.Groups["name"].Value;
                if (name is "if" or "while" or "for")
                    continue;

                var key = $"{i + 1}:property:{name}";
                if (!seen.Add(key))
                    continue;

                AddSymbolRecord(
                    symbols,
                    cssSeenSymbols: null,
                    i + 1,
                    new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "property",
                        SubKind = "walrus",
                        Name = name,
                        Line = i + 1,
                        StartLine = i + 1,
                        StartColumn = match.Groups["name"].Index,
                        EndLine = i + 1,
                        Signature = line.Trim(),
                    },
                    line);
            }
        }
    }

    private static List<PythonExportSymbolEntry>? TryExpandPythonStringDictionaryKeys(
        string[] lines,
        int lineIndex,
        int valuesStartColumn)
    {
        var entries = new List<PythonExportSymbolEntry>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var currentLineIndex = lineIndex;
        var currentColumn = valuesStartColumn;
        var depth = 0;
        var inString = false;
        var quoteChar = '\0';
        var stringStartColumn = -1;

        while (currentLineIndex < lines.Length)
        {
            var currentLine = lines[currentLineIndex];
            if (currentColumn >= currentLine.Length)
            {
                if (depth <= 0 && !inString)
                    break;

                currentLineIndex++;
                currentColumn = 0;
                continue;
            }

            var ch = currentLine[currentColumn];
            if (inString)
            {
                if (ch == '\\' && currentColumn + 1 < currentLine.Length)
                {
                    currentColumn += 2;
                    continue;
                }

                if (ch == quoteChar)
                {
                    var afterStringColumn = currentColumn + 1;
                    while (afterStringColumn < currentLine.Length && char.IsWhiteSpace(currentLine[afterStringColumn]))
                        afterStringColumn++;

                    if (afterStringColumn < currentLine.Length && currentLine[afterStringColumn] == ':')
                    {
                        var name = currentLine[stringStartColumn..currentColumn].Trim();
                        if (name.Length > 0 && seenNames.Add(name))
                            entries.Add(new PythonExportSymbolEntry(name, currentLineIndex, stringStartColumn));
                    }

                    inString = false;
                    quoteChar = '\0';
                    stringStartColumn = -1;
                    currentColumn++;
                    continue;
                }

                currentColumn++;
                continue;
            }

            if (ch == '#')
                break;

            if (ch == '\'' || ch == '"')
            {
                inString = true;
                quoteChar = ch;
                stringStartColumn = currentColumn + 1;
                currentColumn++;
                continue;
            }

            if (ch is '{' or '[' or '(')
            {
                depth++;
                currentColumn++;
                continue;
            }

            if (ch is '}' or ']' or ')')
            {
                if (depth > 0)
                    depth--;
                currentColumn++;
                if (depth <= 0)
                    break;
                continue;
            }

            currentColumn++;
        }

        return entries.Count > 0 ? entries : null;
    }

    private static List<PythonExportSymbolEntry>? TryExpandPythonDataclassFieldMetadataKeys(
        string[] lines,
        int fieldLineIndex,
        int fieldStartColumn)
    {
        var currentLineIndex = fieldLineIndex;
        var currentColumn = fieldStartColumn;
        var depth = 0;
        var sawFieldCall = false;
        var inString = false;
        var quoteChar = '\0';

        while (currentLineIndex < lines.Length)
        {
            var currentLine = lines[currentLineIndex];
            var metadataMatch = PythonDataclassFieldMetadataRegex.Match(currentLine);
            if (metadataMatch.Success)
                return TryExpandPythonStringDictionaryKeys(lines, currentLineIndex, metadataMatch.Groups["values"].Index);

            for (; currentColumn < currentLine.Length; currentColumn++)
            {
                var ch = currentLine[currentColumn];
                if (inString)
                {
                    if (ch == '\\')
                    {
                        currentColumn++;
                        continue;
                    }

                    if (ch == quoteChar)
                        inString = false;
                    continue;
                }

                if (ch == '#')
                    break;
                if (ch is '\'' or '"')
                {
                    inString = true;
                    quoteChar = ch;
                    continue;
                }

                if (ch == '(')
                {
                    depth++;
                    sawFieldCall = true;
                }
                else if (ch == ')' && depth > 0)
                {
                    depth--;
                    if (sawFieldCall && depth == 0)
                        return null;
                }
            }

            if (sawFieldCall && depth <= 0)
                return null;

            currentLineIndex++;
            currentColumn = 0;
        }

        return null;
    }

    private static List<PythonExportSymbolEntry>? TryExpandPythonAllExportSymbols(string[] lines, int lineIndex)
    {
        var line = lines[lineIndex];
        var appendMatch = PythonAllAppendRegex.Match(line);
        if (appendMatch.Success)
        {
            return
            [
                new PythonExportSymbolEntry(
                    appendMatch.Groups["name"].Value,
                    lineIndex,
                    appendMatch.Groups["name"].Index),
            ];
        }

        var extendMatch = PythonAllExtendRegex.Match(line);
        if (extendMatch.Success)
            return TryExpandPythonAllExportSymbolsFromCallValues(lines, lineIndex, extendMatch.Groups["values"].Index);

        var match = PythonAllAssignmentRegex.Match(line);
        if (!match.Success)
            return null;

        return TryExpandPythonAllExportSymbolsFromValues(lines, lineIndex, match.Groups["values"].Index);
    }

    private static List<PythonExportSymbolEntry>? TryExpandPythonAllExportSymbolsFromCallValues(
        string[] lines,
        int lineIndex,
        int valuesStartColumn)
    {
        if (valuesStartColumn < lines[lineIndex].Length)
            return TryExpandPythonAllExportSymbolsFromValues(lines, lineIndex, valuesStartColumn);

        return lineIndex + 1 < lines.Length
            ? TryExpandPythonAllExportSymbolsFromValues(lines, lineIndex + 1, 0)
            : null;
    }

    private static List<PythonExportSymbolEntry>? TryExpandPythonAllExportSymbolsFromValues(
        string[] lines,
        int lineIndex,
        int valuesStartColumn)
    {
        var line = lines[lineIndex];
        if (valuesStartColumn < 0 || valuesStartColumn >= line.Length)
            return null;

        var entries = new List<PythonExportSymbolEntry>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var currentLineIndex = lineIndex;
        var currentColumn = valuesStartColumn;
        var depth = 0;
        var inString = false;
        var quoteChar = '\0';
        var stringStartColumn = -1;

        while (currentLineIndex < lines.Length)
        {
            var currentLine = lines[currentLineIndex];
            if (currentColumn >= currentLine.Length)
            {
                if (depth <= 0 && !inString)
                    break;

                currentLineIndex++;
                currentColumn = 0;
                continue;
            }

            var ch = currentLine[currentColumn];
            if (inString)
            {
                if (ch == '\\' && currentColumn + 1 < currentLine.Length)
                {
                    currentColumn += 2;
                    continue;
                }

                if (ch == quoteChar)
                {
                    var name = currentLine[stringStartColumn..currentColumn].Trim();
                    if (name.Length > 0 && seenNames.Add(name))
                    {
                        entries.Add(new PythonExportSymbolEntry(name, currentLineIndex, stringStartColumn));
                    }

                    inString = false;
                    quoteChar = '\0';
                    stringStartColumn = -1;
                    currentColumn++;
                    continue;
                }

                currentColumn++;
                continue;
            }

            if (ch == '#')
                break;

            if (ch == '\'' || ch == '"')
            {
                inString = true;
                quoteChar = ch;
                stringStartColumn = currentColumn + 1;
                currentColumn++;
                continue;
            }

            if (ch is '[' or '(' or '{')
            {
                depth++;
                currentColumn++;
                continue;
            }

            if (ch is ']' or ')' or '}')
            {
                if (depth > 0)
                    depth--;
                currentColumn++;
                if (depth <= 0)
                    break;
                continue;
            }

            currentColumn++;
        }

        return entries.Count > 0 ? entries : null;
    }

    private static bool TryExpandPythonMultilineParenthesizedImportBlock(
        string[] lines,
        int startLineIndex,
        int importsStartColumn,
        string modulePart,
        string importSpecs,
        List<PythonImportSymbolEntry> entries,
        HashSet<string> seenNames,
        string? pythonModulePrefix)
    {
        if (!importSpecs.StartsWith('(') || importSpecs.EndsWith(')'))
            return false;

        var currentLineIndex = startLineIndex;
        var importLineIndent = FindFirstNonWhitespaceColumn(lines[startLineIndex]);
        var fragment = importSpecs[1..];

        while (currentLineIndex < lines.Length)
        {
            var currentLine = lines[currentLineIndex];
            var fragmentStartColumn = currentLineIndex == startLineIndex
                ? Math.Min(importsStartColumn + 1, currentLine.Length)
                : FindFirstNonWhitespaceColumn(currentLine);
            if (fragmentStartColumn >= currentLine.Length)
            {
                currentLineIndex++;
                fragment = string.Empty;
                if (currentLineIndex >= lines.Length)
                    return true;
                continue;
            }

            if (currentLineIndex != startLineIndex && fragmentStartColumn <= importLineIndent)
                return true;

            fragment = currentLineIndex == startLineIndex
                ? fragment
                : currentLine[fragmentStartColumn..];

            var commentIndex = fragment.IndexOf('#');
            if (commentIndex >= 0)
                fragment = fragment[..commentIndex];

            fragment = fragment.TrimEnd();
            if (fragment.Length > 0)
            {
                var closingParenIndex = fragment.IndexOf(')');
                if (closingParenIndex >= 0)
                {
                    fragment = fragment[..closingParenIndex].TrimEnd();
                    if (fragment.Length > 0)
                    {
                        AddPythonImportSpecEntries(
                            currentLine,
                            fragmentStartColumn,
                            modulePart,
                            fragment,
                            entries,
                            seenNames,
                            treatAsFromImport: true,
                            pythonModulePrefix);
                    }

                    return true;
                }

                AddPythonImportSpecEntries(
                    currentLine,
                    fragmentStartColumn,
                    modulePart,
                    fragment,
                    entries,
                    seenNames,
                    treatAsFromImport: true,
                    pythonModulePrefix);
            }

            if (currentLineIndex + 1 >= lines.Length)
                return true;

            currentLineIndex++;
        }

        return true;
    }

    private static void AddPythonImportSpecEntries(
        string line,
        int absoluteStartColumn,
        string? modulePart,
        string importedNames,
        List<PythonImportSymbolEntry> entries,
        HashSet<string> seenNames,
        bool treatAsFromImport,
        string? pythonModulePrefix)
    {
        importedNames = importedNames.Trim();
        if (importedNames.Length == 0)
            return;

        if (importedNames.StartsWith('(') && importedNames.EndsWith(')'))
            importedNames = importedNames[1..^1].Trim();

        var searchStartColumn = absoluteStartColumn;
        var normalizedModulePart = treatAsFromImport
            ? modulePart?.Trim().TrimStart('.').TrimEnd('.')
            : null;
        var relativeQualifiedModulePart = treatAsFromImport
            ? ResolvePythonRelativeFromImportModuleName(modulePart, pythonModulePrefix)
            : null;
        foreach (var rawSpec in importedNames.Split(','))
        {
            var spec = rawSpec.Trim();
            if (spec.Length == 0 || spec == "*")
                continue;

            var aliasIndex = spec.IndexOf(" as ", StringComparison.Ordinal);
            var importedName = aliasIndex >= 0
                ? spec[..aliasIndex].Trim()
                : spec;
            var localName = aliasIndex >= 0
                ? spec[(aliasIndex + " as ".Length)..].Trim()
                : importedName.Split('.')[0].Trim();

            if (importedName.Length > 0)
            {
                AddPythonImportEntry(line, absoluteStartColumn, importedName, entries, seenNames, ref searchStartColumn);
                if (importedName.Contains('.'))
                {
                    AddPythonImportDottedPrefixEntries(
                        line,
                        absoluteStartColumn,
                        importedName,
                        entries,
                        seenNames,
                        ref searchStartColumn);
                }

                if (!treatAsFromImport
                    && !string.IsNullOrEmpty(pythonModulePrefix)
                    && !importedName.Contains('.'))
                {
                    AddPythonImportEntry(
                        line,
                        absoluteStartColumn,
                        $"{pythonModulePrefix}.{importedName}",
                        entries,
                        seenNames,
                        ref searchStartColumn);
                }

                if (treatAsFromImport && !string.IsNullOrEmpty(normalizedModulePart))
                {
                    AddPythonImportEntry(
                        line,
                        absoluteStartColumn,
                        $"{normalizedModulePart}.{importedName}",
                        entries,
                        seenNames,
                        ref searchStartColumn);
                }

                if (!string.IsNullOrEmpty(relativeQualifiedModulePart))
                {
                    AddPythonImportEntry(
                        line,
                        absoluteStartColumn,
                        $"{relativeQualifiedModulePart}.{importedName}",
                        entries,
                        seenNames,
                        ref searchStartColumn);
                }
            }

            if (localName.Length > 0
                && (!string.Equals(localName, importedName, StringComparison.Ordinal) || treatAsFromImport))
            {
                AddPythonImportEntry(line, absoluteStartColumn, localName, entries, seenNames, ref searchStartColumn);
            }

            if (string.IsNullOrEmpty(pythonModulePrefix))
                continue;

            if (aliasIndex >= 0)
            {
                AddPythonImportEntry(
                    line,
                    absoluteStartColumn,
                    $"{pythonModulePrefix}.{localName}",
                    entries,
                    seenNames,
                    ref searchStartColumn);
            }
        }
    }

    private static string? ResolvePythonRelativeFromImportModuleName(string? modulePart, string? pythonModulePrefix)
    {
        if (string.IsNullOrEmpty(modulePart)
            || string.IsNullOrEmpty(pythonModulePrefix)
            || !modulePart.StartsWith(".", StringComparison.Ordinal))
        {
            return null;
        }

        var leadingDots = 0;
        while (leadingDots < modulePart.Length && modulePart[leadingDots] == '.')
            leadingDots++;

        var levelsToDrop = leadingDots - 1;
        var packageParts = pythonModulePrefix.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (levelsToDrop >= packageParts.Length)
            return null;

        var basePartCount = packageParts.Length - levelsToDrop;
        var resolved = string.Join('.', packageParts.Take(basePartCount));
        var suffix = modulePart[leadingDots..].Trim('.');
        return suffix.Length == 0
            ? resolved
            : $"{resolved}.{suffix}";
    }

    private static void AddPythonImportModuleEntry(
        string line,
        int absoluteStartColumn,
        string modulePart,
        List<PythonImportSymbolEntry> entries,
        HashSet<string> seenNames,
        string? pythonModulePrefix)
    {
        modulePart = modulePart.Trim();
        if (modulePart.Length == 0)
            return;

        var searchStartColumn = absoluteStartColumn;
        var normalizedModule = modulePart.TrimStart('.');
        if (normalizedModule.Length > 0)
        {
            AddPythonImportEntry(line, absoluteStartColumn, normalizedModule, entries, seenNames, ref searchStartColumn);
            if (normalizedModule.Contains('.'))
            {
                AddPythonImportDottedPrefixEntries(
                    line,
                    absoluteStartColumn,
                    normalizedModule,
                    entries,
                    seenNames,
                    ref searchStartColumn);
            }
        }

        var relativeQualifiedModule = ResolvePythonRelativeFromImportModuleName(modulePart, pythonModulePrefix);
        if (!string.IsNullOrEmpty(relativeQualifiedModule))
        {
            AddPythonImportEntry(
                line,
                absoluteStartColumn,
                relativeQualifiedModule,
                entries,
                seenNames,
                ref searchStartColumn);
        }
    }

    private static void AddPythonImportEntry(
        string line,
        int absoluteStartColumn,
        string symbolName,
        List<PythonImportSymbolEntry> entries,
        HashSet<string> seenNames,
        ref int searchStartColumn)
    {
        symbolName = symbolName.Trim();
        if (symbolName.Length == 0 || !seenNames.Add(symbolName))
            return;

        var startColumn = line.IndexOf(symbolName, searchStartColumn, StringComparison.Ordinal);
        if (startColumn < 0)
        {
            startColumn = line.IndexOf(symbolName, absoluteStartColumn, StringComparison.Ordinal);
            if (startColumn < 0)
                startColumn = absoluteStartColumn;
        }
        else
        {
            searchStartColumn = startColumn + Math.Max(1, symbolName.Length);
        }

        entries.Add(new PythonImportSymbolEntry(symbolName, startColumn));
    }

    private static void AddPythonImportDottedPrefixEntries(
        string line,
        int absoluteStartColumn,
        string dottedName,
        List<PythonImportSymbolEntry> entries,
        HashSet<string> seenNames,
        ref int searchStartColumn)
    {
        var prefixStart = 0;
        while (prefixStart >= 0 && prefixStart < dottedName.Length)
        {
            var dotIndex = dottedName.IndexOf('.', prefixStart);
            if (dotIndex < 0)
                break;

            var prefix = dottedName[..dotIndex].Trim();
            if (prefix.Length > 0)
            {
                AddPythonImportEntry(line, absoluteStartColumn, prefix, entries, seenNames, ref searchStartColumn);
            }

            prefixStart = dotIndex + 1;
        }
    }

    private static string? GetPythonModulePrefix(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;

        var normalizedPath = filePath.Replace('\\', '/');
        if (!normalizedPath.EndsWith("/__init__.py", StringComparison.Ordinal)
            && !string.Equals(normalizedPath, "__init__.py", StringComparison.Ordinal))
        {
            return null;
        }

        var modulePath = normalizedPath[..^"/__init__.py".Length];
        if (modulePath.Length == 0)
            return null;

        return modulePath.Replace('/', '.').Trim('.');
    }

    private static (int EndLine, int? BodyStartLine, int? BodyEndLine) FindIndentRange(string[] lines, int startIndex)
    {
        var currentLine = lines[startIndex];
        var currentIndent = CountIndent(currentLine);
        var trimmedCurrent = currentLine.Trim();

        if (trimmedCurrent.Contains(':'))
        {
            var suffix = trimmedCurrent[(trimmedCurrent.IndexOf(':') + 1)..].Trim();
            if (suffix.Length > 0 && !suffix.StartsWith('#'))
                return (startIndex + 1, startIndex + 1, startIndex + 1);
        }

        int? bodyStartLine = null;
        int endLine = startIndex + 1;

        for (int i = startIndex + 1; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0)
                continue;

            var indent = CountIndent(lines[i]);
            if (bodyStartLine == null)
            {
                if (indent <= currentIndent)
                    return (endLine, null, null);

                bodyStartLine = i + 1;
                endLine = i + 1;
                continue;
            }

            if (indent <= currentIndent)
                return (endLine, bodyStartLine, endLine);

            endLine = i + 1;
        }

        return bodyStartLine == null
            ? (startIndex + 1, null, null)
            : (endLine, bodyStartLine, endLine);
    }

}
