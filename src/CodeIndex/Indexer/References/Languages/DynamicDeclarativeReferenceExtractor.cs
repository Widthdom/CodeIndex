using System.Text.RegularExpressions;
using CodeIndex.Models;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

internal static class DynamicDeclarativeReferenceExtractor
{
    internal sealed class ExtractionState
    {
        public ExtractionState(
            HashSet<string> callableNames,
            IReadOnlyDictionary<int, SymbolRecord> containersByLine)
        {
            CallableNames = callableNames;
            ContainersByLine = containersByLine;
        }

        public HashSet<string> CallableNames { get; }
        public IReadOnlyDictionary<int, SymbolRecord> ContainersByLine { get; }

        public SymbolRecord? ResolveContainer(int lineNumber, SymbolRecord? fallback)
            => ContainersByLine.TryGetValue(lineNumber, out var container) ? container : fallback;
    }

    private static readonly Regex TclProcRegex = new(
        @"^\s*proc\s+[A-Za-z_:][\w:.-]*\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PrologHeadRegex = new(
        @"^\s*(?<name>[a-z][A-Za-z0-9_]*)\s*(?:\([^\r\n]*\))?\s*(?::-|-->|\.)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CrystalRequireRegex = new(
        @"^\s*require\s+['""](?<name>[^'""]+)['""]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GroovyImportRegex = new(
        @"^\s*import\s+(?:static\s+)?(?<name>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)(?:\.\*)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TclPackageRegex = new(
        @"^\s*package\s+require\s+(?:-exact\s+)?(?<name>[A-Za-z_:][\w:.-]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PrologImportRegex = new(
        @"^\s*:-\s*use_module\s*\(\s*(?:library\s*\(\s*)?['""]?(?<name>(?:\.\.?/)*[a-z][A-Za-z0-9_./-]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CrystalBareCallRegex = new(
        @"(?:^|[;=])\s*(?:return\s+)?(?<name>[A-Za-z_]\w*[?!]?)(?![\w?!])\s*(?!\()",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CrystalSuffixedParenthesizedCallRegex = new(
        @"(?<![\w])(?<name>[A-Za-z_]\w*[?!])\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GroovyBareCallRegex = new(
        @"(?:^|[;=])\s*(?:return\s+)?(?<name>[A-Za-z_]\w*)\b(?!\s*\()",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TclCommandRegex = new(
        @"(?:^|[;\[{}])\s*(?<name>[A-Za-z_:][\w:.-]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PrologBareCallRegex = new(
        @"(?:^|:-|-->|[,;])\s*(?:\\\+\s*)?(?<name>[a-z][A-Za-z0-9_]*)\b(?!\s*\()",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CrystalHeredocOpenerRegex = new(
        @"<<-\s*['""]?(?<delimiter>[A-Za-z_]\w*)['""]?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly record struct TclContainerScope(
        SymbolRecord Symbol,
        int StartLine,
        int EndLine);

    private readonly record struct TclBraceEnd(
        int Line,
        int Column);

    public static string[] MaskNonCodeLines(string language, IReadOnlyList<string> lines)
    {
        if (language is not ("crystal" or "groovy" or "tcl" or "prolog" or "ambiguous_pl"))
            return lines as string[] ?? lines.ToArray();

        var result = new string[lines.Count];
        var insideBlockComment = false;
        char groovyTripleQuote = '\0';
        string? crystalHeredocDelimiter = null;
        var insideAmbiguousPerlPod = false;

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            if (crystalHeredocDelimiter != null)
            {
                result[lineIndex] = new string(' ', line.Length);
                if (string.Equals(line.Trim(), crystalHeredocDelimiter, StringComparison.Ordinal))
                    crystalHeredocDelimiter = null;
                continue;
            }

            if (language == "ambiguous_pl")
            {
                var trimmed = line.AsSpan().TrimStart();
                if (insideAmbiguousPerlPod)
                {
                    result[lineIndex] = new string(' ', line.Length);
                    if (trimmed.StartsWith("=cut", StringComparison.Ordinal))
                        insideAmbiguousPerlPod = false;
                    continue;
                }

                if (trimmed.Length > 1
                    && trimmed[0] == '='
                    && char.IsLetter(trimmed[1]))
                {
                    result[lineIndex] = new string(' ', line.Length);
                    insideAmbiguousPerlPod = !trimmed.StartsWith("=cut", StringComparison.Ordinal);
                    continue;
                }
            }

            var buffer = line.ToCharArray();
            for (var column = 0; column < line.Length;)
            {
                if (insideBlockComment)
                {
                    buffer[column] = ' ';
                    if (column + 1 < line.Length && line[column] == '*' && line[column + 1] == '/')
                    {
                        buffer[column + 1] = ' ';
                        column += 2;
                        insideBlockComment = false;
                    }
                    else
                    {
                        column++;
                    }
                    continue;
                }

                if (groovyTripleQuote != '\0')
                {
                    buffer[column] = ' ';
                    if (column + 2 < line.Length
                        && line[column] == groovyTripleQuote
                        && line[column + 1] == groovyTripleQuote
                        && line[column + 2] == groovyTripleQuote)
                    {
                        buffer[column + 1] = ' ';
                        buffer[column + 2] = ' ';
                        column += 3;
                        groovyTripleQuote = '\0';
                    }
                    else
                    {
                        column++;
                    }
                    continue;
                }

                var ch = line[column];
                if (language == "groovy"
                    && ch is '\'' or '"'
                    && column + 2 < line.Length
                    && line[column + 1] == ch
                    && line[column + 2] == ch)
                {
                    buffer[column] = ' ';
                    buffer[column + 1] = ' ';
                    buffer[column + 2] = ' ';
                    groovyTripleQuote = ch;
                    column += 3;
                    continue;
                }

                if (ch is '\'' or '"' or '`')
                {
                    column = SkipQuotedToken(line, column, ch);
                    continue;
                }

                if (language == "crystal"
                    && column + 2 < line.Length
                    && line[column] == '<'
                    && line[column + 1] == '<'
                    && line[column + 2] == '-')
                {
                    var heredocMatch = CrystalHeredocOpenerRegex.Match(line, column);
                    if (heredocMatch.Success && heredocMatch.Index == column)
                        crystalHeredocDelimiter = heredocMatch.Groups["delimiter"].Value;
                }

                if ((language is "groovy" or "prolog" or "ambiguous_pl")
                    && column + 1 < line.Length
                    && line[column] == '/'
                    && line[column + 1] == '*')
                {
                    buffer[column] = ' ';
                    buffer[column + 1] = ' ';
                    column += 2;
                    insideBlockComment = true;
                    continue;
                }

                if (language == "groovy"
                    && column + 1 < line.Length
                    && line[column] == '/'
                    && line[column + 1] == '/')
                {
                    FillWithSpaces(buffer, column);
                    break;
                }

                if ((language is "crystal" or "tcl" or "ambiguous_pl") && ch == '#')
                {
                    FillWithSpaces(buffer, column);
                    break;
                }

                if ((language is "prolog" or "ambiguous_pl") && ch == '%')
                {
                    FillWithSpaces(buffer, column);
                    break;
                }

                column++;
            }

            result[lineIndex] = new string(buffer);
        }

        return result;
    }

    public static ExtractionState? CreateState(
        string language,
        IReadOnlyList<string> preparedLines,
        IReadOnlyList<SymbolRecord> symbols)
    {
        if (language is not ("crystal" or "groovy" or "tcl" or "prolog" or "ambiguous_pl"))
            return null;

        var callableNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
        {
            if (symbol.Kind is "function" or "lambda" or "operator")
                callableNames.Add(symbol.Name);
        }

        var containersByLine = new Dictionary<int, SymbolRecord>();
        if (language == "tcl")
            AddTclContainers(preparedLines, symbols, containersByLine);
        else if (language is "prolog" or "ambiguous_pl")
            AddPrologContainers(preparedLines, symbols, containersByLine);

        return new ExtractionState(callableNames, containersByLine);
    }

    public static void EmitAdditionalReferences(
        string language,
        string preparedLine,
        string structuralLine,
        ExtractionState state,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Action<string, int> addCallLikeReference)
    {
        EmitImportReference(
            language,
            structuralLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForCall);

        var callRegex = language switch
        {
            "crystal" => CrystalBareCallRegex,
            "groovy" => GroovyBareCallRegex,
            "tcl" => TclCommandRegex,
            "prolog" or "ambiguous_pl" => PrologBareCallRegex,
            _ => null,
        };
        if (callRegex == null)
            return;

        if (language == "crystal")
        {
            foreach (Match match in BoundedRegex.EnumerateMatches(CrystalSuffixedParenthesizedCallRegex, preparedLine))
            {
                var nameGroup = match.Groups["name"];
                if (state.CallableNames.Contains(nameGroup.Value))
                    addCallLikeReference(nameGroup.Value, nameGroup.Index);
            }
        }

        foreach (Match match in BoundedRegex.EnumerateMatches(callRegex, preparedLine))
        {
            var nameGroup = match.Groups["name"];
            if (!state.CallableNames.Contains(nameGroup.Value))
                continue;

            if (language is "prolog" or "ambiguous_pl"
                && state.ContainersByLine.TryGetValue(lineNumber, out var prologContainer)
                && !string.Equals(prologContainer.Name, nameGroup.Value, StringComparison.Ordinal))
            {
                ReferenceExtractor.AddReference(
                    references,
                    seen,
                    fileId,
                    nameGroup.Value,
                    nameGroup.Index,
                    "call",
                    context,
                    lineNumber,
                    prologContainer,
                    language);
                continue;
            }

            addCallLikeReference(nameGroup.Value, nameGroup.Index);
        }
    }

    private static int SkipQuotedToken(string line, int startColumn, char delimiter)
    {
        for (var column = startColumn + 1; column < line.Length; column++)
        {
            if (line[column] == '\\')
            {
                column++;
                continue;
            }

            if (line[column] != delimiter)
                continue;

            if (column + 1 < line.Length && line[column + 1] == delimiter)
            {
                column++;
                continue;
            }

            return column + 1;
        }

        return line.Length;
    }

    private static void FillWithSpaces(char[] buffer, int startColumn)
    {
        for (var column = startColumn; column < buffer.Length; column++)
            buffer[column] = ' ';
    }

    private static void EmitImportReference(
        string language,
        string originalLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForCall)
    {
        var match = language switch
        {
            "crystal" => CrystalRequireRegex.Match(originalLine),
            "groovy" => GroovyImportRegex.Match(originalLine),
            "tcl" => TclPackageRegex.Match(originalLine),
            "prolog" or "ambiguous_pl" => PrologImportRegex.Match(originalLine),
            _ => Match.Empty,
        };
        if (!match.Success)
            return;

        var nameGroup = match.Groups["name"];
        var name = NormalizeImportTarget(language, nameGroup.Value);
        if (name.Length == 0)
            return;

        ReferenceExtractor.AddReference(
            references,
            seen,
            fileId,
            name,
            nameGroup.Index,
            "type_reference",
            context,
            lineNumber,
            resolveContainerForCall(nameGroup.Index),
            language);
    }

    private static string NormalizeImportTarget(string language, string name)
    {
        var normalized = name.Replace('\\', '/').TrimEnd('/');
        if (language == "groovy")
            return normalized[(normalized.LastIndexOf('.') + 1)..];
        if (language is "crystal" or "prolog" or "ambiguous_pl")
        {
            normalized = normalized[(normalized.LastIndexOf('/') + 1)..];
            var extensionIndex = normalized.LastIndexOf('.');
            if (extensionIndex > 0)
                normalized = normalized[..extensionIndex];
        }
        return normalized;
    }

    private static void AddTclContainers(
        IReadOnlyList<string> lines,
        IReadOnlyList<SymbolRecord> symbols,
        Dictionary<int, SymbolRecord> containersByLine)
    {
        var braceEnds = BuildTclBraceEndPositions(lines);
        var scopes = new List<TclContainerScope>();
        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "function" || symbol.StartLine < 1 || symbol.StartLine > lines.Count)
                continue;

            var startLineIndex = symbol.StartLine - 1;
            var declarationMatch = TclProcRegex.Match(lines[startLineIndex]);
            if (!declarationMatch.Success
                || !TryFindTclBodyEnd(
                    lines,
                    braceEnds,
                    startLineIndex,
                    declarationMatch.Index + declarationMatch.Length,
                    out var endLineIndex))
            {
                continue;
            }

            scopes.Add(new TclContainerScope(symbol, symbol.StartLine, endLineIndex + 1));
        }

        scopes.Sort(static (left, right) =>
        {
            var startComparison = left.StartLine.CompareTo(right.StartLine);
            return startComparison != 0
                ? startComparison
                : right.EndLine.CompareTo(left.EndLine);
        });

        var activeScopes = new Stack<TclContainerScope>();
        var scopeIndex = 0;
        for (var lineNumber = 1; lineNumber <= lines.Count; lineNumber++)
        {
            while (activeScopes.Count > 0 && activeScopes.Peek().EndLine < lineNumber)
                activeScopes.Pop();

            while (scopeIndex < scopes.Count && scopes[scopeIndex].StartLine == lineNumber)
            {
                var scope = scopes[scopeIndex++];
                while (activeScopes.Count > 0 && activeScopes.Peek().EndLine < lineNumber)
                    activeScopes.Pop();
                activeScopes.Push(scope);
            }

            if (activeScopes.Count > 0)
                containersByLine[lineNumber] = activeScopes.Peek().Symbol;
        }
    }

