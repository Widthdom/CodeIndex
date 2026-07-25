using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CodeIndex.Models;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

internal static partial class DynamicDeclarativeReferenceExtractor
{
    private static readonly ConditionalWeakTable<string, PrologClauseTerminatorMap>
        PrologClauseTerminatorMaps = new();

    internal sealed class ExtractionState
    {
        public ExtractionState(
            HashSet<string> callableNames,
            HashSet<DeclarationPosition> declarationPositions,
            IReadOnlyDictionary<int, SymbolRecord> containersByLine,
            IReadOnlyList<TclContainerScope> tclContainerScopes,
            IReadOnlyDictionary<string, TclCallableTarget> tclQualifiedCallableTargets,
            IReadOnlyDictionary<int, List<SymbolRecord>> prologContainersByLine,
            IReadOnlyList<string>? tclCallLines,
            IReadOnlyDictionary<int, IReadOnlyList<PrologGoalCall>>? prologGoalCallsByLine,
            IReadOnlySet<int> prologDirectiveLines)
        {
            CallableNames = callableNames;
            DeclarationPositions = declarationPositions;
            ContainersByLine = containersByLine;
            TclContainerScopes = tclContainerScopes;
            TclQualifiedCallableTargets = tclQualifiedCallableTargets;
            PrologContainersByLine = prologContainersByLine;
            TclCallLines = tclCallLines;
            PrologGoalCallsByLine = prologGoalCallsByLine;
            PrologDirectiveLines = prologDirectiveLines;
        }

        public HashSet<string> CallableNames { get; }
        private HashSet<DeclarationPosition> DeclarationPositions { get; }
        public IReadOnlyDictionary<int, SymbolRecord> ContainersByLine { get; }
        private IReadOnlyList<TclContainerScope> TclContainerScopes { get; }
        private IReadOnlyDictionary<string, TclCallableTarget> TclQualifiedCallableTargets { get; }
        private IReadOnlyDictionary<int, List<SymbolRecord>> PrologContainersByLine { get; }
        private IReadOnlyList<string>? TclCallLines { get; }
        private IReadOnlyDictionary<int, IReadOnlyList<PrologGoalCall>>? PrologGoalCallsByLine { get; }
        private IReadOnlySet<int> PrologDirectiveLines { get; }

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

        public bool TryResolveTclCallable(
            string callName,
            out string referenceName,
            out string? targetQualifier,
            out int referenceNameOffset)
        {
            referenceName = callName;
            targetQualifier = null;
            referenceNameOffset = 0;
            if (CallableNames.Contains(callName))
                return true;

            var normalizedCallName = NormalizeTclQualifiedName(callName);
            if (TclQualifiedCallableTargets.TryGetValue(normalizedCallName, out var target))
            {
                referenceName = target.Name;
                targetQualifier = target.Qualifier;
                referenceNameOffset = callName.LastIndexOf("::", StringComparison.Ordinal) + 2;
                return true;
            }

            if (!normalizedCallName.Contains("::", StringComparison.Ordinal)
                && CallableNames.Contains(normalizedCallName))
            {
                referenceName = normalizedCallName;
                referenceNameOffset = callName.Length - normalizedCallName.Length;
                return true;
            }

            return false;
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

        public bool IsPrologDirectiveLine(int lineNumber) =>
            PrologDirectiveLines.Contains(lineNumber);

        public bool IsDeclarationAt(int lineNumber, int column, string name) =>
            DeclarationPositions.Contains(new DeclarationPosition(lineNumber, column, name));
    }

