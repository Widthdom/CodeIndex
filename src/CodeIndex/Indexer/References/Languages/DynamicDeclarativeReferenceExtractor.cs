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
            HashSet<DeclarationPosition> declarationPositions,
            IReadOnlyDictionary<int, SymbolRecord> containersByLine,
            IReadOnlyList<TclContainerScope> tclContainerScopes,
            IReadOnlyDictionary<int, List<SymbolRecord>> prologContainersByLine,
            IReadOnlyList<string>? tclCallLines,
            IReadOnlyDictionary<int, IReadOnlyList<PrologGoalCall>>? prologGoalCallsByLine)
        {
            CallableNames = callableNames;
            DeclarationPositions = declarationPositions;
            ContainersByLine = containersByLine;
            TclContainerScopes = tclContainerScopes;
            PrologContainersByLine = prologContainersByLine;
            TclCallLines = tclCallLines;
            PrologGoalCallsByLine = prologGoalCallsByLine;
        }

        public HashSet<string> CallableNames { get; }
        private HashSet<DeclarationPosition> DeclarationPositions { get; }
        public IReadOnlyDictionary<int, SymbolRecord> ContainersByLine { get; }
        private IReadOnlyList<TclContainerScope> TclContainerScopes { get; }
        private IReadOnlyDictionary<int, List<SymbolRecord>> PrologContainersByLine { get; }
        private IReadOnlyList<string>? TclCallLines { get; }
        private IReadOnlyDictionary<int, IReadOnlyList<PrologGoalCall>>? PrologGoalCallsByLine { get; }

        public SymbolRecord? ResolveContainer(int lineNumber, int column, SymbolRecord? fallback)
        {
            for (var scopeIndex = TclContainerScopes.Count - 1; scopeIndex >= 0; scopeIndex--)
            {
                var scope = TclContainerScopes[scopeIndex];
                if (scope.Contains(lineNumber, column))
                    return scope.Symbol;
            }

            if (PrologContainersByLine.TryGetValue(lineNumber, out var prologContainers))
            {
                for (var containerIndex = prologContainers.Count - 1;
                    containerIndex >= 0;
                    containerIndex--)
                {
                    var prologContainer = prologContainers[containerIndex];
                    if (prologContainer.StartColumn is { } startColumn && startColumn < column)
                        return prologContainer;
                }
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

        public IReadOnlyList<PrologGoalCall> GetPrologGoalCalls(int lineNumber)
        {
            if (PrologGoalCallsByLine != null
                && PrologGoalCallsByLine.TryGetValue(lineNumber, out var calls))
            {
                return calls;
            }

            return Array.Empty<PrologGoalCall>();
        }

        public bool HasPrologContainer(int lineNumber) =>
            ContainersByLine.ContainsKey(lineNumber)
            || PrologContainersByLine.ContainsKey(lineNumber);

        public bool IsDeclarationAt(int lineNumber, int column, string name) =>
            DeclarationPositions.Contains(new DeclarationPosition(lineNumber, column, name));
    }

    private static readonly Regex TclProcRegex = new(
        @"^\s*proc\s+[A-Za-z_:][\w:.-]*\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PrologHeadRegex = new(
        @"^\s*(?<name>[a-z][A-Za-z0-9_]*)\s*(?:\([^\r\n]*\))?\s*(?::-|-->|\.(?=\s*(?:$|[a-z][A-Za-z0-9_]*(?:\s*\([^)]*\))?\s*(?::-|-->|\.))))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PrologMultilineHeadRegex = new(
        @"^\s*(?<name>[a-z][A-Za-z0-9_]*)\s*\(",
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
        @"(?:^|[;={]|\b(?:then|do)\b|&&|\|\|)\s*(?:return\s+)?(?<name>[A-Za-z_]\w*[?!]?)(?![\w?!])(?!\s*(?::|\(|(?:<<|>>|[+\-*/%&|^])?=))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CrystalSuffixedParenthesizedCallRegex = new(
        @"(?<![\w])(?<name>[A-Za-z_]\w*[?!])\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GroovyBareCallRegex = new(
        @"(?:^|[;={])\s*(?:return\s+)?(?<name>[A-Za-z_]\w*)\b(?!\s*(?::|\(|(?:<<|>>|[+\-*/%&|^])?=))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GroovyConstructorDeclarationRegex = new(
        @"(?:^|[;{])\s*(?:@[A-Za-z_$][\w.$]*(?:\s*\([^)\r\n]*\))?\s+)*(?:(?:public|protected|private)\s+)*(?<name>[A-Z]\w*)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GroovyMethodDeclarationRegex = new(
        @"(?:^|[;{])\s*(?:@[A-Za-z_$][\w.$]*(?:\s*\([^)\r\n]*\))?\s+)*(?:(?:public|private|protected|static|final|abstract|synchronized|native|strictfp)\s+)*(?:def|void|boolean|byte|char|short|int|long|float|double|BigDecimal|BigInteger|String|[A-Za-z_$][\w.$]*(?:\s*<[^(){}\r\n]+>)?(?:\s*\[\])*)\s+(?<name>[A-Za-z_]\w*)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CrystalMethodDeclarationRegex = new(
        @"(?:^|;)\s*(?:(?:private|protected|abstract)\s+)*def\s+(?:self\.)?(?<name>[A-Za-z_]\w*[?!]?)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TclCommandRegex = new(
        @"(?:^|[;\[])\s*(?<name>[A-Za-z_:][\w:.-]*)",
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
        SwitchTable,
    }

    internal readonly record struct PrologGoalCall(string Name, int Column);

    internal readonly record struct DeclarationPosition(int Line, int Column, string Name);

    private enum PrologLexicalFrameKind
    {
        PredicateArguments,
        GoalGroup,
        MetaArguments,
        TermGroup,
    }

    private sealed class PrologLexicalFrame(
        PrologLexicalFrameKind kind,
        IReadOnlySet<int>? goalArgumentIndices = null,
        char terminator = ')')
    {
        public PrologLexicalFrameKind Kind { get; } = kind;
        public IReadOnlySet<int>? GoalArgumentIndices { get; } = goalArgumentIndices;
        public char Terminator { get; } = terminator;
        public int ArgumentIndex { get; set; }
        public bool CurrentArgumentIsGoal =>
            GoalArgumentIndices?.Contains(ArgumentIndex) == true;
    }

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<int>> PrologMetaGoalArguments =
        new Dictionary<string, IReadOnlySet<int>>(StringComparer.Ordinal)
        {
            ["call"] = new HashSet<int> { 0 },
            ["once"] = new HashSet<int> { 0 },
            ["ignore"] = new HashSet<int> { 0 },
            ["not"] = new HashSet<int> { 0 },
            ["catch"] = new HashSet<int> { 0, 2 },
            ["call_cleanup"] = new HashSet<int> { 0, 1 },
            ["setup_call_cleanup"] = new HashSet<int> { 0, 1, 2 },
            ["findall"] = new HashSet<int> { 1 },
            ["bagof"] = new HashSet<int> { 1 },
            ["setof"] = new HashSet<int> { 1 },
            ["forall"] = new HashSet<int> { 0, 1 },
            ["phrase"] = new HashSet<int> { 0 },
            ["maplist"] = new HashSet<int> { 0 },
            ["include"] = new HashSet<int> { 0 },
            ["exclude"] = new HashSet<int> { 0 },
            ["foldl"] = new HashSet<int> { 0 },
            ["convlist"] = new HashSet<int> { 0 },
        };

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
        public int SwitchStringWordIndex { get; set; } = -1;
        public bool SwitchOptionsEnded { get; set; }
        public bool SwitchOptionValuePending { get; set; }
        public int TryClauseWordIndex { get; set; } = 2;
        public int TryScriptWordIndex { get; set; } = 1;

        public void ResetCommand()
        {
            CommandStart = true;
            WordStart = true;
            WordIndex = 0;
            CommandName = null;
            LastBareWord = null;
            LastBareWordIndex = -1;
            SwitchStringWordIndex = -1;
            SwitchOptionsEnded = false;
            SwitchOptionValuePending = false;
            TryClauseWordIndex = 2;
            TryScriptWordIndex = 1;
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
        var tclCommentContinued = false;

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            if (language == "tcl" && tclCommentContinued)
            {
                result[lineIndex] = new string(' ', line.Length);
                tclCommentContinued = HasTclEscapedNewline(line);
                continue;
            }
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
            var tclCommentStart = language == "tcl"
                ? FindTclCommentStart(line)
                : -1;
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
                if (language == "tcl"
                    && ch == '#'
                    && column == tclCommentStart)
                {
                    FillWithSpaces(buffer, column);
                    tclCommentContinued = HasTclEscapedNewline(line);
                    break;
                }
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
                        FillWithSpaces(buffer, column);
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
                    if (IsPerlHashSigil(line, column)
                        || IsLikelyPerlModuloOperator(line, column))
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

    public static string[] MaskTclContinuedCommentLines(
        IReadOnlyList<string> rawLines,
        IReadOnlyList<string> maskedLines)
    {
        if (rawLines.Count == 0)
            return maskedLines as string[] ?? maskedLines.ToArray();

        var commentColumns = new Dictionary<int, int>();
        _ = BuildTclCallLines(
            maskedLines,
            BuildTclBraceEndPositions(maskedLines),
            new HashSet<long>(),
            commentColumns);
        if (commentColumns.Count == 0)
            return maskedLines as string[] ?? maskedLines.ToArray();

        var result = maskedLines.ToArray();
        foreach (var (lineIndex, commentColumn) in commentColumns)
        {
            var buffer = result[lineIndex].ToCharArray();
            FillWithSpaces(buffer, commentColumn);
            result[lineIndex] = new string(buffer);
        }

        return result;
    }

    public static string[] MaskTclNonScriptLines(IReadOnlyList<string> maskedLines) =>
        BuildTclCallLines(
            maskedLines,
            BuildTclBraceEndPositions(maskedLines),
            new HashSet<long>());

    public static ExtractionState? CreateState(
        string language,
        IReadOnlyList<string> preparedLines,
        IReadOnlyList<string> structuralLines,
        IReadOnlyList<SymbolRecord> symbols)
    {
        if (language is not ("crystal" or "groovy" or "tcl" or "prolog" or "ambiguous_pl"))
            return null;

        var callableNames = new HashSet<string>(StringComparer.Ordinal);
        var declarationPositions = new HashSet<DeclarationPosition>();
        foreach (var symbol in symbols)
        {
            if (symbol.Kind is "function" or "lambda" or "operator")
            {
                callableNames.Add(symbol.Name);
                if (symbol.StartColumn is { } startColumn)
                {
                    declarationPositions.Add(new DeclarationPosition(
                        symbol.StartLine,
                        startColumn,
                        symbol.Name));
                }
            }
        }

        var containersByLine = new Dictionary<int, SymbolRecord>();
        var tclContainerScopes = new List<TclContainerScope>();
        var prologContainersByLine = new Dictionary<int, List<SymbolRecord>>();
        string[]? tclCallLines = null;
        IReadOnlyDictionary<int, IReadOnlyList<PrologGoalCall>>? prologGoalCallsByLine = null;
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
        {
            AddPrologContainers(
                preparedLines,
                symbols,
                containersByLine,
                prologContainersByLine);
            prologGoalCallsByLine = BuildPrologGoalCalls(
                preparedLines,
                containersByLine,
                callableNames);
        }

        return new ExtractionState(
            callableNames,
            declarationPositions,
            containersByLine,
            tclContainerScopes,
            prologContainersByLine,
            tclCallLines,
            prologGoalCallsByLine);
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

        if (language is "prolog" or "ambiguous_pl")
        {
            foreach (var call in state.GetPrologGoalCalls(lineNumber))
            {
                if (!state.CallableNames.Contains(call.Name))
                    continue;

                var prologContainer = state.ResolveContainer(lineNumber, call.Column, fallback: null);
                if (prologContainer != null
                    && !string.Equals(prologContainer.Name, call.Name, StringComparison.Ordinal))
                {
                    ReferenceExtractor.AddReference(
                        references,
                        seen,
                        fileId,
                        call.Name,
                        call.Column,
                        "call",
                        context,
                        lineNumber,
                        prologContainer,
                        language);
                    continue;
                }

                addCallLikeReference(call.Name, call.Column);
            }

            return;
        }

        var callRegex = language switch
        {
            "crystal" => CrystalBareCallRegex,
            "groovy" => GroovyBareCallRegex,
            "tcl" => TclCommandRegex,
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

            addCallLikeReference(nameGroup.Value, nameGroup.Index);
        }

    }

    public static bool ShouldSuppressGenericCall(
        string language,
        string preparedLine,
        string name,
        int callIndex,
        int lineNumber,
        ExtractionState? state,
        SymbolRecord? container)
    {
        if (language == "ambiguous_pl")
            return state?.HasPrologContainer(lineNumber) == true;
        if (state?.IsDeclarationAt(lineNumber, callIndex, name) == true)
            return true;
        if (language == "crystal")
            return MatchesDeclarationAt(CrystalMethodDeclarationRegex, preparedLine, name, callIndex);
        if (language != "groovy")
            return false;
        if (name is "super" or "synchronized" or "this")
            return true;

        if (MatchesDeclarationAt(GroovyMethodDeclarationRegex, preparedLine, name, callIndex))
            return true;
        return container?.Kind == "class"
            && string.Equals(container.Name, name, StringComparison.Ordinal)
            && MatchesDeclarationAt(GroovyConstructorDeclarationRegex, preparedLine, name, callIndex);
    }

    private static bool MatchesDeclarationAt(
        Regex regex,
        string line,
        string name,
        int callIndex)
    {
        foreach (Match declaration in BoundedRegex.EnumerateMatches(regex, line))
        {
            var nameGroup = declaration.Groups["name"];
            if (nameGroup.Index == callIndex
                && string.Equals(nameGroup.Value, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
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

        if (previousColumn < 0)
            return tokenEnd < line.Length && line[tokenEnd] is '=' or '{' or '[';

        var prefix = line.AsSpan(0, previousColumn + 1);
        if (prefix.Contains(":-", StringComparison.Ordinal)
            || prefix.Contains("-->", StringComparison.Ordinal)
            || line[previousColumn] == '.')
        {
            return false;
        }

        if (line[previousColumn] is '=' or '(' or '[' or '{' or ',' or '\\')
            return true;
        if (line[previousColumn] == '>'
            && previousColumn > 0
            && line[previousColumn - 1] == '=')
        {
            return true;
        }

        var previousTokenStart = previousColumn;
        while (previousTokenStart >= 0
            && (char.IsLetterOrDigit(line[previousTokenStart])
                || line[previousTokenStart] == '_'))
        {
            previousTokenStart--;
        }
        var previousToken = line.AsSpan(previousTokenStart + 1, previousColumn - previousTokenStart);
        return previousToken.Equals("my", StringComparison.Ordinal)
            || previousToken.Equals("our", StringComparison.Ordinal)
            || previousToken.Equals("state", StringComparison.Ordinal)
            || previousToken.Equals("local", StringComparison.Ordinal)
            || previousToken.Equals("return", StringComparison.Ordinal)
            || previousToken.Equals("keys", StringComparison.Ordinal)
            || previousToken.Equals("values", StringComparison.Ordinal)
            || previousToken.Equals("each", StringComparison.Ordinal)
            || previousToken.Equals("delete", StringComparison.Ordinal)
            || previousToken.Equals("exists", StringComparison.Ordinal)
            || previousToken.Equals("defined", StringComparison.Ordinal)
            || previousToken.Equals("scalar", StringComparison.Ordinal);
    }

    private static bool IsLikelyPerlModuloOperator(string line, int column)
    {
        var prefix = line.AsSpan(0, column);
        var hasPerlContext = prefix.Contains('$')
            || prefix.Contains('@')
            || StartsWithPerlStatementKeyword(prefix);
        if (!hasPerlContext)
            return false;

        var previousColumn = column - 1;
        while (previousColumn >= 0 && char.IsWhiteSpace(line[previousColumn]))
            previousColumn--;
        if (previousColumn < 0)
            return false;

        if (column + 1 < line.Length && line[column + 1] == '=')
            return true;

        var nextColumn = column + 1;
        while (nextColumn < line.Length && char.IsWhiteSpace(line[nextColumn]))
            nextColumn++;
        if (nextColumn >= line.Length)
            return false;

        var previous = line[previousColumn];
        var next = line[nextColumn];
        return (char.IsLetterOrDigit(previous) || previous is '_' or ')' or ']' or '}')
            && (char.IsLetterOrDigit(next) || next is '_' or '$' or '@' or '(' or '+' or '-');
    }

    private static bool StartsWithPerlStatementKeyword(ReadOnlySpan<char> prefix)
    {
        prefix = prefix.TrimStart();
        foreach (var keyword in new[] { "my", "our", "state", "local", "return" })
        {
            if (!prefix.StartsWith(keyword, StringComparison.Ordinal))
                continue;
            if (prefix.Length == keyword.Length || char.IsWhiteSpace(prefix[keyword.Length]))
                return true;
        }

        return false;
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

        var masked = line.ToCharArray();
        var clauseStartColumn = 0;
        var changed = false;
        while (TryFindNextPrologClauseStart(line, clauseStartColumn, out var headStartColumn)
            && TryFindPrologHeadBoundary(
                line,
                headStartColumn,
                out var bodyStartColumn,
                out var clauseEndColumn))
        {
            if (bodyStartColumn >= 0)
            {
                FillWithSpaces(masked, headStartColumn, bodyStartColumn);
                changed = true;
                if (clauseEndColumn < 0)
                    break;
            }
            else
            {
                FillWithSpaces(masked, headStartColumn, clauseEndColumn + 1);
                changed = true;
            }

            clauseStartColumn = clauseEndColumn + 1;
        }

        return changed ? new string(masked) : line;
    }

    private static bool TryFindNextPrologClauseStart(
        string line,
        int searchColumn,
        out int clauseStartColumn)
    {
        for (var column = Math.Max(0, searchColumn); column < line.Length; column++)
        {
            if (char.IsWhiteSpace(line[column]))
                continue;

            clauseStartColumn = column;
            return char.IsLower(line[column]);
        }

        clauseStartColumn = -1;
        return false;
    }

    private static bool TryFindPrologHeadBoundary(
        string line,
        int headStartColumn,
        out int bodyStartColumn,
        out int clauseEndColumn)
    {
        bodyStartColumn = -1;
        clauseEndColumn = -1;
        var parenthesisDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        for (var column = headStartColumn; column < line.Length; column++)
        {
            var ch = line[column];
            if (ch is '\'' or '"')
            {
                column = SkipQuotedToken(line, column, ch) - 1;
                continue;
            }
            switch (ch)
            {
                case '(':
                    parenthesisDepth++;
                    continue;
                case ')' when parenthesisDepth > 0:
                    parenthesisDepth--;
                    continue;
                case '[':
                    bracketDepth++;
                    continue;
                case ']' when bracketDepth > 0:
                    bracketDepth--;
                    continue;
                case '{':
                    braceDepth++;
                    continue;
                case '}' when braceDepth > 0:
                    braceDepth--;
                    continue;
            }
            if (parenthesisDepth != 0 || bracketDepth != 0 || braceDepth != 0)
                continue;

            var separatorLength = line.AsSpan(column).StartsWith("-->", StringComparison.Ordinal)
                ? 3
                : line.AsSpan(column).StartsWith(":-", StringComparison.Ordinal)
                    ? 2
                    : 0;
            if (separatorLength > 0)
            {
                bodyStartColumn = column + separatorLength;
                clauseEndColumn = FindPrologClauseTerminator(line, bodyStartColumn);
                return true;
            }
            if (IsPrologClauseTerminator(line, column))
            {
                clauseEndColumn = column;
                return true;
            }
        }

        return false;
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
        if (next != '\0' && !char.IsWhiteSpace(next))
            return false;
        return IsTopLevelPrologColumn(line, column);
    }

    private static bool IsTopLevelPrologColumn(string line, int targetColumn)
    {
        var parenthesisDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        for (var column = 0; column < targetColumn; column++)
        {
            var ch = line[column];
            if (ch is '\'' or '"')
            {
                column = SkipQuotedToken(line, column, ch) - 1;
                continue;
            }
            switch (ch)
            {
                case '(':
                    parenthesisDepth++;
                    break;
                case ')' when parenthesisDepth > 0:
                    parenthesisDepth--;
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']' when bracketDepth > 0:
                    bracketDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}' when braceDepth > 0:
                    braceDepth--;
                    break;
            }
        }

        return parenthesisDepth == 0 && bracketDepth == 0 && braceDepth == 0;
    }

    private static int FindPrologClauseTerminator(string line, int startColumn)
    {
        for (var column = Math.Max(0, startColumn); column < line.Length; column++)
        {
            if (IsPrologClauseTerminator(line, column))
                return column;
        }

        return -1;
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
                lines[bodyStartLineIndex][bodyStartColumn] is '{' or '"'
                    ? bodyStartColumn
                    : bodyStartColumn - 1,
                bodyEnd.Line + 1,
                lines[bodyStartLineIndex][bodyStartColumn] is '{' or '"'
                    ? bodyEnd.Column
                    : bodyEnd.Column + 1));
            if (lines[bodyStartLineIndex][bodyStartColumn] == '{')
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
            || !TryFindTclWordEnd(
                lines,
                braceEnds,
                bodyStartLineIndex,
                bodyStartColumn,
                out var bodyEndLine,
                out var bodyEndColumn))
        {
            return false;
        }

        bodyEnd = new TclBraceEnd(bodyEndLine, bodyEndColumn);
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
                if (foundColumn == lines[lineIndex].Length - 1
                    && lines[lineIndex][foundColumn] == '\\')
                {
                    continue;
                }

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
        IReadOnlySet<long> scriptBodyOpenings,
        IDictionary<int, int>? commentColumns = null)
    {
        var result = new string[lines.Count];
        var frames = new Stack<TclLexicalFrame>();
        frames.Push(new TclLexicalFrame(TclLexicalFrameKind.Script));
        var commentContinued = false;

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            if (commentContinued)
            {
                result[lineIndex] = new string(' ', line.Length);
                commentColumns?.TryAdd(lineIndex, 0);
                commentContinued = HasTclEscapedNewline(line);
                continue;
            }

            var buffer = line.ToCharArray();
            var lineContinued = false;
            var suppressLeadingContinuedWord = frames.Peek().Kind != TclLexicalFrameKind.Script
                || !frames.Peek().CommandStart;
            for (var column = 0; column < line.Length;)
            {
                var frame = frames.Peek();
                var ch = line[column];
                if (frame.Kind == TclLexicalFrameKind.SwitchTable)
                {
                    buffer[column] = ' ';
                    if (ch == frame.Terminator)
                    {
                        frames.Pop();
                        column++;
                    }
                    else if (ch == '\\' && column + 1 < line.Length)
                    {
                        buffer[column + 1] = ' ';
                        frame.WordStart = false;
                        column += 2;
                    }
                    else if (char.IsWhiteSpace(ch))
                    {
                        frame.WordStart = true;
                        column++;
                    }
                    else if (ch == '{' && frame.WordStart)
                    {
                        var wordIndex = frame.WordIndex++;
                        if (wordIndex % 2 == 1)
                        {
                            buffer[column] = ';';
                            frames.Push(new TclLexicalFrame(TclLexicalFrameKind.Script, '}'));
                            suppressLeadingContinuedWord = false;
                        }
                        else
                        {
                            frames.Push(new TclLexicalFrame(TclLexicalFrameKind.BracedWord, '}'));
                        }

                        frame.WordStart = false;
                        column++;
                    }
                    else if (ch == '"' && frame.WordStart)
                    {
                        var wordIndex = frame.WordIndex++;
                        if (wordIndex % 2 == 1)
                        {
                            buffer[column] = ';';
                            frames.Push(new TclLexicalFrame(TclLexicalFrameKind.Script, '"'));
                            suppressLeadingContinuedWord = false;
                        }
                        else
                        {
                            var endColumn = SkipQuotedToken(line, column, '"');
                            FillWithSpaces(buffer, column, endColumn);
                            column = endColumn - 1;
                        }
                        frame.WordStart = false;
                        column++;
                    }
                    else
                    {
                        if (frame.WordStart)
                        {
                            var wordIndex = frame.WordIndex++;
                            if (wordIndex % 2 == 1)
                            {
                                var endColumn = column;
                                while (endColumn < line.Length
                                    && !char.IsWhiteSpace(line[endColumn])
                                    && line[endColumn] != frame.Terminator)
                                {
                                    buffer[endColumn] = line[endColumn];
                                    endColumn++;
                                }
                                MarkTclBareScriptCommandBoundary(buffer, column);
                                column = endColumn - 1;
                            }
                        }
                        frame.WordStart = false;
                        column++;
                    }

                    continue;
                }

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
                    buffer[column] = ' ';
                    if (column + 1 >= line.Length)
                    {
                        lineContinued = true;
                        frame.WordStart = true;
                        column++;
                        continue;
                    }

                    if (frame.WordStart)
                        frame.WordIndex++;
                    buffer[column + 1] = ' ';
                    column += 2;
                    frame.CommandStart = false;
                    frame.WordStart = false;
                    continue;
                }
                if (ch == '#' && frame.CommandStart)
                {
                    FillWithSpaces(buffer, column);
                    commentColumns?.TryAdd(lineIndex, column);
                    commentContinued = HasTclEscapedNewline(line);
                    break;
                }
                if (ch == '"')
                {
                    var isScriptArgument = false;
                    if (frame.WordStart)
                    {
                        var wordIndex = frame.WordIndex++;
                        isScriptArgument = IsTclScriptArgument(
                            frame,
                            wordIndex,
                            lines,
                            braceEnd: null);
                        UpdateTclSwitchArgumentState(frame, wordIndex, string.Empty);
                        UpdateTclTryArgumentState(frame, wordIndex, string.Empty, isScriptArgument);
                    }
                    buffer[column] = isScriptArgument ? ';' : ' ';
                    frames.Push(new TclLexicalFrame(
                        isScriptArgument ? TclLexicalFrameKind.Script : TclLexicalFrameKind.Quote,
                        '"'));
                    frame.CommandStart = false;
                    frame.WordStart = false;
                    if (isScriptArgument)
                        suppressLeadingContinuedWord = false;
                    column++;
                    continue;
                }
                if (ch == '[')
                {
                    if (frame.WordStart)
                    {
                        var wordIndex = frame.WordIndex++;
                        UpdateTclSwitchArgumentState(frame, wordIndex, string.Empty);
                        UpdateTclTryArgumentState(
                            frame,
                            wordIndex,
                            string.Empty,
                            isScriptArgument: false);
                    }
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
                    TclBraceEnd? braceEnd = braceEnds.TryGetValue(positionKey, out var foundBraceEnd)
                        ? foundBraceEnd
                        : null;
                    var isSwitchTable = IsTclSwitchTableArgument(
                        frame,
                        wordIndex,
                        lines,
                        braceEnd);
                    var isScriptArgument = !isSwitchTable
                        && (scriptBodyOpenings.Contains(positionKey)
                        || IsTclScriptArgument(
                            frame,
                            wordIndex,
                            lines,
                            braceEnd));
                    UpdateTclSwitchArgumentState(frame, wordIndex, string.Empty);
                    UpdateTclTryArgumentState(
                        frame,
                        wordIndex,
                        string.Empty,
                        isScriptArgument);
                    if (isSwitchTable)
                    {
                        buffer[column] = ' ';
                        frames.Push(new TclLexicalFrame(TclLexicalFrameKind.SwitchTable, '}'));
                    }
                    else if (isScriptArgument)
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
                    var isScriptCommand = token.Length > 0
                        && IsTclBareScriptCommandArgument(frame, wordIndex);
                    if (wordIndex == 0)
                        frame.CommandName = token;
                    else
                    {
                        UpdateTclSwitchArgumentState(frame, wordIndex, token);
                        UpdateTclTryArgumentState(
                            frame,
                            wordIndex,
                            token,
                            isScriptCommand);
                    }
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
                    else if (isScriptCommand)
                    {
                        MarkTclBareScriptCommandBoundary(buffer, column);
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

    private static bool HasTclEscapedNewline(string line)
    {
        var backslashCount = 0;
        for (var column = line.Length - 1; column >= 0 && line[column] == '\\'; column--)
            backslashCount++;
        return backslashCount % 2 == 1;
    }

    private static int FindTclCommentStart(string line)
    {
        var commandStart = true;
        for (var column = 0; column < line.Length; column++)
        {
            var ch = line[column];
            if (ch is '\'' or '"')
            {
                column = SkipQuotedToken(line, column, ch) - 1;
                commandStart = false;
                continue;
            }
            if (ch == '\\')
            {
                column++;
                commandStart = false;
                continue;
            }
            if (ch == '#' && commandStart)
                return column;
            if (ch is ';' or '[')
            {
                commandStart = true;
                continue;
            }
            if (char.IsWhiteSpace(ch))
                continue;
            commandStart = false;
        }

        return -1;
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
            "proc" => wordIndex == 3,
            "eval" => wordIndex >= 1,
            "after" => wordIndex == 2,
            "uplevel" => wordIndex is 1 or 2,
            "try" => wordIndex == frame.TryScriptWordIndex,
            "namespace" => wordIndex == 3,
            "dict" => frame.LastBareWord == "for"
                && wordIndex == frame.LastBareWordIndex + 3,
            "switch" => frame.SwitchStringWordIndex >= 0
                && wordIndex - frame.SwitchStringWordIndex >= 2
                && (wordIndex - frame.SwitchStringWordIndex) % 2 == 0,
            _ => false,
        };
    }

    private static bool IsTclBareScriptCommandArgument(
        TclLexicalFrame frame,
        int wordIndex)
    {
        if (frame.CommandName == "eval")
            return wordIndex == 1;
        if (frame.CommandName == "uplevel")
            return wordIndex is 1 or 2;

        return IsTclScriptArgument(
            frame,
            wordIndex,
            Array.Empty<string>(),
            braceEnd: null);
    }

    private static void MarkTclBareScriptCommandBoundary(char[] buffer, int commandColumn)
    {
        var boundaryColumn = commandColumn - 1;
        if (boundaryColumn >= 0 && char.IsWhiteSpace(buffer[boundaryColumn]))
            buffer[boundaryColumn] = ';';
    }

    private static bool IsTclSwitchTableArgument(
        TclLexicalFrame frame,
        int wordIndex,
        IReadOnlyList<string> lines,
        TclBraceEnd? braceEnd)
    {
        return frame.CommandName == "switch"
            && frame.SwitchStringWordIndex >= 0
            && wordIndex == frame.SwitchStringWordIndex + 1
            && braceEnd is { } end
            && IsTclLastCommandWord(lines, end);
    }

    private static void UpdateTclSwitchArgumentState(
        TclLexicalFrame frame,
        int wordIndex,
        string token)
    {
        if (frame.CommandName != "switch"
            || wordIndex == 0
            || frame.SwitchStringWordIndex >= 0)
        {
            return;
        }

        if (frame.SwitchOptionValuePending)
        {
            frame.SwitchOptionValuePending = false;
            return;
        }

        if (!frame.SwitchOptionsEnded && token.StartsWith("-", StringComparison.Ordinal))
        {
            if (token == "--")
                frame.SwitchOptionsEnded = true;
            else if (token is "-matchvar" or "-indexvar")
                frame.SwitchOptionValuePending = true;
            return;
        }

        frame.SwitchStringWordIndex = wordIndex;
    }

    private static void UpdateTclTryArgumentState(
        TclLexicalFrame frame,
        int wordIndex,
        string token,
        bool isScriptArgument)
    {
        if (frame.CommandName != "try")
            return;

        if (isScriptArgument)
        {
            frame.TryClauseWordIndex = wordIndex + 1;
            frame.TryScriptWordIndex = -1;
            return;
        }

        if (wordIndex != frame.TryClauseWordIndex)
            return;

        frame.TryScriptWordIndex = token switch
        {
            "on" or "trap" => wordIndex + 3,
            "finally" => wordIndex + 1,
            _ => -1,
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

    private static IReadOnlyDictionary<int, IReadOnlyList<PrologGoalCall>> BuildPrologGoalCalls(
        IReadOnlyList<string> lines,
        IReadOnlyDictionary<int, SymbolRecord> containersByLine,
        IReadOnlySet<string> callableNames)
    {
        var result = new Dictionary<int, IReadOnlyList<PrologGoalCall>>();
        var frames = new Stack<PrologLexicalFrame>();
        var expectGoal = true;
        SymbolRecord? activeContainer = null;
        var scanningMultilineHead = false;
        var multilineHeadParenthesisDepth = 0;
        var multilineHeadParenthesesClosed = false;

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var lineNumber = lineIndex + 1;
            if (!containersByLine.TryGetValue(lineNumber, out var container))
            {
                frames.Clear();
                activeContainer = null;
                expectGoal = true;
                scanningMultilineHead = false;
                multilineHeadParenthesisDepth = 0;
                multilineHeadParenthesesClosed = false;
                continue;
            }

            if (activeContainer == null
                || activeContainer.StartLine != container.StartLine
                || !string.Equals(activeContainer.Name, container.Name, StringComparison.Ordinal))
            {
                frames.Clear();
                activeContainer = container;
                expectGoal = true;
                scanningMultilineHead = container.StartLine == lineNumber
                    && !PrologHeadRegex.IsMatch(lines[lineIndex])
                    && PrologMultilineHeadRegex.IsMatch(lines[lineIndex]);
                multilineHeadParenthesisDepth = 0;
                multilineHeadParenthesesClosed = false;
            }

            string callScanLine;
            if (scanningMultilineHead)
            {
                callScanLine = PreparePrologMultilineHeadScanLine(
                    lines[lineIndex],
                    ref multilineHeadParenthesisDepth,
                    ref multilineHeadParenthesesClosed,
                    out var headEnded);
                scanningMultilineHead = !headEnded;
            }
            else
            {
                callScanLine = PreparePrologCallScanLine(
                    "prolog",
                    lines[lineIndex],
                    container.StartLine < lineNumber);
            }
            var lineCalls = new List<PrologGoalCall>();
            ScanPrologGoalLine(
                lines,
                lineIndex,
                callScanLine,
                callableNames,
                frames,
                ref expectGoal,
                lineCalls);
            if (lineCalls.Count > 0)
                result[lineNumber] = lineCalls;

            if (ContainsPrologClauseTerminator(callScanLine))
            {
                frames.Clear();
                activeContainer = null;
                expectGoal = true;
            }
        }

        return result;
    }

    private static string PreparePrologMultilineHeadScanLine(
        string line,
        ref int parenthesisDepth,
        ref bool parenthesesClosed,
        out bool headEnded)
    {
        headEnded = false;
        for (var column = 0; column < line.Length; column++)
        {
            var ch = line[column];
            if (ch is '\'' or '"')
            {
                column = SkipQuotedToken(line, column, ch) - 1;
                continue;
            }

            if (!parenthesesClosed)
            {
                if (ch == '(')
                {
                    parenthesisDepth++;
                }
                else if (ch == ')' && parenthesisDepth > 0)
                {
                    parenthesisDepth--;
                    parenthesesClosed = parenthesisDepth == 0;
                }
                continue;
            }

            if (line.AsSpan(column).StartsWith("-->", StringComparison.Ordinal))
            {
                var masked = line.ToCharArray();
                FillWithSpaces(masked, 0, column + 3);
                headEnded = true;
                return new string(masked);
            }
            if (line.AsSpan(column).StartsWith(":-", StringComparison.Ordinal))
            {
                var masked = line.ToCharArray();
                FillWithSpaces(masked, 0, column + 2);
                headEnded = true;
                return new string(masked);
            }
            if (IsPrologClauseTerminator(line, column))
            {
                headEnded = true;
                return new string(' ', line.Length);
            }
        }

        return new string(' ', line.Length);
    }

    private static void ScanPrologGoalLine(
        IReadOnlyList<string> lines,
        int lineIndex,
        string line,
        IReadOnlySet<string> callableNames,
        Stack<PrologLexicalFrame> frames,
        ref bool expectGoal,
        List<PrologGoalCall> calls)
    {
        for (var column = 0; column < line.Length;)
        {
            var ch = line[column];
            if (char.IsWhiteSpace(ch))
            {
                column++;
                continue;
            }

            if (ch is '\'' or '"')
            {
                column = SkipQuotedToken(line, column, ch);
                if (expectGoal)
                    expectGoal = false;
                continue;
            }
            if (IsPrologClauseTerminator(line, column))
            {
                frames.Clear();
                expectGoal = true;
                column++;
                continue;
            }

            if (expectGoal)
            {
                if (line.AsSpan(column).StartsWith("-->", StringComparison.Ordinal))
                {
                    column += 3;
                    continue;
                }
                if (line.AsSpan(column).StartsWith(":-", StringComparison.Ordinal)
                    || line.AsSpan(column).StartsWith(@"\+", StringComparison.Ordinal))
                {
                    column += 2;
                    continue;
                }
                if (ch is ',' or ';')
                {
                    column++;
                    continue;
                }
                if (line.AsSpan(column).StartsWith("->", StringComparison.Ordinal))
                {
                    column += 2;
                    continue;
                }
                if (ch == '(')
                {
                    frames.Push(new PrologLexicalFrame(PrologLexicalFrameKind.GoalGroup));
                    column++;
                    continue;
                }
                if (ch == '{')
                {
                    frames.Push(new PrologLexicalFrame(
                        PrologLexicalFrameKind.GoalGroup,
                        terminator: '}'));
                    column++;
                    continue;
                }
                if (ch == '[')
                {
                    frames.Push(new PrologLexicalFrame(
                        PrologLexicalFrameKind.TermGroup,
                        terminator: ']'));
                    expectGoal = false;
                    column++;
                    continue;
                }
                if (ch == '!')
                {
                    expectGoal = false;
                    column++;
                    continue;
                }
                if (char.IsLower(ch))
                {
                    var nameStart = column;
                    column++;
                    while (column < line.Length
                        && (char.IsLetterOrDigit(line[column]) || line[column] == '_'))
                    {
                        column++;
                    }

                    var name = line[nameStart..column];
                    var nextColumn = column;
                    while (nextColumn < line.Length && char.IsWhiteSpace(line[nextColumn]))
                        nextColumn++;
                    if (nextColumn < line.Length
                        && line[nextColumn] == ':'
                        && (nextColumn + 1 >= line.Length || line[nextColumn + 1] != '-'))
                    {
                        column = nextColumn + 1;
                        expectGoal = true;
                        continue;
                    }
                    if (callableNames.Contains(name)
                        && !IsPrologTermBeforeInfixOperator(
                            lines,
                            lineIndex,
                            column,
                            nextColumn))
                    {
                        calls.Add(new PrologGoalCall(name, nameStart));
                    }

                    if (nextColumn < line.Length && line[nextColumn] == '(')
                    {
                        if (PrologMetaGoalArguments.TryGetValue(name, out var goalArgumentIndices))
                        {
                            var metaFrame = new PrologLexicalFrame(
                                PrologLexicalFrameKind.MetaArguments,
                                goalArgumentIndices);
                            frames.Push(metaFrame);
                            expectGoal = metaFrame.CurrentArgumentIsGoal;
                        }
                        else
                        {
                            frames.Push(new PrologLexicalFrame(
                                PrologLexicalFrameKind.PredicateArguments));
                            expectGoal = false;
                        }

                        column = nextColumn + 1;
                    }
                    else
                    {
                        expectGoal = false;
                    }

                    continue;
                }

                expectGoal = false;
                column++;
                continue;
            }

            if (ch == '(')
            {
                frames.Push(new PrologLexicalFrame(PrologLexicalFrameKind.PredicateArguments));
                column++;
                continue;
            }
            if (ch is '[' or '{')
            {
                frames.Push(new PrologLexicalFrame(
                    PrologLexicalFrameKind.TermGroup,
                    terminator: ch == '[' ? ']' : '}'));
                column++;
                continue;
            }
            if (ch is ')' or ']' or '}')
            {
                if (frames.TryPeek(out var closingFrame)
                    && closingFrame.Terminator == ch)
                {
                    frames.Pop();
                }
                expectGoal = false;
                column++;
                continue;
            }
            if (ch == ',')
            {
                if (frames.TryPeek(out var frame)
                    && frame.Kind == PrologLexicalFrameKind.MetaArguments)
                {
                    frame.ArgumentIndex++;
                    expectGoal = frame.CurrentArgumentIsGoal;
                }
                else if (CanStartNextPrologGoal(frames))
                {
                    expectGoal = true;
                }

                column++;
                continue;
            }
            if (ch == ';'
                || line.AsSpan(column).StartsWith("->", StringComparison.Ordinal))
            {
                if (CanStartNextPrologGoal(frames))
                    expectGoal = true;
                column += ch == ';' ? 1 : 2;
                continue;
            }

            column++;
        }
    }

    private static bool IsPrologTermBeforeInfixOperator(
        IReadOnlyList<string> lines,
        int lineIndex,
        int nameEndColumn,
        int nextColumn)
    {
        const int lookaheadLineLimit = 256;
        var line = lines[lineIndex];
        var afterTermLine = lineIndex;
        var afterTermColumn = nextColumn;
        if (nextColumn < line.Length && line[nextColumn] == '(')
        {
            var depth = 0;
            var termClosed = false;
            var endLineExclusive = Math.Min(lines.Count, lineIndex + lookaheadLineLimit);
            for (var scanLineIndex = lineIndex;
                scanLineIndex < endLineExclusive && !termClosed;
                scanLineIndex++)
            {
                var scanLine = lines[scanLineIndex];
                var startColumn = scanLineIndex == lineIndex ? nextColumn : 0;
                for (var column = startColumn; column < scanLine.Length; column++)
                {
                    var ch = scanLine[column];
                    if (ch is '\'' or '"')
                    {
                        column = SkipQuotedToken(scanLine, column, ch) - 1;
                        continue;
                    }

                    if (ch == '(')
                    {
                        depth++;
                    }
                    else if (ch == ')' && --depth == 0)
                    {
                        afterTermLine = scanLineIndex;
                        afterTermColumn = column + 1;
                        termClosed = true;
                        break;
                    }
                }
            }

            // An unterminated compound term is not authoritative evidence of a call.
            // 未終端の compound term は call と判断できる根拠にならない。
            if (!termClosed)
                return true;
        }
        else
        {
            afterTermColumn = nameEndColumn;
        }

        if (!TryFindNextPrologToken(
                lines,
                afterTermLine,
                afterTermColumn,
                lookaheadLineLimit,
                out var operatorLine,
                out var operatorColumn))
        {
            return false;
        }

        var operatorSourceLine = lines[operatorLine];
        var remaining = operatorSourceLine.AsSpan(operatorColumn);
        if (remaining.StartsWith("->", StringComparison.Ordinal)
            || remaining.StartsWith("*->", StringComparison.Ordinal))
        {
            return false;
        }

        if (operatorSourceLine[operatorColumn] is '=' or '\\' or '<' or '>' or '@' or '#'
            or ':' or '+' or '-' or '*' or '/' or '^')
        {
            return true;
        }

        foreach (var operatorName in PrologInfixOperatorNames)
        {
            if (!remaining.StartsWith(operatorName, StringComparison.Ordinal))
                continue;
            var operatorEnd = operatorColumn + operatorName.Length;
            if (operatorEnd >= operatorSourceLine.Length
                || !char.IsLetterOrDigit(operatorSourceLine[operatorEnd])
                    && operatorSourceLine[operatorEnd] != '_')
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryFindNextPrologToken(
        IReadOnlyList<string> lines,
        int startLine,
        int startColumn,
        int lookaheadLineLimit,
        out int tokenLine,
        out int tokenColumn)
    {
        var endLineExclusive = Math.Min(lines.Count, startLine + lookaheadLineLimit);
        for (var lineIndex = startLine; lineIndex < endLineExclusive; lineIndex++)
        {
            var line = lines[lineIndex];
            var column = lineIndex == startLine ? startColumn : 0;
            while (column < line.Length && char.IsWhiteSpace(line[column]))
                column++;
            if (column < line.Length)
            {
                tokenLine = lineIndex;
                tokenColumn = column;
                return true;
            }
        }

        tokenLine = -1;
        tokenColumn = -1;
        return false;
    }

    private static readonly string[] PrologInfixOperatorNames =
        ["is", "mod", "rem", "xor", "div", "rdiv"];

    private static bool CanStartNextPrologGoal(
        IEnumerable<PrologLexicalFrame> frames)
    {
        foreach (var frame in frames)
        {
            if (frame.Kind == PrologLexicalFrameKind.PredicateArguments)
                return false;
            if (frame.Kind == PrologLexicalFrameKind.TermGroup)
                return false;
            if (frame.Kind == PrologLexicalFrameKind.MetaArguments)
                return frame.CurrentArgumentIsGoal;
        }

        return true;
    }

    private static bool ContainsPrologClauseTerminator(string line)
    {
        for (var column = 0; column < line.Length; column++)
        {
            if (IsPrologClauseTerminator(line, column))
                return true;
        }

        return false;
    }

    private static void AddPrologContainers(
        IReadOnlyList<string> lines,
        IReadOnlyList<SymbolRecord> symbols,
        Dictionary<int, SymbolRecord> containersByLine,
        Dictionary<int, List<SymbolRecord>> declarationsByLine)
    {
        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "function" || symbol.StartLine < 1 || symbol.StartLine > lines.Count)
                continue;

            var startLineIndex = symbol.StartLine - 1;
            var startColumn = Math.Clamp(
                symbol.StartColumn ?? 0,
                0,
                lines[startLineIndex].Length);
            var headLine = lines[startLineIndex][startColumn..];
            var headMatch = PrologHeadRegex.Match(headLine);
            if (!headMatch.Success)
                headMatch = PrologMultilineHeadRegex.Match(headLine);
            if (!headMatch.Success
                || !string.Equals(headMatch.Groups["name"].Value, symbol.Name, StringComparison.Ordinal))
            {
                continue;
            }

            if (!declarationsByLine.TryGetValue(symbol.StartLine, out var declarations))
            {
                declarations = [];
                declarationsByLine[symbol.StartLine] = declarations;
            }
            declarations.Add(symbol);

            var endLineIndex = FindPrologClauseEnd(lines, startLineIndex, startColumn);
            for (var lineIndex = startLineIndex; lineIndex <= endLineIndex; lineIndex++)
                containersByLine.TryAdd(lineIndex + 1, symbol);
        }

        foreach (var declarations in declarationsByLine.Values)
        {
            declarations.Sort(static (left, right) =>
                (left.StartColumn ?? 0).CompareTo(right.StartColumn ?? 0));
        }
    }

    private static int FindPrologClauseEnd(
        IReadOnlyList<string> lines,
        int startLineIndex,
        int startColumn)
    {
        for (var lineIndex = startLineIndex; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            var firstColumn = lineIndex == startLineIndex ? startColumn : 0;
            for (var column = firstColumn; column < line.Length; column++)
            {
                if (IsPrologClauseTerminator(line, column))
                    return lineIndex;
            }
        }

        return startLineIndex;
    }
}