    private static bool TryFindTclBodyEnd(
        IReadOnlyList<string> lines,
        IReadOnlyDictionary<long, TclBraceEnd> braceEnds,
        int startLineIndex,
        int searchColumn,
        out int endLineIndex)
    {
        endLineIndex = startLineIndex;
        if (!TryFindNextNonWhitespace(lines[startLineIndex], searchColumn, out var argsColumn)
            || !TryFindTclWordEnd(
                lines,
                braceEnds,
                startLineIndex,
                argsColumn,
                out var argsEndLine,
                out var argsEndColumn)
            || !TryFindNextNonWhitespace(lines[argsEndLine], argsEndColumn + 1, out var bodyColumn)
            || lines[argsEndLine][bodyColumn] != '{'
            || !braceEnds.TryGetValue(GetTclPositionKey(argsEndLine, bodyColumn), out var bodyEnd))
        {
            return false;
        }

        endLineIndex = bodyEnd.Line;
        return true;
    }

    private static bool TryFindTclWordEnd(
        IReadOnlyList<string> lines,
        IReadOnlyDictionary<long, TclBraceEnd> braceEnds,
        int startLine,
        int startColumn,
        out int endLine,
        out int endColumn)
    {
        var line = lines[startLine];
        if (line[startColumn] == '{')
        {
            if (braceEnds.TryGetValue(GetTclPositionKey(startLine, startColumn), out var braceEnd))
            {
                endLine = braceEnd.Line;
                endColumn = braceEnd.Column;
                return true;
            }

            endLine = -1;
            endColumn = -1;
            return false;
        }

        if (line[startColumn] == '"')
        {
            for (var column = startColumn + 1; column < line.Length; column++)
            {
                if (line[column] == '\\')
                {
                    column++;
                    continue;
                }
                if (line[column] == '"')
                {
                    endLine = startLine;
                    endColumn = column;
                    return true;
                }
            }

            endLine = -1;
            endColumn = -1;
            return false;
        }

        var wordEnd = startColumn;
        while (wordEnd + 1 < line.Length && !char.IsWhiteSpace(line[wordEnd + 1]))
            wordEnd++;
        endLine = startLine;
        endColumn = wordEnd;
        return true;
    }