    private static readonly Regex TclProcRegex = new(
        @"(?<![\w:.-])proc\s+(?<name>[A-Za-z_:][\w:.-]*)\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TclInlineProcRegex = new(
        @"(?:^|[;\[])\s*proc\s+(?<name>[A-Za-z_:][\w:.-]*)\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PrologHeadRegex = new(
        @"^\s*(?<name>[a-z][A-Za-z0-9_]*)\s*(?:\([^\r\n]*\))?\s*(?::-|-->|\.(?=\s*(?:$|:-|[a-z][A-Za-z0-9_]*\s*\(\s*$|[a-z][A-Za-z0-9_]*(?:\s*\([^)]*\))?\s*(?::-|-->|\.))))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PrologMultilineHeadRegex = new(
        @"^\s*(?<name>[a-z][A-Za-z0-9_]*)(?:(?<open>\s*\()|\s*$)",
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
    private static readonly Regex CrystalControlPredicateCallRegex = new(
        @"\b(?:if|unless|while|until)\s+(?<name>[A-Za-z_]\w*[?!]?)(?![\w?!])(?!\s*(?::|(?:<<|>>|[+\-*/%&|^])?=))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GroovyBareCallRegex = new(
        @"(?:^|[;={])\s*(?:return\s+)?(?<name>[A-Za-z_]\w*)\b(?!\s*(?::|\(|(?:<<|>>|[+\-*/%&|^])?=))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GroovyConstructorDeclarationRegex = new(
        @"(?:^|[;{])\s*(?:@[A-Za-z_$][\w.$]*(?:\s*\([^)\r\n]*\))?\s+)*(?:(?:public|protected|private)\s+)*(?<name>[A-Z]\w*)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GroovyMethodDeclarationRegex = new(
        @"(?:^|[;{])\s*(?:@[A-Za-z_$][\w.$]*(?:\s*\([^)\r\n]*\))?\s+)*(?:(?:public|private|protected|static|final|abstract|synchronized|native|strictfp)\s+)*(?:<[^(){}\r\n]+>\s+)?(?:def|void|boolean|byte|char|short|int|long|float|double|BigDecimal|BigInteger|String|[A-Za-z_$][\w.$]*(?:\s*<[^(){}\r\n]+>)?(?:\s*\[\])*)\s+(?<name>[A-Za-z_]\w*)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CrystalMethodDeclarationRegex = new(
        @"(?:^|;)\s*(?:(?:private|protected|abstract)\s+)*def\s+(?:self\.)?(?<name>[A-Za-z_]\w*[?!]?)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CrystalFunDeclarationRegex = new(
        @"(?:^|;)\s*fun\s+(?:[A-Za-z_]\w*\.)?(?<name>[A-Za-z_]\w*[?!]?)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TclCommandRegex = new(
        @"(?:^|[;\[])\s*(?<name>[A-Za-z_:][\w:.-]*)(?=$|[\s;\]}""])",
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

    internal readonly record struct TclCallableTarget(
        string Name,
        string Qualifier);

    private enum TclLexicalFrameKind
    {
        Script,
        Quote,
        BracedWord,
        ExpressionWord,
        SwitchTable,
    }

    internal readonly record struct PrologGoalCall(
        string Name,
        int Column,
        bool IsTopLevelDirective = false);

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

    private sealed class AmbiguousPerlQuoteLikeState(
        char openingDelimiter,
        char closingDelimiter,
        int remainingSegments)
    {
        public char OpeningDelimiter { get; set; } = openingDelimiter;
        public char ClosingDelimiter { get; set; } = closingDelimiter;
        public int RemainingSegments { get; set; } = remainingSegments;
        public int DelimiterDepth { get; set; } = openingDelimiter == closingDelimiter ? 0 : 1;
        public bool AwaitingNextOpeningDelimiter { get; set; }
    }

    private readonly record struct AmbiguousPerlHeredocDelimiter(
        string Value,
        bool AllowIndent)
    {
        public bool MatchesTerminator(string line)
        {
            var candidate = line.AsSpan().TrimEnd();
            if (AllowIndent)
                candidate = candidate.TrimStart();
            return candidate.SequenceEqual(Value);
        }
    }

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<int>> PrologMetaGoalArguments =
        new Dictionary<string, IReadOnlySet<int>>(StringComparer.Ordinal)
        {
            ["call"] = new HashSet<int> { 0 },
            ["initialization"] = new HashSet<int> { 0 },
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
        char terminator = '\0',
        TclLexicalFrame? concatenationOwner = null)
    {
        public TclLexicalFrameKind Kind { get; } = kind;
        public char Terminator { get; } = terminator;
        public TclLexicalFrame? ConcatenationOwner { get; } = concatenationOwner;
        public bool CommandStart { get; set; } = true;
        public bool WordStart { get; set; } = true;
        public int WordIndex { get; set; }
        public string? CommandName { get; set; }
        public string? FirstArgument { get; set; }
        public string? LastBareWord { get; set; }
        public int LastBareWordIndex { get; set; } = -1;
        public int SwitchStringWordIndex { get; set; } = -1;
        public bool SwitchOptionsEnded { get; set; }
        public bool SwitchOptionValuePending { get; set; }
        public int TryClauseWordIndex { get; set; } = 2;
        public int TryScriptWordIndex { get; set; } = 1;
        public int DictScriptWordIndex { get; set; } = -1;
        public int UplevelScriptWordIndex { get; set; } = -1;
        public TclLexicalFrame? ConcatenatedScriptState { get; set; }

        public void CopyCommandStateFrom(TclLexicalFrame source)
        {
            CommandStart = source.CommandStart;
            WordStart = source.WordStart;
            WordIndex = source.WordIndex;
            CommandName = source.CommandName;
            FirstArgument = source.FirstArgument;
            LastBareWord = source.LastBareWord;
            LastBareWordIndex = source.LastBareWordIndex;
            SwitchStringWordIndex = source.SwitchStringWordIndex;
            SwitchOptionsEnded = source.SwitchOptionsEnded;
            SwitchOptionValuePending = source.SwitchOptionValuePending;
            TryClauseWordIndex = source.TryClauseWordIndex;
            TryScriptWordIndex = source.TryScriptWordIndex;
            DictScriptWordIndex = source.DictScriptWordIndex;
            UplevelScriptWordIndex = source.UplevelScriptWordIndex;
            ConcatenatedScriptState = source.ConcatenatedScriptState;
        }

        public void ResetCommand()
        {
            CommandStart = true;
            WordStart = true;
            WordIndex = 0;
            CommandName = null;
            FirstArgument = null;
            LastBareWord = null;
            LastBareWordIndex = -1;
            SwitchStringWordIndex = -1;
            SwitchOptionsEnded = false;
            SwitchOptionValuePending = false;
            TryClauseWordIndex = 2;
            TryScriptWordIndex = 1;
            DictScriptWordIndex = -1;
            UplevelScriptWordIndex = -1;
            ConcatenatedScriptState = null;
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
        var insideAmbiguousPerlData = false;
        var ambiguousPerlHeredocDelimiters =
            new Queue<AmbiguousPerlHeredocDelimiter>();
        AmbiguousPerlQuoteLikeState? ambiguousPerlQuoteLikeState = null;
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
                if (ambiguousPerlHeredocDelimiters.TryPeek(out var heredocDelimiter))
                {
                    result[lineIndex] = new string(' ', line.Length);
                    if (heredocDelimiter.MatchesTerminator(line))
                        ambiguousPerlHeredocDelimiters.Dequeue();
                    continue;
                }

                if (insideAmbiguousPerlData)
                {
                    result[lineIndex] = new string(' ', line.Length);
                    continue;
                }

                var trimmedMarker = trimmed.TrimEnd();
                if (trimmedMarker.SequenceEqual("__DATA__")
                    || trimmedMarker.SequenceEqual("__END__"))
                {
                    result[lineIndex] = new string(' ', line.Length);
                    insideAmbiguousPerlData = true;
                    continue;
                }

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
            var hasPrologLineStructure = language == "ambiguous_pl"
                && (StartsWithPrologGoalDirective(line) || PrologHeadRegex.IsMatch(line));
            for (var column = 0; column < line.Length;)
            {
                if (ambiguousPerlQuoteLikeState != null)
                {
                    MaskAmbiguousPerlQuoteLikeCharacter(
                        line,
                        buffer,
                        ref column,
                        ref ambiguousPerlQuoteLikeState);
                    continue;
                }

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

                if (language == "ambiguous_pl"
                    && !hasPrologLineStructure
                    && TryBeginAmbiguousPerlQuoteLikeLiteral(
                        line,
                        buffer,
                        column,
                        out var perlQuoteLikeState,
                        out var perlContentColumn))
                {
                    ambiguousPerlQuoteLikeState = perlQuoteLikeState;
                    column = perlContentColumn;
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
                    if (IsPrologHashOperator(line, column)
                        || IsPerlLastIndexVariable(line, column))
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
            if (language == "ambiguous_pl")
            {
                EnqueueAmbiguousPerlHeredocDelimiters(
                    line,
                    result[lineIndex],
                    ambiguousPerlHeredocDelimiters);
            }
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

    public static void AddTclInlineProcSymbols(
        long fileId,
        IReadOnlyList<string> rawLines,
        IReadOnlyList<string> scriptLines,
        List<SymbolRecord> symbols)
    {
        var declarationContainerScopes = BuildTclDeclarationContainerScopes(
            rawLines,
            symbols);
        var existingDeclarations = new HashSet<(int Line, string Name)>(
            symbols
                .Where(static symbol => symbol.Kind == "function")
                .Select(static symbol => (symbol.StartLine, symbol.Name)));
        for (var lineIndex = 0; lineIndex < scriptLines.Count; lineIndex++)
        {
            foreach (Match match in BoundedRegex.EnumerateMatches(
                TclInlineProcRegex,
                scriptLines[lineIndex]))
            {
                var nameGroup = match.Groups["name"];
                var lineNumber = lineIndex + 1;
                if (!existingDeclarations.Add((lineNumber, nameGroup.Value)))
                    continue;

                var symbol = new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "function",
                    Name = nameGroup.Value,
                    Line = lineNumber,
                    StartLine = lineNumber,
                    StartColumn = nameGroup.Index,
                    EndLine = lineNumber,
                    Signature = rawLines[lineIndex].Trim(),
                };
                for (var scopeIndex = declarationContainerScopes.Count - 1;
                    scopeIndex >= 0;
                    scopeIndex--)
                {
                    var scope = declarationContainerScopes[scopeIndex];
                    if (!scope.Contains(lineNumber, nameGroup.Index))
                        continue;
                    symbol.ContainerKind = scope.Symbol.Kind;
                    symbol.ContainerName = scope.Symbol.Name;
                    break;
                }
                symbols.Add(symbol);
            }
        }
    }

    private static IReadOnlyList<TclContainerScope> BuildTclDeclarationContainerScopes(
        IReadOnlyList<string> lines,
        IReadOnlyList<SymbolRecord> symbols)
    {
        var braceEnds = BuildTclBraceEndPositions(lines);
        var scopes = new List<TclContainerScope>();
        foreach (var symbol in symbols)
        {
            if (symbol.Kind is not ("namespace" or "class")
                || symbol.StartLine < 1
                || symbol.StartLine > lines.Count)
            {
                continue;
            }

            var lineIndex = symbol.StartLine - 1;
            var nameColumn = lines[lineIndex].IndexOf(
                symbol.Name,
                StringComparison.Ordinal);
            if (nameColumn < 0)
                continue;
            var openingColumn = lines[lineIndex].IndexOf(
                '{',
                nameColumn + symbol.Name.Length);
            if (openingColumn < 0
                || !braceEnds.TryGetValue(
                    GetTclPositionKey(lineIndex, openingColumn),
                    out var bodyEnd))
            {
                continue;
            }

            scopes.Add(new TclContainerScope(
                symbol,
                lineIndex + 1,
                openingColumn,
                bodyEnd.Line + 1,
                bodyEnd.Column));
        }

        return scopes;
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
        var tclQualifiedCallableTargets =
            new Dictionary<string, TclCallableTarget>(StringComparer.Ordinal);
        var prologContainersByLine = new Dictionary<int, List<SymbolRecord>>();
        string[]? tclCallLines = null;
        IReadOnlyDictionary<int, IReadOnlyList<PrologGoalCall>>? prologGoalCallsByLine = null;
        IReadOnlySet<int> prologDirectiveLines = new HashSet<int>();
        if (language == "tcl")
        {
            foreach (var symbol in symbols)
            {
                if (symbol.Kind is not ("function" or "lambda" or "operator")
                    || string.IsNullOrWhiteSpace(symbol.ContainerName))
                {
                    continue;
                }

                var normalizedContainer = NormalizeTclQualifiedName(symbol.ContainerName);
                var normalizedName = NormalizeTclQualifiedName(symbol.Name);
                if (normalizedContainer.Length == 0 || normalizedName.Length == 0)
                    continue;

                tclQualifiedCallableTargets[
                    $"{normalizedContainer}::{normalizedName}"] =
                    new TclCallableTarget(symbol.Name, symbol.ContainerName);
            }

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
            prologDirectiveLines = BuildPrologDirectiveLines(preparedLines);
        }

        return new ExtractionState(
            callableNames,
            declarationPositions,
            containersByLine,
            tclContainerScopes,
            tclQualifiedCallableTargets,
            prologContainersByLine,
            tclCallLines,
            prologGoalCallsByLine,
            prologDirectiveLines);
    }

}
