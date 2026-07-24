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
        @"^\s*(?<name>[a-z][A-Za-z0-9_]*)\s*(?:\([^.\r\n]*\))?\s*(?::-|-->|\.)",
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
        @"^\s*:-\s*use_module\s*\(\s*(?:library\s*\(\s*)?['""]?(?<name>[a-z][A-Za-z0-9_./-]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CrystalGroovyBareCallRegex = new(
        @"(?:^|[;=])\s*(?:return\s+)?(?<name>[A-Za-z_]\w*)\b(?!\s*\()",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TclCommandRegex = new(
        @"(?:^|[;\[])\s*(?<name>[A-Za-z_:][\w:.-]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PrologBareCallRegex = new(
        @"(?:^|:-|-->|[,;])\s*(?:\\\+\s*)?(?<name>[a-z][A-Za-z0-9_]*)\b(?!\s*\()",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
        string originalLine,
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
            originalLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForCall);

        var callRegex = language switch
        {
            "crystal" or "groovy" => CrystalGroovyBareCallRegex,
            "tcl" => TclCommandRegex,
            "prolog" or "ambiguous_pl" => PrologBareCallRegex,
            _ => null,
        };
        if (callRegex == null)
            return;

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
        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "function" || symbol.StartLine < 1 || symbol.StartLine > lines.Count)
                continue;

            var startLineIndex = symbol.StartLine - 1;
            var declarationMatch = TclProcRegex.Match(lines[startLineIndex]);
            if (!declarationMatch.Success
                || !TryFindTclBodyEnd(lines, startLineIndex, declarationMatch.Index + declarationMatch.Length, out var endLineIndex))
            {
                continue;
            }

            for (var lineIndex = startLineIndex; lineIndex <= endLineIndex; lineIndex++)
                containersByLine.TryAdd(lineIndex + 1, symbol);
        }
    }

    private static bool TryFindTclBodyEnd(
        IReadOnlyList<string> lines,
        int startLineIndex,
        int searchColumn,
        out int endLineIndex)
    {
        endLineIndex = startLineIndex;
        if (!TryFindNextNonWhitespace(lines[startLineIndex], searchColumn, out var argsColumn)
            || !TryFindTclWordEnd(lines, startLineIndex, argsColumn, out var argsEndLine, out var argsEndColumn)
            || !TryFindNextNonWhitespace(lines[argsEndLine], argsEndColumn + 1, out var bodyColumn)
            || lines[argsEndLine][bodyColumn] != '{'
            || !TryFindMatchingBrace(lines, argsEndLine, bodyColumn, out endLineIndex, out _))
        {
            return false;
        }

        return true;
    }

    private static bool TryFindTclWordEnd(
        IReadOnlyList<string> lines,
        int startLine,
        int startColumn,
        out int endLine,
        out int endColumn)
    {
        var line = lines[startLine];
        if (line[startColumn] == '{')
            return TryFindMatchingBrace(lines, startLine, startColumn, out endLine, out endColumn);

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

    private static bool TryFindMatchingBrace(
        IReadOnlyList<string> lines,
        int startLine,
        int startColumn,
        out int endLine,
        out int endColumn)
    {
        var depth = 0;
        for (var lineIndex = startLine; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            for (var column = lineIndex == startLine ? startColumn : 0; column < line.Length; column++)
            {
                if (line[column] == '\\')
                {
                    column++;
                    continue;
                }
                if (line[column] == '{')
                    depth++;
                else if (line[column] == '}' && --depth == 0)
                {
                    endLine = lineIndex;
                    endColumn = column;
                    return true;
                }
            }
        }

        endLine = -1;
        endColumn = -1;
        return false;
    }

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
