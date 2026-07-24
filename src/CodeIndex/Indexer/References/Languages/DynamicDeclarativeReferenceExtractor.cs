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
            IReadOnlyDictionary<int, SymbolRecord> containersByLine,
            IReadOnlyList<TclContainerScope> tclContainerScopes,
            IReadOnlyList<string>? tclCallLines)
        {
            CallableNames = callableNames;
            ContainersByLine = containersByLine;
            TclContainerScopes = tclContainerScopes;
            TclCallLines = tclCallLines;
        }

        public HashSet<string> CallableNames { get; }
        public IReadOnlyDictionary<int, SymbolRecord> ContainersByLine { get; }
        private IReadOnlyList<TclContainerScope> TclContainerScopes { get; }
        private IReadOnlyList<string>? TclCallLines { get; }

        public SymbolRecord? ResolveContainer(int lineNumber, int column, SymbolRecord? fallback)
        {
            for (var scopeIndex = TclContainerScopes.Count - 1; scopeIndex >= 0; scopeIndex--)
            {
                var scope = TclContainerScopes[scopeIndex];
                if (scope.Contains(lineNumber, column))
                    return scope.Symbol;
            }

            return ContainersByLine.TryGetValue(lineNumber, out var container) ? container : fallback;
        }

        public string GetCallScanLine(string language, int lineNumber, string preparedLine)
        {
            if (language == "tcl"
                && TclCallLines != null
                && lineNumber > 0
                && lineNumber <= TclCallLines.Count)
            {
                return TclCallLines[lineNumber - 1];
            }

            var isClauseContinuation = ContainersByLine.TryGetValue(lineNumber, out var container)
                && container.StartLine < lineNumber;
            return PreparePrologCallScanLine(language, preparedLine, isClauseContinuation);
        }
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
        @"(?:^|[;\[])\s*(?<name>[A-Za-z_:][\w:.-]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PrologBareCallRegex = new(
        @"(?:^|:-|-->|[,;])\s*(?:\\\+\s*)?(?<name>[a-z][A-Za-z0-9_]*)\b(?!\s*\()",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CrystalHeredocOpenerRegex = new(
        @"<<-\s*['""]?(?<delimiter>[A-Za-z_]\w*)['""]?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal readonly record struct TclContainerScope(
        SymbolRecord Symbol,
        int BodyStartLine,
        int BodyStartColumn,
        int BodyEndLine,
        int BodyEndColumn)
    {
        public bool Contains(int lineNumber, int column)
        {
            if (lineNumber < BodyStartLine || lineNumber > BodyEndLine)
                return false;
            if (BodyStartLine == BodyEndLine)
                return column > BodyStartColumn && column < BodyEndColumn;
            if (lineNumber == BodyStartLine)
                return column > BodyStartColumn;
            if (lineNumber == BodyEndLine)
                return column < BodyEndColumn;
            return true;
        }
    }

    private readonly record struct TclBraceEnd(
        int Line,
        int Column);

    private enum TclLexicalFrameKind
    {
        Script,
        Quote,
        BracedWord,
    }

    private sealed class TclLexicalFrame(
        TclLexicalFrameKind kind,
        char terminator = '\0')
    {
        public TclLexicalFrameKind Kind { get; } = kind;
        public char Terminator { get; } = terminator;
        public bool CommandStart { get; set; } = true;
        public bool WordStart { get; set; } = true;
        public int WordIndex { get; set; }
        public string? CommandName { get; set; }
        public string? LastBareWord { get; set; }
        public int LastBareWordIndex { get; set; } = -1;

        public void ResetCommand()
        {
            CommandStart = true;
            WordStart = true;
            WordIndex = 0;
            CommandName = null;
            LastBareWord = null;
            LastBareWordIndex = -1;
        }
    }

    public static string[] MaskNonCodeLines(string language, IReadOnlyList<string> lines)
    {
        if (language is not ("crystal" or "groovy" or "tcl" or "prolog" or "ambiguous_pl"))
            return lines as string[] ?? lines.ToArray();

        var result = new string[lines.Count];
        var insideBlockComment = false;
        char groovyTripleQuote = '\0';
        string? crystalHeredocDelimiter = null;
        var insideAmbiguousPerlPod = false;
        var insideSlashyLiteral = false;
        var insideGroovyDollarSlashyLiteral = false;
        char crystalMultilineQuote = '\0';
        char crystalPercentOpeningDelimiter = '\0';
        char crystalPercentClosingDelimiter = '\0';
        var crystalPercentDelimiterDepth = 0;

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
                if (crystalMultilineQuote != '\0')
                {
                    buffer[column] = ' ';
                    if (line[column] == '\\' && column + 1 < line.Length)
                    {
                        buffer[column + 1] = ' ';
                        column += 2;
                    }
                    else if (line[column] == crystalMultilineQuote)
                    {
                        crystalMultilineQuote = '\0';
                        column++;
                    }
                    else
                    {
                        column++;
                    }
                    continue;
                }

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

                if (insideGroovyDollarSlashyLiteral)
                {
                    buffer[column] = ' ';
                    if (column + 1 < line.Length
                        && line[column] == '/'
                        && line[column + 1] == '$')
                    {
                        buffer[column + 1] = ' ';
                        column += 2;
                        insideGroovyDollarSlashyLiteral = false;
                    }
                    else
                    {
                        column++;
                    }
                    continue;
                }

                if (insideSlashyLiteral)
                {
                    buffer[column] = ' ';
                    if (line[column] == '\\' && column + 1 < line.Length)
                    {
                        buffer[column + 1] = ' ';
                        column += 2;
                    }
                    else if (line[column] == '/')
                    {
                        column++;
                        insideSlashyLiteral = false;
                    }
                    else
                    {
                        column++;
                    }
                    continue;
                }

                if (crystalPercentClosingDelimiter != '\0')
                {
                    buffer[column] = ' ';
                    if (line[column] == '\\' && column + 1 < line.Length)
                    {
                        buffer[column + 1] = ' ';
                        column += 2;
                        continue;
                    }

                    if (crystalPercentOpeningDelimiter != crystalPercentClosingDelimiter
                        && line[column] == crystalPercentOpeningDelimiter)
                    {
                        crystalPercentDelimiterDepth++;
                    }
                    else if (line[column] == crystalPercentClosingDelimiter
                        && --crystalPercentDelimiterDepth == 0)
                    {
                        crystalPercentOpeningDelimiter = '\0';
                        crystalPercentClosingDelimiter = '\0';
                    }

                    column++;
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
                    if (language == "crystal"
                        && ch is '"' or '`'
                        && !HasClosingQuotedDelimiter(line, column, ch))
                    {
                        crystalMultilineQuote = ch;
                        column = line.Length;
                        continue;
                    }

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

                if (language == "crystal" && ch == '#')
                {
                    FillWithSpaces(buffer, column);
                    break;
                }

                if (language == "prolog" && ch == '%')
                {
                    FillWithSpaces(buffer, column);
                    break;
                }

                if (language == "ambiguous_pl" && ch == '#')
                {
                    if (IsPrologHashOperator(line, column))
                    {
                        column++;
                        continue;
                    }

                    FillWithSpaces(buffer, column);
                    break;
                }

                if (language == "ambiguous_pl" && ch == '%')
                {
                    if (IsPerlHashSigil(line, column))
                    {
                        column++;
                        continue;
                    }

                    FillWithSpaces(buffer, column);
                    break;
                }

                if (language == "groovy"
                    && ch == '$'
                    && column + 1 < line.Length
                    && line[column + 1] == '/')
                {
                    buffer[column] = ' ';
                    buffer[column + 1] = ' ';
                    column += 2;
                    insideGroovyDollarSlashyLiteral = true;
                    continue;
                }

                if (language == "crystal"
                    && TryBeginCrystalPercentLiteral(
                        line,
                        column,
                        out var percentOpeningDelimiter,
                        out var percentClosingDelimiter,
                        out var percentContentColumn))
                {
                    FillWithSpaces(buffer, column, percentContentColumn);
                    crystalPercentOpeningDelimiter = percentOpeningDelimiter;
                    crystalPercentClosingDelimiter = percentClosingDelimiter;
                    crystalPercentDelimiterDepth = 1;
                    column = percentContentColumn;
                    continue;
                }

                if (language is "groovy" or "crystal"
                    && ch == '/'
                    && IsLikelySlashyLiteralStart(line, column))
                {
                    buffer[column] = ' ';
                    column++;
                    insideSlashyLiteral = true;
                    continue;
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
        IReadOnlyList<string> structuralLines,
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
        var tclContainerScopes = new List<TclContainerScope>();
        string[]? tclCallLines = null;
        if (language == "tcl")
        {
            var tclScriptBodyOpenings = new HashSet<long>();
            var tclBraceEnds = BuildTclBraceEndPositions(structuralLines);
            AddTclContainers(
                structuralLines,
                symbols,
                tclBraceEnds,
                tclContainerScopes,
                tclScriptBodyOpenings);
            tclCallLines = BuildTclCallLines(
                structuralLines,
                tclBraceEnds,
                tclScriptBodyOpenings);
        }
        else if (language is "prolog" or "ambiguous_pl")
            AddPrologContainers(preparedLines, symbols, containersByLine);

        return new ExtractionState(
            callableNames,
            containersByLine,
            tclContainerScopes,
            tclCallLines);
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

    private static bool HasClosingQuotedDelimiter(
        string line,
        int startColumn,
        char delimiter)
    {
        for (var column = startColumn + 1; column < line.Length; column++)
        {
            if (line[column] == '\\')
            {
                column++;
                continue;
            }

            if (line[column] == delimiter)
                return true;
        }

        return false;
    }

    private static void FillWithSpaces(char[] buffer, int startColumn)
    {
        for (var column = startColumn; column < buffer.Length; column++)
            buffer[column] = ' ';
    }

    private static void FillWithSpaces(char[] buffer, int startColumn, int endColumn)
    {
        for (var column = startColumn; column < endColumn && column < buffer.Length; column++)
            buffer[column] = ' ';
    }

    private static bool IsPrologHashOperator(string line, int column)
    {
        if (column + 1 >= line.Length)
            return false;

        return line[column + 1] is '=' or '<' or '>' or '\\' or '/' or '#';
    }

    private static bool IsPerlHashSigil(string line, int column)
    {
        if (column + 1 >= line.Length
            || (line[column + 1] != '{'
                && line[column + 1] != '_'
                && !char.IsLetter(line[column + 1])))
        {
            return false;
        }

        var previousColumn = column - 1;
        while (previousColumn >= 0 && char.IsWhiteSpace(line[previousColumn]))
            previousColumn--;
        if (previousColumn >= 0)
            return true;

        var tokenEnd = column + 1;
        if (line[tokenEnd] == '{')
            return true;
        while (tokenEnd < line.Length
            && (char.IsLetterOrDigit(line[tokenEnd]) || line[tokenEnd] == '_'))
        {
            tokenEnd++;
        }
        while (tokenEnd < line.Length && char.IsWhiteSpace(line[tokenEnd]))
            tokenEnd++;

        return tokenEnd >= line.Length || line[tokenEnd] is '=' or ';' or '{' or '[' or ',';
    }

    private static bool IsLikelySlashyLiteralStart(string line, int column)
    {
        if (column + 1 >= line.Length || line[column + 1] is '/' or '*')
            return false;

        var previousColumn = column - 1;
        while (previousColumn >= 0 && char.IsWhiteSpace(line[previousColumn]))
            previousColumn--;

        if (previousColumn < 0)
            return true;

        if (line[previousColumn] is '=' or '(' or '[' or '{' or ',' or ':' or ';'
            or '!' or '&' or '|' or '?' or '+' or '-' or '*' or '%' or '~')
        {
            return true;
        }

        var tokenEnd = previousColumn + 1;
        while (previousColumn >= 0
            && (char.IsLetterOrDigit(line[previousColumn]) || line[previousColumn] == '_'))
        {
            previousColumn--;
        }

        var token = line.AsSpan(previousColumn + 1, tokenEnd - previousColumn - 1);
        return token.SequenceEqual("return")
            || token.SequenceEqual("case")
            || token.SequenceEqual("throw")
            || token.SequenceEqual("assert")
            || token.SequenceEqual("in")
            || token.SequenceEqual("when");
    }

    private static bool TryBeginCrystalPercentLiteral(
        string line,
        int column,
        out char openingDelimiter,
        out char closingDelimiter,
        out int contentColumn)
    {
        openingDelimiter = '\0';
        closingDelimiter = '\0';
        contentColumn = column;
        if (line[column] != '%' || column + 1 >= line.Length)
            return false;

        var delimiterColumn = column + 1;
        var hasTypePrefix = line[delimiterColumn] is 'q' or 'Q' or 'w' or 'W' or 'i' or 'I' or 'x' or 'r';
        if (hasTypePrefix)
            delimiterColumn++;
        if (delimiterColumn >= line.Length
            || char.IsLetterOrDigit(line[delimiterColumn])
            || char.IsWhiteSpace(line[delimiterColumn])
            || (!hasTypePrefix && line[delimiterColumn] is not ('(' or '[' or '{' or '<')))
        {
            return false;
        }

        openingDelimiter = line[delimiterColumn];
        closingDelimiter = openingDelimiter switch
        {
            '(' => ')',
            '[' => ']',
            '{' => '}',
            '<' => '>',
            _ => openingDelimiter,
        };
        contentColumn = delimiterColumn + 1;
        return true;
    }

    private static string PreparePrologCallScanLine(
        string language,
        string line,
        bool isClauseContinuation)
    {
        if (language is not ("prolog" or "ambiguous_pl") || isClauseContinuation)
            return line;

        var firstColumn = 0;
        while (firstColumn < line.Length && char.IsWhiteSpace(line[firstColumn]))
            firstColumn++;
        if (firstColumn >= line.Length
            || !char.IsLower(line[firstColumn])
            || line.AsSpan(firstColumn).StartsWith(":-", StringComparison.Ordinal))
        {
            return line;
        }

        var parenthesisDepth = 0;
        for (var column = firstColumn; column < line.Length; column++)
        {
            var ch = line[column];
            if (ch is '\'' or '"')
            {
                column = SkipQuotedToken(line, column, ch) - 1;
                continue;
            }
            if (ch == '(')
            {
                parenthesisDepth++;
                continue;
            }
            if (ch == ')' && parenthesisDepth > 0)
            {
                parenthesisDepth--;
                continue;
            }
            if (parenthesisDepth != 0)
                continue;

            var separatorLength = line.AsSpan(column).StartsWith("-->", StringComparison.Ordinal)
                ? 3
                : line.AsSpan(column).StartsWith(":-", StringComparison.Ordinal)
                    ? 2
                    : 0;
            if (separatorLength > 0)
            {
                var masked = line.ToCharArray();
                FillWithSpaces(masked, 0, column);
                return new string(masked);
            }

            if (IsPrologClauseTerminator(line, column)
                && IsOnlyWhitespaceAfter(line, column + 1))
                return new string(' ', line.Length);
        }

        return line;
    }

    private static bool IsOnlyWhitespaceAfter(string line, int startColumn)
    {
        for (var column = startColumn; column < line.Length; column++)
        {
            if (!char.IsWhiteSpace(line[column]))
                return false;
        }
        return true;
    }

    internal static bool IsPrologClauseTerminator(string line, int column)
    {
        if (column < 0 || column >= line.Length || line[column] != '.')
            return false;

        var previous = column > 0 ? line[column - 1] : '\0';
        var next = column + 1 < line.Length ? line[column + 1] : '\0';
        if (previous == '.' || next == '.')
            return false;
        if (char.IsDigit(previous) && char.IsDigit(next))
            return false;
        return IsOnlyWhitespaceAfter(line, column + 1);
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
        IReadOnlyDictionary<long, TclBraceEnd> braceEnds,
        List<TclContainerScope> scopes,
        HashSet<long> scriptBodyOpenings)
    {
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
                    out var bodyStartLineIndex,
                    out var bodyStartColumn,
                    out var bodyEnd))
            {
                continue;
            }

            scopes.Add(new TclContainerScope(
                symbol,
                bodyStartLineIndex + 1,
                bodyStartColumn,
                bodyEnd.Line + 1,
                bodyEnd.Column));
            scriptBodyOpenings.Add(GetTclPositionKey(bodyStartLineIndex, bodyStartColumn));
        }

        scopes.Sort(static (left, right) =>
        {
            var startComparison = left.BodyStartLine.CompareTo(right.BodyStartLine);
            return startComparison != 0
                ? startComparison
                : left.BodyStartColumn.CompareTo(right.BodyStartColumn);
        });
    }

    private static bool TryFindTclBodyEnd(
        IReadOnlyList<string> lines,
        IReadOnlyDictionary<long, TclBraceEnd> braceEnds,
        int startLineIndex,
        int searchColumn,
        out int bodyStartLineIndex,
        out int bodyStartColumn,
        out TclBraceEnd bodyEnd)
    {
        bodyStartLineIndex = startLineIndex;
        bodyStartColumn = -1;
        bodyEnd = default;
        if (!TryFindNextNonWhitespace(lines[startLineIndex], searchColumn, out var argsColumn)
            || !TryFindTclWordEnd(
                lines,
                braceEnds,
                startLineIndex,
                argsColumn,
                out var argsEndLine,
                out var argsEndColumn)
            || !TryFindNextNonWhitespace(
                lines,
                argsEndLine,
                argsEndColumn + 1,
                out bodyStartLineIndex,
                out bodyStartColumn)
            || lines[bodyStartLineIndex][bodyStartColumn] != '{'
            || !braceEnds.TryGetValue(
                GetTclPositionKey(bodyStartLineIndex, bodyStartColumn),
                out bodyEnd))
        {
            return false;
        }

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

    private static bool TryFindNextNonWhitespace(
        IReadOnlyList<string> lines,
        int startLine,
        int startColumn,
        out int foundLine,
        out int foundColumn)
    {
        for (var lineIndex = startLine; lineIndex < lines.Count; lineIndex++)
        {
            var column = lineIndex == startLine ? startColumn : 0;
            if (TryFindNextNonWhitespace(lines[lineIndex], column, out foundColumn))
            {
                foundLine = lineIndex;
                return true;
            }
        }

        foundLine = -1;
        foundColumn = -1;
        return false;
    }

    private static Dictionary<long, TclBraceEnd> BuildTclBraceEndPositions(IReadOnlyList<string> lines)
    {
        var result = new Dictionary<long, TclBraceEnd>();
        var openings = new Stack<(int Line, int Column)>();
        var commandStart = true;
        var wordStart = true;
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            for (var column = 0; column < line.Length; column++)
            {
                var ch = line[column];
                if (openings.Count > 0)
                {
                    if (ch == '\\')
                    {
                        column++;
                        continue;
                    }
                    if (ch == '{')
                    {
                        openings.Push((lineIndex, column));
                    }
                    else if (ch == '}')
                    {
                        var opening = openings.Pop();
                        result[GetTclPositionKey(opening.Line, opening.Column)] =
                            new TclBraceEnd(lineIndex, column);
                    }
                    continue;
                }

                if (ch == '\\')
                {
                    column++;
                    commandStart = false;
                    wordStart = false;
                    continue;
                }

                if (ch == '"')
                {
                    column = SkipQuotedToken(line, column, ch) - 1;
                    commandStart = false;
                    wordStart = false;
                    continue;
                }

                if (ch == '#' && commandStart)
                    break;
                if (ch == ';' || ch == '[')
                {
                    commandStart = true;
                    wordStart = true;
                    continue;
                }
                if (char.IsWhiteSpace(ch))
                {
                    wordStart = true;
                    continue;
                }
                if (ch == '{' && wordStart)
                {
                    openings.Push((lineIndex, column));
                    commandStart = false;
                    wordStart = false;
                    continue;
                }

                commandStart = false;
                wordStart = false;
            }

            if (openings.Count == 0)
            {
                commandStart = true;
                wordStart = true;
            }
        }

        return result;
    }

    private static string[] BuildTclCallLines(
        IReadOnlyList<string> lines,
        IReadOnlyDictionary<long, TclBraceEnd> braceEnds,
        IReadOnlySet<long> scriptBodyOpenings)
    {
        var result = new string[lines.Count];
        var frames = new Stack<TclLexicalFrame>();
        frames.Push(new TclLexicalFrame(TclLexicalFrameKind.Script));

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            var buffer = line.ToCharArray();
            var lineContinued = false;
            var suppressLeadingContinuedWord = frames.Peek().Kind != TclLexicalFrameKind.Script
                || !frames.Peek().CommandStart;
            for (var column = 0; column < line.Length;)
            {
                var frame = frames.Peek();
                var ch = line[column];
                if (frame.Kind == TclLexicalFrameKind.BracedWord)
                {
                    buffer[column] = ' ';
                    if (ch == '\\' && column + 1 < line.Length)
                    {
                        buffer[column + 1] = ' ';
                        column += 2;
                    }
                    else if (ch == '{')
                    {
                        frames.Push(new TclLexicalFrame(TclLexicalFrameKind.BracedWord, '}'));
                        column++;
                    }
                    else if (ch == frame.Terminator)
                    {
                        frames.Pop();
                        column++;
                    }
                    else
                    {
                        column++;
                    }
                    continue;
                }

                if (frame.Kind == TclLexicalFrameKind.Quote)
                {
                    buffer[column] = ' ';
                    if (ch == '\\' && column + 1 < line.Length)
                    {
                        buffer[column + 1] = ' ';
                        column += 2;
                    }
                    else if (ch == frame.Terminator)
                    {
                        frames.Pop();
                        column++;
                    }
                    else if (ch == '[')
                    {
                        buffer[column] = '[';
                        frames.Push(new TclLexicalFrame(TclLexicalFrameKind.Script, ']'));
                        suppressLeadingContinuedWord = false;
                        column++;
                    }
                    else
                    {
                        column++;
                    }
                    continue;
                }

                if (frame.Terminator != '\0' && ch == frame.Terminator)
                {
                    frames.Pop();
                    buffer[column] = frame.Terminator == '}' ? ' ' : ch;
                    column++;
                    continue;
                }
                if (ch == '\\')
                {
                    if (frame.WordStart)
                        frame.WordIndex++;
                    lineContinued = column + 1 >= line.Length;
                    column += Math.Min(2, line.Length - column);
                    frame.CommandStart = false;
                    frame.WordStart = false;
                    continue;
                }
                if (ch == '#' && frame.CommandStart)
                {
                    FillWithSpaces(buffer, column);
                    break;
                }
                if (ch == '"')
                {
                    if (frame.WordStart)
                        frame.WordIndex++;
                    buffer[column] = ' ';
                    frames.Push(new TclLexicalFrame(TclLexicalFrameKind.Quote, '"'));
                    frame.CommandStart = false;
                    frame.WordStart = false;
                    column++;
                    continue;
                }
                if (ch == '[')
                {
                    if (frame.WordStart)
                        frame.WordIndex++;
                    frames.Push(new TclLexicalFrame(TclLexicalFrameKind.Script, ']'));
                    frame.CommandStart = false;
                    frame.WordStart = false;
                    suppressLeadingContinuedWord = false;
                    column++;
                    continue;
                }
                if (ch == '{' && frame.WordStart)
                {
                    var wordIndex = frame.WordIndex++;
                    var positionKey = GetTclPositionKey(lineIndex, column);
                    var isScriptArgument =
                        scriptBodyOpenings.Contains(positionKey)
                        || IsTclScriptArgument(
                            frame,
                            wordIndex,
                            lines,
                            braceEnds.TryGetValue(positionKey, out var braceEnd)
                                ? braceEnd
                                : null);
                    if (isScriptArgument)
                    {
                        buffer[column] = ';';
                        frames.Push(new TclLexicalFrame(TclLexicalFrameKind.Script, '}'));
                        suppressLeadingContinuedWord = false;
                    }
                    else
                    {
                        buffer[column] = ' ';
                        frames.Push(new TclLexicalFrame(TclLexicalFrameKind.BracedWord, '}'));
                    }
                    frame.CommandStart = false;
                    frame.WordStart = false;
                    column++;
                    continue;
                }
                if (ch == ';')
                {
                    frame.ResetCommand();
                    suppressLeadingContinuedWord = false;
                    column++;
                    continue;
                }
                if (char.IsWhiteSpace(ch))
                {
                    frame.WordStart = true;
                    column++;
                    continue;
                }

                if (frame.WordStart)
                {
                    var wordIndex = frame.WordIndex++;
                    var token = ReadTclBareWord(line, column);
                    if (wordIndex == 0)
                        frame.CommandName = token;
                    if (token.Length > 0)
                    {
                        frame.LastBareWord = token;
                        frame.LastBareWordIndex = wordIndex;
                    }

                    if (suppressLeadingContinuedWord)
                    {
                        FillWithSpaces(buffer, column, column + token.Length);
                        suppressLeadingContinuedWord = false;
                    }
                }

                frame.CommandStart = false;
                frame.WordStart = false;
                column++;
            }

            result[lineIndex] = new string(buffer);
            if (!lineContinued && frames.Peek().Kind == TclLexicalFrameKind.Script)
                frames.Peek().ResetCommand();
        }

        return result;
    }

    private static string ReadTclBareWord(string line, int startColumn)
    {
        var endColumn = startColumn;
        while (endColumn < line.Length
            && (char.IsLetterOrDigit(line[endColumn])
                || line[endColumn] is '_' or ':' or '.' or '-'))
        {
            endColumn++;
        }

        return endColumn == startColumn
            ? string.Empty
            : line[startColumn..endColumn];
    }

    private static bool IsTclScriptArgument(
        TclLexicalFrame frame,
        int wordIndex,
        IReadOnlyList<string> lines,
        TclBraceEnd? braceEnd)
    {
        var isLastCommandWord = braceEnd is { } end
            && IsTclLastCommandWord(lines, end);
        return frame.CommandName switch
        {
            "if" => wordIndex == 2
                || (frame.LastBareWord == "then"
                    && wordIndex == frame.LastBareWordIndex + 1)
                || (frame.LastBareWord == "elseif"
                    && wordIndex == frame.LastBareWordIndex + 2)
                || (frame.LastBareWord == "else"
                    && wordIndex == frame.LastBareWordIndex + 1),
            "foreach" or "lmap" => wordIndex >= 3
                && wordIndex % 2 == 1
                && isLastCommandWord,
            "while" => wordIndex == 2,
            "catch" => wordIndex == 1,
            "for" => wordIndex is 1 or 3 or 4,
            "eval" => wordIndex >= 1,
            "after" => wordIndex == 2,
            "try" => wordIndex == 1,
            "namespace" => wordIndex == 3,
            "dict" => frame.LastBareWord == "for"
                && wordIndex == frame.LastBareWordIndex + 3,
            _ => false,
        };
    }

    private static bool IsTclLastCommandWord(
        IReadOnlyList<string> lines,
        TclBraceEnd braceEnd)
    {
        var line = lines[braceEnd.Line];
        for (var column = braceEnd.Column + 1; column < line.Length; column++)
        {
            if (char.IsWhiteSpace(line[column]))
                continue;
            return line[column] is ';' or ']' or '}';
        }

        return true;
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
                if (IsPrologClauseTerminator(line, column))
                    return lineIndex;
            }
        }

        return startLineIndex;
    }
}