    private static bool TryFindNextNonWhitespace(
        string line,
        int startColumn,
        out int foundColumn)
    {
        for (var column = startColumn; column < line.Length; column++)
        {
            if (!char.IsWhiteSpace(line[column]))
            {
                foundColumn = column;
                return true;
            }
        }

        foundColumn = -1;
        return false;
    }

    private static Dictionary<long, TclBraceEnd> BuildTclBraceEndPositions(IReadOnlyList<string> lines)
    {
        var result = new Dictionary<long, TclBraceEnd>();
        var openings = new Stack<(int Line, int Column)>();
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            for (var column = 0; column < line.Length; column++)
            {
                if (line[column] == '\\')
                {
                    column++;
                    continue;
                }

                if (line[column] == '{')
                {
                    openings.Push((lineIndex, column));
                }
                else if (line[column] == '}' && openings.Count > 0)
                {
                    var opening = openings.Pop();
                    result[GetTclPositionKey(opening.Line, opening.Column)] = new TclBraceEnd(lineIndex, column);
                }
            }
        }

        return result;
    }

    private static long GetTclPositionKey(int line, int column) =>
        ((long)line << 32) | (uint)column;

    private static void AddPrologContainers(
        IReadOnlyList<string> lines,
        IReadOnlyList<SymbolRecord> symbols,
        Dictionary<int, SymbolRecord> containersByLine)
    {
        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "function" || symbol.StartLine < 1 || symbol.StartLine > lines.Count)
                continue;

            var startLineIndex = symbol.StartLine - 1;
            var headMatch = PrologHeadRegex.Match(lines[startLineIndex]);
            if (!headMatch.Success
                || !string.Equals(headMatch.Groups["name"].Value, symbol.Name, StringComparison.Ordinal))
            {
                continue;
            }

            var endLineIndex = FindPrologClauseEnd(lines, startLineIndex);
            for (var lineIndex = startLineIndex; lineIndex <= endLineIndex; lineIndex++)
                containersByLine.TryAdd(lineIndex + 1, symbol);
        }
    }

    private static int FindPrologClauseEnd(IReadOnlyList<string> lines, int startLineIndex)
    {
        for (var lineIndex = startLineIndex; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            for (var column = 0; column < line.Length; column++)
            {
                if (line[column] != '.')
                    continue;

                var previousIsDigit = column > 0 && char.IsDigit(line[column - 1]);
                var nextIsIdentifier = column + 1 < line.Length
                    && (char.IsLetterOrDigit(line[column + 1]) || line[column + 1] == '_');
                if (!previousIsDigit && !nextIsIdentifier)
                    return lineIndex;
            }
        }

        return startLineIndex;
    }
}
