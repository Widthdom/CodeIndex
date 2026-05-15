using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static readonly Regex PhpPropertyDeclarationHeadRegex = new(
        @"^\s*(?:(?<visibility>public|private|protected|var)\s+)(?:(?:static|readonly)\s+)*(?:(?<returnType>\??[A-Za-z_\\][\w\\]*(?:\s*[|&]\s*\??[A-Za-z_\\][\w\\]*)*)\s+)?\$(?<name>\w+)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PhpSameLineConstructorRegex = new(
        @"\bfunction\s+__construct\s*\((?<parameters>[^)]*)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex PhpConstructorStartRegex = new(
        @"\bfunction\s+__construct\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex PhpPromotedPropertyParameterRegex = new(
        @"^\s*(?:(?<visibility>public|private|protected)\s+)(?:(?:readonly)\s+)*(?:(?<returnType>\??[A-Za-z_\\][\w\\]*(?:\s*[|&]\s*\??[A-Za-z_\\][\w\\]*)*)\s+)?\$(?<name>\w+)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PhpDocblockMethodRegex = new(
        @"^\s*(?:/\*\*)?\s*\*?\s*@method\s+(?:static\s+)?(?:(?<returnType>[^\s()]+)\s+)?(?<name>[A-Za-z_]\w*)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex PhpDocblockPropertyRegex = new(
        @"^\s*(?:/\*\*)?\s*\*?\s*@(?>phpstan-|psalm-)?property(?:-read|-write)?\s+(?<returnType>[^\s]+)\s+\$(?<name>[A-Za-z_]\w*)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex PhpTraitAliasRegex = new(
        @"^\s*(?:[A-Za-z_]\w*(?:\\[A-Za-z_]\w*)*::)?[A-Za-z_]\w*\s+as\s+(?:(?:public|protected|private)\s+)?(?<name>(?!public\b|protected\b|private\b)[A-Za-z_]\w*)\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex PhpUseGroupItemAliasRegex = new(
        @"^(?<name>[\w\\]+)\s+as\s+(?<alias>\w+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex PhpDocblockTypeAliasRegex = new(
        @"^\s*(?:/\*\*)?\s*\*?\s*@(?>phpstan-|psalm-)?type\s+(?<name>[A-Za-z_]\w*)\s+(?<returnType>\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex PhpDocblockImportTypeRegex = new(
        @"^\s*(?:/\*\*)?\s*\*?\s*@(?>phpstan-|psalm-)?import-type\s+(?<imported>[A-Za-z_]\w*)\s+from\s+(?<returnType>\S+)(?:\s+as\s+(?<alias>[A-Za-z_]\w*))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static void ExtractPhpImportSymbols(List<SymbolRecord> symbols, string line, int lineNumber)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        var groupUseMatch = PhpGroupUseRegex.Match(line);
        if (groupUseMatch.Success)
        {
            var prefix = groupUseMatch.Groups["prefix"].Value;
            var signature = line.Trim();
            foreach (var rawItem in groupUseMatch.Groups["items"].Value.Split(','))
            {
                var item = rawItem.Trim();
                if (item.Length == 0)
                    continue;

                var importedName = item;
                var alias = string.Empty;
                var aliasMatch = PhpUseGroupItemAliasRegex.Match(item);
                if (aliasMatch.Success)
                {
                    importedName = aliasMatch.Groups["name"].Value;
                    alias = aliasMatch.Groups["alias"].Value;
                }

                var symbolName = alias.Length > 0 ? alias : prefix + importedName;
                if (symbolName.Length == 0)
                    continue;

                AddSymbolRecord(
                    symbols,
                    cssSeenSymbols: null,
                    lineNumber,
                    new SymbolRecord
                    {
                        Kind = "import",
                        Name = symbolName,
                        Line = lineNumber,
                        StartLine = lineNumber,
                        EndLine = lineNumber,
                        Signature = signature
                    });
            }

            return;
        }

        var useMatch = PhpUseRegex.Match(line);
        if (useMatch.Success)
        {
            var symbolName = useMatch.Groups["alias"].Success
                ? useMatch.Groups["alias"].Value.Trim()
                : useMatch.Groups["name"].Value.Trim();
            if (symbolName.Length > 0)
            {
                AddSymbolRecord(
                    symbols,
                    cssSeenSymbols: null,
                    lineNumber,
                    new SymbolRecord
                    {
                        Kind = "import",
                        Name = symbolName,
                        Line = lineNumber,
                        StartLine = lineNumber,
                        EndLine = lineNumber,
                        Signature = line.Trim()
                    });
            }

            return;
        }

        var requireMatch = PhpRequireIncludeRegex.Match(line);
        if (!requireMatch.Success)
            requireMatch = PhpPrefixedRequireIncludeRegex.Match(line);
        if (!requireMatch.Success)
            return;

        var importedPath = requireMatch.Groups["singleName"].Success
            ? requireMatch.Groups["singleName"].Value.Trim()
            : requireMatch.Groups["doubleName"].Value.Trim();
        if (importedPath.Length == 0)
            return;

        if (requireMatch.Groups["prefix"].Success)
            importedPath = importedPath.TrimStart('/', '\\');

        AddSymbolRecord(
            symbols,
            cssSeenSymbols: null,
            lineNumber,
            new SymbolRecord
            {
                Kind = "import",
                Name = importedPath,
                Line = lineNumber,
                StartLine = lineNumber,
                EndLine = lineNumber,
                Signature = line.Trim()
            });
    }

    private static void ExtractPhpAdditionalPropertySymbols(long fileId, string[] lines, List<SymbolRecord> symbols)
    {
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var match = PhpPropertyDeclarationHeadRegex.Match(line);
            if (!match.Success)
                continue;

            var lineNumber = lineIndex + 1;
            var firstNameEnd = match.Groups["name"].Index + match.Groups["name"].Length;
            foreach (var candidate in EnumeratePhpAdditionalPropertyNames(line, firstNameEnd))
            {
                AddSymbolRecord(
                    symbols,
                    cssSeenSymbols: null,
                    lineNumber,
                    new SymbolRecord
                    {
                        FileId = fileId,
                        Kind = "property",
                        Name = candidate.Name,
                        Line = lineNumber,
                        StartLine = lineNumber,
                        StartColumn = candidate.Column,
                        EndLine = lineNumber,
                        Signature = line.Trim(),
                        Visibility = match.Groups["visibility"].Value,
                        ReturnType = match.Groups["returnType"].Success
                            ? match.Groups["returnType"].Value
                            : null,
                    },
                    line);
            }
        }
    }

    private static void ExtractPhpDocblockMethodSymbols(long fileId, string[] lines, List<SymbolRecord> symbols)
    {
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var match = PhpDocblockMethodRegex.Match(line);
            if (!match.Success)
                continue;

            var lineNumber = lineIndex + 1;
            var nameGroup = match.Groups["name"];
            AddSymbolRecord(
                symbols,
                cssSeenSymbols: null,
                lineNumber,
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "function",
                    Name = nameGroup.Value,
                    Line = lineNumber,
                    StartLine = lineNumber,
                    StartColumn = nameGroup.Index,
                    EndLine = lineNumber,
                    Signature = line.Trim(),
                    ReturnType = match.Groups["returnType"].Success
                        ? match.Groups["returnType"].Value
                        : null,
                },
                line);
        }
    }

    private static void ExtractPhpDocblockPropertySymbols(long fileId, string[] lines, List<SymbolRecord> symbols)
    {
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var match = PhpDocblockPropertyRegex.Match(line);
            if (!match.Success)
                continue;

            var lineNumber = lineIndex + 1;
            var nameGroup = match.Groups["name"];
            AddSymbolRecord(
                symbols,
                cssSeenSymbols: null,
                lineNumber,
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "property",
                    Name = nameGroup.Value,
                    Line = lineNumber,
                    StartLine = lineNumber,
                    StartColumn = nameGroup.Index,
                    EndLine = lineNumber,
                    Signature = line.Trim(),
                    ReturnType = match.Groups["returnType"].Value,
                },
                line);
        }
    }

    private static void ExtractPhpTraitAliasSymbols(long fileId, string[] lines, List<SymbolRecord> symbols)
    {
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var match = PhpTraitAliasRegex.Match(line);
            if (!match.Success)
                continue;

            var lineNumber = lineIndex + 1;
            var nameGroup = match.Groups["name"];
            AddSymbolRecord(
                symbols,
                cssSeenSymbols: null,
                lineNumber,
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "function",
                    Name = nameGroup.Value,
                    Line = lineNumber,
                    StartLine = lineNumber,
                    StartColumn = nameGroup.Index,
                    EndLine = lineNumber,
                    Signature = line.Trim(),
                },
                line);
        }
    }

    private static void ExtractPhpDocblockTypeAliasSymbols(long fileId, string[] lines, List<SymbolRecord> symbols)
    {
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var match = PhpDocblockTypeAliasRegex.Match(line);
            if (!match.Success)
                continue;

            var lineNumber = lineIndex + 1;
            var nameGroup = match.Groups["name"];
            AddSymbolRecord(
                symbols,
                cssSeenSymbols: null,
                lineNumber,
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "type",
                    Name = nameGroup.Value,
                    Line = lineNumber,
                    StartLine = lineNumber,
                    StartColumn = nameGroup.Index,
                    EndLine = lineNumber,
                    Signature = line.Trim(),
                    ReturnType = match.Groups["returnType"].Value,
                },
                line);
        }
    }

    private static void ExtractPhpDocblockImportTypeSymbols(long fileId, string[] lines, List<SymbolRecord> symbols)
    {
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var match = PhpDocblockImportTypeRegex.Match(line);
            if (!match.Success)
                continue;

            var lineNumber = lineIndex + 1;
            var nameGroup = match.Groups["alias"].Success
                ? match.Groups["alias"]
                : match.Groups["imported"];
            AddSymbolRecord(
                symbols,
                cssSeenSymbols: null,
                lineNumber,
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "type",
                    Name = nameGroup.Value,
                    Line = lineNumber,
                    StartLine = lineNumber,
                    StartColumn = nameGroup.Index,
                    EndLine = lineNumber,
                    Signature = line.Trim(),
                    ReturnType = match.Groups["returnType"].Value,
                },
                line);
        }
    }

    private static IEnumerable<(string Name, int Column)> EnumeratePhpAdditionalPropertyNames(string line, int startColumn)
    {
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var quote = '\0';

        for (var index = Math.Max(0, startColumn); index < line.Length; index++)
        {
            var ch = line[index];
            if (quote != '\0')
            {
                if (ch == '\\' && index + 1 < line.Length)
                {
                    index++;
                    continue;
                }

                if (ch == quote)
                    quote = '\0';
                continue;
            }

            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }

            if (ch == '/' && index + 1 < line.Length && line[index + 1] == '/')
                yield break;
            if (ch == '#')
                yield break;
            if (ch == ';' && parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
                yield break;

            if (ch == '(')
                parenDepth++;
            else if (ch == ')' && parenDepth > 0)
                parenDepth--;
            else if (ch == '[')
                bracketDepth++;
            else if (ch == ']' && bracketDepth > 0)
                bracketDepth--;
            else if (ch == '{')
                braceDepth++;
            else if (ch == '}' && braceDepth > 0)
                braceDepth--;

            if (ch != ',' || parenDepth != 0 || bracketDepth != 0 || braceDepth != 0)
                continue;

            var nameStart = index + 1;
            while (nameStart < line.Length && char.IsWhiteSpace(line[nameStart]))
                nameStart++;
            if (nameStart >= line.Length || line[nameStart] != '$')
                continue;

            var nameColumn = nameStart + 1;
            var nameEnd = nameColumn;
            while (nameEnd < line.Length && IsPhpIdentifierPart(line[nameEnd]))
                nameEnd++;

            if (nameEnd > nameColumn)
                yield return (line[nameColumn..nameEnd], nameColumn);
        }
    }

    private static bool IsPhpIdentifierPart(char ch)
        => ch == '_' || char.IsLetterOrDigit(ch);

    private static void ExtractPhpPromotedConstructorProperties(long fileId, string[] lines, List<SymbolRecord> symbols)
    {
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var constructorMatch = PhpSameLineConstructorRegex.Match(line);
            var constructorStart = PhpConstructorStartRegex.IsMatch(line);
            if (!constructorMatch.Success && !constructorStart)
                continue;

            if (constructorMatch.Success)
            {
                var lineNumber = lineIndex + 1;
                var parameters = constructorMatch.Groups["parameters"];
                foreach (var parameter in EnumeratePhpTopLevelCommaSegments(parameters.Value))
                {
                    AddPhpPromotedPropertySymbol(
                        fileId,
                        symbols,
                        line,
                        lineNumber,
                        parameters.Index + parameter.StartColumn,
                        parameter.Text);
                }
            }

            if (constructorMatch.Success || !constructorStart)
                continue;

            for (var parameterLineIndex = lineIndex + 1;
                 parameterLineIndex < lines.Length && parameterLineIndex <= lineIndex + 64;
                 parameterLineIndex++)
            {
                var parameterLine = lines[parameterLineIndex];
                AddPhpPromotedPropertySymbol(
                    fileId,
                    symbols,
                    parameterLine,
                    parameterLineIndex + 1,
                    0,
                    parameterLine);

                if (parameterLine.Contains(')'))
                    break;
            }
        }
    }

    private static void AddPhpPromotedPropertySymbol(
        long fileId,
        List<SymbolRecord> symbols,
        string line,
        int lineNumber,
        int baseColumn,
        string parameterText)
    {
        var match = PhpPromotedPropertyParameterRegex.Match(parameterText);
        if (!match.Success)
            return;

        var name = match.Groups["name"].Value;
        AddSymbolRecord(
            symbols,
            cssSeenSymbols: null,
            lineNumber,
            new SymbolRecord
            {
                FileId = fileId,
                Kind = "property",
                Name = name,
                Line = lineNumber,
                StartLine = lineNumber,
                StartColumn = baseColumn + match.Groups["name"].Index,
                EndLine = lineNumber,
                Signature = line.Trim(),
                Visibility = match.Groups["visibility"].Value,
                ReturnType = match.Groups["returnType"].Success
                    ? match.Groups["returnType"].Value
                    : null,
            },
            line);
    }

    private static IEnumerable<(string Text, int StartColumn)> EnumeratePhpTopLevelCommaSegments(string text)
    {
        var segmentStart = 0;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var quote = '\0';

        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            if (quote != '\0')
            {
                if (ch == '\\' && index + 1 < text.Length)
                {
                    index++;
                    continue;
                }

                if (ch == quote)
                    quote = '\0';
                continue;
            }

            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }

            if (ch == '(')
                parenDepth++;
            else if (ch == ')' && parenDepth > 0)
                parenDepth--;
            else if (ch == '[')
                bracketDepth++;
            else if (ch == ']' && bracketDepth > 0)
                bracketDepth--;
            else if (ch == '{')
                braceDepth++;
            else if (ch == '}' && braceDepth > 0)
                braceDepth--;

            if (ch != ',' || parenDepth != 0 || bracketDepth != 0 || braceDepth != 0)
                continue;

            yield return TrimPhpCommaSegment(text, segmentStart, index);
            segmentStart = index + 1;
        }

        yield return TrimPhpCommaSegment(text, segmentStart, text.Length);
    }

    private static (string Text, int StartColumn) TrimPhpCommaSegment(string text, int start, int end)
    {
        while (start < end && char.IsWhiteSpace(text[start]))
            start++;
        while (end > start && char.IsWhiteSpace(text[end - 1]))
            end--;

        return (text[start..end], start);
    }

}
