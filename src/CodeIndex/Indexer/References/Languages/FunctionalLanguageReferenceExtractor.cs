using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static readonly Regex ClojureRequireEntryRegex = new(
        @"\[\s*(?<name>[A-Za-z_][\w.-]*)(?:\s+:as\s+(?<alias>[A-Za-z_][\w.-]*))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ClojureTypeRelationRegex = new(
        @"^\s*\(\s*(?:defrecord|deftype)\s+(?<defined>[^\s\)\[\{]+)\s+\[[^\]]*\]\s*(?<types>[^)]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ClojureCallHeadRegex = new(
        @"\(\s*(?<name>(?:[A-Za-z_][\w.*+!?<>=-]*/)?[A-Za-z_][\w.*+!?<>=-]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ClojureIgnoredCallHeads = new(StringComparer.Ordinal)
    {
        "ns", "require", "import", "refer", "def", "defonce", "defn", "defn-", "defmacro",
        "defmulti", "defmethod", "defprotocol", "defrecord", "deftype", "extend-type",
        "fn", "let", "letfn", "loop", "recur", "if", "if-let", "if-some", "when", "when-let",
        "when-some", "cond", "case", "do", "doseq", "for", "binding", "try", "catch",
        "finally", "throw", "quote", "comment", "var", "set!", "new", ".",
    };

    private static readonly Regex ErlangImportRegex = new(
        @"^\s*-import\s*\(\s*(?<name>[a-z][\w@]*)\s*,",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ErlangBehaviourRegex = new(
        @"^\s*-behaviou?r\s*\(\s*(?<name>[a-z][\w@]*)\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ErlangFunctionDefinitionRegex = new(
        @"^\s*(?<name>[a-z][\w@]*|'(?:\\.|[^'\\\r\n])+')\s*\([^)\r\n]*\)\s*(?:when\b[^-\r\n]*)?->",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ErlangRemoteCallRegex = new(
        @"(?<![\w@])(?<module>[a-z][\w@]*|'(?:\\.|[^'\\\r\n])+'):(?<name>[a-z][\w@]*|'(?:\\.|[^'\\\r\n])+')\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ErlangLocalCallRegex = new(
        @"(?<![-:\w@])(?<name>[a-z][\w@]*|'(?:\\.|[^'\\\r\n])+')\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ErlangSpecificationAttributeRegex = new(
        @"^\s*-(?:spec|callback|type|opaque|record)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ErlangIgnoredCalls = new(StringComparer.Ordinal)
    {
        "if", "case", "receive", "try", "catch", "fun", "when", "andalso", "orelse",
    };

    private static readonly Regex OcamlImportRegex = new(
        @"^\s*(?:open|include)\s+(?<name>[A-Z][\w.']*)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OcamlModuleAliasRegex = new(
        @"^\s*module\s+[A-Z][A-Za-z0-9_']*\s*=\s*(?<name>[A-Z][\w.']*)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OcamlTypeReferenceRegex = new(
        @"(?::|\bof)\s*(?<name>(?:[A-Z][\w.']*|[a-z_][A-Za-z0-9_']*))\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OcamlTypeAliasTargetRegex = new(
        @"^\s*type\s+(?:nonrec\s+)?(?:'[\w]+\s+)*[A-Za-z_][A-Za-z0-9_']*\s*=\s*(?<name>(?:[A-Z][\w.']*|[a-z_][A-Za-z0-9_']*))\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OcamlFunctionDefinitionRegex = new(
        @"^\s*let\s+(?:rec\s+)?(?<name>[a-z_][A-Za-z0-9_']*)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OcamlQualifiedCallRegex = new(
        @"(?<![\w.'])(?<module>[A-Z][\w.']*)\.(?<name>[a-z_][A-Za-z0-9_']*)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OcamlBareCallRegex = new(
        @"(?<![\w.'])(?<name>[a-z_][A-Za-z0-9_']*)\s+(?!(?:with|then|else|do|done|in|to|downto|of)\b)(?=[A-Za-z_(~?])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> OcamlIgnoredCalls = new(StringComparer.Ordinal)
    {
        "let", "rec", "and", "in", "fun", "function", "match", "with", "if", "then", "else",
        "try", "raise", "while", "for", "to", "downto", "do", "done", "begin", "end",
        "module", "open", "include", "type", "class", "object", "method", "val", "external",
    };
    private static readonly HashSet<string> OcamlIgnoredTypeReferences = new(StringComparer.Ordinal)
    {
        "int", "string", "bool", "float", "char", "unit", "bytes", "exn", "list", "array",
        "option", "result", "seq", "lazy_t",
    };

    private static readonly Regex RakuImportRegex = new(
        @"^\s*(?:use|need|require)\s+(?<name>[A-Za-z_][\w:.-]*)(?:\s+:as(?:<(?<angleAlias>[\w.-]+)>|\s+(?<alias>[\w.-]+)))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RakuTypeRelationRegex = new(
        @"\b(?:is|does)\s+(?<name>[A-Za-z_][\w:.-]*)\b(?!\s*\()",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RakuReturnTypeRegex = new(
        @"-->\s*(?<name>[A-Za-z_][\w:.-]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RakuFunctionDefinitionRegex = new(
        @"^\s*(?:(?:my|our|multi|proto|only)\s+)*(?:sub|method|submethod|macro)\s+(?<name>[\w:!?.-]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RakuQualifiedCallRegex = new(
        @"(?<![\w:.-])(?<module>[A-Za-z_][\w:.-]*)::(?<name>[A-Za-z_][\w!?.-]*)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RakuMethodCallRegex = new(
        @"(?<![\w:.$@%&-])(?:[$@%&][A-Za-z_]\w*|[A-Za-z_][\w:.-]*)\.(?<name>[A-Za-z_][\w!?-]*)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RakuBareCallRegex = new(
        @"(?<![\w:.$@%&-])(?<name>[A-Za-z_][\w!?-]*)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> RakuIgnoredCalls = new(StringComparer.Ordinal)
    {
        "if", "elsif", "else", "unless", "given", "when", "for", "loop", "while", "until",
        "repeat", "try", "catch", "return", "sub", "method", "submethod", "macro", "class",
        "role", "grammar", "module", "package", "enum", "use", "need", "require",
    };

    private sealed class FunctionalReferenceState
    {
        internal char StringDelimiter;
        internal int OcamlCommentDepth;
        internal bool RakuPod;
        internal bool ClojureRequireMode;
        internal int ClojureRequireBracketDepth;
        internal bool ClojureProtocolMode;
        internal int ClojureProtocolBaseDepth;
        internal bool ClojureTypeBodyMode;
        internal int ClojureTypeBodyBaseDepth;
        internal int ClojureSuppressedFormDepth;
        internal int ClojureParenDepth;
        internal int CallableBaseDepth;
        internal int RakuBraceDepth;
        internal bool RakuCallableBodyOpened;
        internal char RakuQuoteOpenDelimiter;
        internal char RakuQuoteCloseDelimiter;
        internal int RakuQuoteDepth;
        internal bool ErlangQuotedAtom;
        internal bool ErlangSpecificationMode;
        internal bool OcamlTypeDeclarationMode;
        internal string? OcamlQuotedStringTerminator;
        internal string? RakuHeredocTerminator;
        internal SymbolRecord? ClojureActiveTypeDefinition;
        internal SymbolRecord? OcamlActiveTypeDefinition;
        internal SymbolRecord? ActiveCallable;
    }

    internal static bool ContainsFunctionalSpan(
        IReadOnlyList<(int Start, int End)> spans,
        int index)
    {
        for (var spanIndex = 0; spanIndex < spans.Count; spanIndex++)
        {
            var span = spans[spanIndex];
            if (index >= span.Start && index < span.End)
                return true;
        }

        return false;
    }

    private static bool ContainsFunctionalSpanInterior(
        IReadOnlyList<(int Start, int End)> spans,
        int index)
    {
        for (var spanIndex = 0; spanIndex < spans.Count; spanIndex++)
        {
            var span = spans[spanIndex];
            if (index > span.Start && index < span.End)
                return true;
        }

        return false;
    }

    internal static bool OverlapsFunctionalSpan(
        IReadOnlyList<(int Start, int End)> spans,
        int start,
        int end)
    {
        for (var spanIndex = 0; spanIndex < spans.Count; spanIndex++)
        {
            var span = spans[spanIndex];
            if (span.Start < end && start < span.End)
                return true;
        }

        return false;
    }

    internal static bool TrimmedFunctionalLineEndsWith(
        ReadOnlySpan<char> line,
        char suffix)
    {
        line = line.TrimEnd();
        return !line.IsEmpty && line[^1] == suffix;
    }

    internal static bool TrimmedFunctionalLineEquals(
        ReadOnlySpan<char> line,
        string expected)
        => line.TrimEnd().Equals(expected, StringComparison.Ordinal);

    private static List<ReferenceRecord> ExtractFunctionalLanguageReferences(ReferenceExtractionContext request)
    {
        if (!TryPrepareReferenceLines(
                request.Language,
                request.Content,
                isRazorFile: false,
                request.ContentIsNormalized,
                request.HasOversizeLine,
                request.ConflictMarkerLine,
                out var preparedInput))
        {
            return [];
        }

        var references = CreateReferenceList(request.MaxReferenceCount, EstimateReferenceListInitialCapacity(preparedInput.Lines.Length));
        var seen = CreateReferenceSeenSet(preparedInput.Lines.Length);
        var symbolsByLine = BuildFunctionalSymbolsByLine(request.Symbols, request.ReportDiagnostic);
        var containerResolver = new InnermostContainerResolver(
            BuildReferenceContainerCandidates(request.Symbols, request.ReportDiagnostic));
        var state = new FunctionalReferenceState();

        for (var index = 0; index < preparedInput.Lines.Length; index++)
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            if (ReferenceLimitReached(references))
                break;

            var originalLine = preparedInput.Lines[index];
            var maskedLine = MaskFunctionalReferenceLine(request.Language, originalLine, state);
            var lineNumber = index + 1;
            var definition = FindFunctionalDefinition(request.Language, maskedLine, lineNumber, symbolsByLine);
            var typeDefinition = FindFunctionalTypeDefinition(lineNumber, symbolsByLine);
            PrepareFunctionalCallableState(request.Language, maskedLine, definition, state);
            var container = state.ActiveCallable ?? containerResolver.Find(lineNumber);
            var context = originalLine.Trim();

            switch (request.Language)
            {
                case "clojure":
                    EmitClojureReferences(
                        request.FileId, maskedLine, context, lineNumber, typeDefinition, container, references, seen, state);
                    break;
                case "erlang":
                    EmitErlangReferences(
                        request.FileId, maskedLine, context, lineNumber, container, references, seen, state);
                    break;
                case "ocaml":
                    EmitOcamlReferences(
                        request.FileId, maskedLine, context, lineNumber, definition, typeDefinition, container, references, seen, state);
                    break;
                case "raku":
                    EmitRakuReferences(
                        request.FileId, maskedLine, context, lineNumber, definition, typeDefinition, container, references, seen);
                    break;
            }

            AdvanceFunctionalCallableState(request.Language, maskedLine, state);
        }

        MarkMutualRecursionReferences(references);
        return references;
    }

    private static Dictionary<int, List<SymbolRecord>> BuildFunctionalSymbolsByLine(
        IReadOnlyList<SymbolRecord> symbols,
        Action<ReferenceExtractionDiagnostic>? reportDiagnostic)
    {
        var limits = GetSafetyLimits();
        var symbolsByLine = new Dictionary<int, List<SymbolRecord>>();
        var lineBudgetReported = false;
        var lineNameBudgetReported = false;
        for (var index = 0; index < symbols.Count; index++)
        {
            if (index >= limits.MaxLookupSymbols)
            {
                ReportReferenceLookupBudgetHit(
                    reportDiagnostic,
                    "reference_definition_lookup_symbol_budget_exceeded",
                    $"Functional reference lookup used the first {limits.MaxLookupSymbols:N0} symbols and skipped additional symbols.");
                break;
            }

            var symbol = symbols[index];
            if (!symbolsByLine.TryGetValue(symbol.Line, out var lineSymbols))
            {
                if (symbolsByLine.Count >= limits.MaxLookupLines)
                {
                    if (!lineBudgetReported)
                    {
                        ReportReferenceLookupBudgetHit(
                            reportDiagnostic,
                            "reference_definition_lookup_line_budget_exceeded",
                            $"Functional reference lookup retained the first {limits.MaxLookupLines:N0} definition lines and skipped additional lines.");
                        lineBudgetReported = true;
                    }
                    continue;
                }

                lineSymbols = [];
                symbolsByLine[symbol.Line] = lineSymbols;
            }

            if (lineSymbols.Count < limits.MaxNamesPerLine)
                lineSymbols.Add(symbol);
            else if (!lineNameBudgetReported)
            {
                ReportReferenceLookupBudgetHit(
                    reportDiagnostic,
                    "reference_definition_lookup_line_name_budget_exceeded",
                    $"Functional reference lookup retained at most {limits.MaxNamesPerLine:N0} symbols per definition line and skipped additional symbols.");
                lineNameBudgetReported = true;
            }
        }

        return symbolsByLine;
    }

    private static SymbolRecord? FindFunctionalDefinition(
        string language,
        string maskedLine,
        int lineNumber,
        IReadOnlyDictionary<int, List<SymbolRecord>> symbolsByLine)
    {
        var match = language switch
        {
            "clojure" => Regex.Match(
                maskedLine,
                @"^\s*\(\s*(?:defn-?|defmacro|defmulti|defmethod)\s+(?<name>[^\s\)\[\{]+)",
                RegexOptions.CultureInvariant,
                ExtractionRegexTimeout),
            "erlang" => ErlangFunctionDefinitionRegex.Match(maskedLine),
            "ocaml" => OcamlFunctionDefinitionRegex.Match(maskedLine),
            "raku" => RakuFunctionDefinitionRegex.Match(maskedLine),
            _ => Match.Empty,
        };
        if (!match.Success || !symbolsByLine.TryGetValue(lineNumber, out var lineSymbols))
            return null;

        var name = match.Groups["name"].Value;
        return lineSymbols.FirstOrDefault(symbol =>
            symbol.Kind == "function"
            && string.Equals(symbol.Name, name, StringComparison.Ordinal));
    }

    private static SymbolRecord? FindFunctionalTypeDefinition(
        int lineNumber,
        IReadOnlyDictionary<int, List<SymbolRecord>> symbolsByLine)
    {
        if (!symbolsByLine.TryGetValue(lineNumber, out var lineSymbols))
            return null;

        return lineSymbols.FirstOrDefault(symbol =>
            symbol.Kind is "class" or "struct" or "interface" or "protocol" or "type");
    }

    private static void PrepareFunctionalCallableState(
        string language,
        string maskedLine,
        SymbolRecord? definition,
        FunctionalReferenceState state)
    {
        if (language == "ocaml"
            && state.ActiveCallable != null
            && maskedLine.Length > 0
            && !char.IsWhiteSpace(maskedLine[0])
            && Regex.IsMatch(
                maskedLine,
                @"^(?:let|module|type|class|exception|external)\b",
                RegexOptions.CultureInvariant,
                ExtractionRegexTimeout))
        {
            state.ActiveCallable = null;
        }

        if (definition == null)
            return;

        state.ActiveCallable = definition;
        state.CallableBaseDepth = language switch
        {
            "clojure" => state.ClojureParenDepth,
            "raku" => state.RakuBraceDepth,
            _ => 0,
        };
        if (language == "raku")
            state.RakuCallableBodyOpened = maskedLine.Contains('{');
    }

    private static void AdvanceFunctionalCallableState(
        string language,
        string maskedLine,
        FunctionalReferenceState state)
    {
        switch (language)
        {
            case "clojure":
                state.ClojureParenDepth += CountDelimiterDelta(maskedLine, '(', ')');
                if (state.ActiveCallable != null && state.ClojureParenDepth <= state.CallableBaseDepth)
                    state.ActiveCallable = null;
                if (state.ClojureProtocolMode && state.ClojureParenDepth <= state.ClojureProtocolBaseDepth)
                    state.ClojureProtocolMode = false;
                if (state.ClojureTypeBodyMode && state.ClojureParenDepth <= state.ClojureTypeBodyBaseDepth)
                {
                    state.ClojureTypeBodyMode = false;
                    state.ClojureActiveTypeDefinition = null;
                }
                break;
            case "erlang":
                if (state.ActiveCallable != null
                    && TrimmedFunctionalLineEndsWith(maskedLine, '.'))
                    state.ActiveCallable = null;
                break;
            case "raku":
                state.RakuBraceDepth += CountDelimiterDelta(maskedLine, '{', '}');
                if (state.ActiveCallable != null)
                {
                    if (!state.RakuCallableBodyOpened && maskedLine.Contains('{'))
                        state.RakuCallableBodyOpened = true;

                    if ((state.RakuCallableBodyOpened && state.RakuBraceDepth <= state.CallableBaseDepth)
                        || (!state.RakuCallableBodyOpened && maskedLine.Contains(';')))
                    {
                        state.ActiveCallable = null;
                        state.RakuCallableBodyOpened = false;
                    }
                }
                break;
        }
    }

    private static int CountDelimiterDelta(string line, char open, char close)
    {
        var delta = 0;
        foreach (var character in line)
        {
            if (character == open)
                delta++;
            else if (character == close)
                delta--;
        }

        return delta;
    }

    private static string MaskFunctionalReferenceLine(
        string language,
        string line,
        FunctionalReferenceState state)
    {
        if (language == "raku")
        {
            var trimmed = line.AsSpan().TrimStart();
            if (state.RakuHeredocTerminator != null)
            {
                if (TrimmedFunctionalLineEquals(
                        trimmed,
                        state.RakuHeredocTerminator))
                    state.RakuHeredocTerminator = null;
                return new string(' ', line.Length);
            }

            if (state.RakuPod)
            {
                if (trimmed.StartsWith("=end", StringComparison.Ordinal))
                    state.RakuPod = false;
                return new string(' ', line.Length);
            }

            if (trimmed.StartsWith("=begin", StringComparison.Ordinal)
                || trimmed.StartsWith("=for", StringComparison.Ordinal)
                || trimmed.StartsWith("=head", StringComparison.Ordinal))
            {
                state.RakuPod = trimmed.StartsWith("=begin", StringComparison.Ordinal);
                return new string(' ', line.Length);
            }
        }

        var masked = line.ToCharArray();
        for (var index = 0; index < masked.Length; index++)
        {
            if (language == "ocaml" && state.OcamlQuotedStringTerminator != null)
            {
                var terminatorIndex = line.IndexOf(
                    state.OcamlQuotedStringTerminator,
                    index,
                    StringComparison.Ordinal);
                if (terminatorIndex < 0)
                {
                    Array.Fill(masked, ' ', index, masked.Length - index);
                    break;
                }

                var terminatorEnd = terminatorIndex + state.OcamlQuotedStringTerminator.Length;
                Array.Fill(masked, ' ', index, terminatorEnd - index);
                index = terminatorEnd - 1;
                state.OcamlQuotedStringTerminator = null;
                continue;
            }

            if (language == "raku" && state.RakuQuoteCloseDelimiter != '\0')
            {
                masked[index] = ' ';
                if (line[index] == '\\' && index + 1 < masked.Length)
                {
                    masked[index + 1] = ' ';
                    index++;
                }
                else if (state.RakuQuoteOpenDelimiter != '\0' && line[index] == state.RakuQuoteOpenDelimiter)
                    state.RakuQuoteDepth++;
                else if (line[index] == state.RakuQuoteCloseDelimiter && --state.RakuQuoteDepth == 0)
                {
                    state.RakuQuoteOpenDelimiter = '\0';
                    state.RakuQuoteCloseDelimiter = '\0';
                }
                continue;
            }

            if (language == "ocaml" && state.OcamlCommentDepth > 0)
            {
                masked[index] = ' ';
                if (index + 1 < masked.Length && line[index] == '(' && line[index + 1] == '*')
                {
                    masked[index + 1] = ' ';
                    state.OcamlCommentDepth++;
                    index++;
                }
                else if (index + 1 < masked.Length && line[index] == '*' && line[index + 1] == ')')
                {
                    masked[index + 1] = ' ';
                    state.OcamlCommentDepth--;
                    index++;
                }
                continue;
            }

            if (language == "erlang" && state.ErlangQuotedAtom)
            {
                if (line[index] == '\\' && index + 1 < masked.Length)
                {
                    index++;
                }
                else if (line[index] == '\'')
                {
                    state.ErlangQuotedAtom = false;
                }
                continue;
            }

            if (state.StringDelimiter != '\0')
            {
                masked[index] = ' ';
                if (line[index] == '\\' && index + 1 < masked.Length)
                {
                    masked[index + 1] = ' ';
                    index++;
                }
                else if (line[index] == state.StringDelimiter)
                {
                    state.StringDelimiter = '\0';
                }
                continue;
            }

            if (language == "clojure" && line[index] == '\\')
            {
                var characterEnd = FindClojureCharacterLiteralEnd(line, index);
                Array.Fill(masked, ' ', index, characterEnd - index);
                index = characterEnd - 1;
                continue;
            }

            if (language == "erlang" && line[index] == '$' && index + 1 < masked.Length)
            {
                var characterEnd = index + 2;
                if (line[index + 1] == '\\' && characterEnd < masked.Length)
                    characterEnd++;
                Array.Fill(masked, ' ', index, characterEnd - index);
                index = characterEnd - 1;
                continue;
            }

            if (language == "ocaml"
                && line[index] == '\''
                && TryGetOcamlCharacterLiteralEnd(line, index, out var ocamlCharacterEnd))
            {
                Array.Fill(masked, ' ', index, ocamlCharacterEnd - index);
                index = ocamlCharacterEnd - 1;
                continue;
            }

            if (language == "ocaml"
                && TryStartOcamlQuotedString(line, index, out var ocamlOpeningEnd, out var ocamlTerminator))
            {
                Array.Fill(masked, ' ', index, ocamlOpeningEnd - index);
                state.OcamlQuotedStringTerminator = ocamlTerminator;
                index = ocamlOpeningEnd - 1;
                continue;
            }

            if (language == "ocaml" && index + 1 < masked.Length && line[index] == '(' && line[index + 1] == '*')
            {
                masked[index] = ' ';
                masked[index + 1] = ' ';
                state.OcamlCommentDepth++;
                index++;
                continue;
            }

            if (language == "raku"
                && TryStartRakuHeredoc(line, index, out var rakuHeredocEnd, out var rakuHeredocTerminator))
            {
                Array.Fill(masked, ' ', index, masked.Length - index);
                state.RakuHeredocTerminator = rakuHeredocTerminator;
                index = Math.Max(index, rakuHeredocEnd - 1);
                break;
            }

            if (language == "raku"
                && TryStartRakuQuoteOperator(
                    line,
                    index,
                    out var rakuOpeningEnd,
                    out var rakuOpenDelimiter,
                    out var rakuCloseDelimiter))
            {
                Array.Fill(masked, ' ', index, rakuOpeningEnd - index);
                state.RakuQuoteOpenDelimiter = rakuOpenDelimiter;
                state.RakuQuoteCloseDelimiter = rakuCloseDelimiter;
                state.RakuQuoteDepth = 1;
                index = rakuOpeningEnd - 1;
                continue;
            }

            if (language == "erlang" && line[index] == '\'')
            {
                state.ErlangQuotedAtom = true;
                continue;
            }

            var supportsSingleQuotedStrings = language == "raku";
            if (line[index] == '"' || (supportsSingleQuotedStrings && line[index] == '\''))
            {
                state.StringDelimiter = line[index];
                masked[index] = ' ';
                continue;
            }

            var isCommentStart = language switch
            {
                "clojure" => line[index] == ';',
                "erlang" => line[index] == '%',
                "raku" => line[index] == '#',
                _ => false,
            };
            if (isCommentStart)
            {
                Array.Fill(masked, ' ', index, masked.Length - index);
                break;
            }
        }

        return new string(masked);
    }

    private static int FindClojureCharacterLiteralEnd(string line, int start)
    {
        var index = Math.Min(line.Length, start + 2);
        while (index < line.Length
               && !char.IsWhiteSpace(line[index])
               && line[index] is not ('(' or ')' or '[' or ']' or '{' or '}' or '"' or ';' or ','))
        {
            index++;
        }

        return index;
    }

    private static bool TryGetOcamlCharacterLiteralEnd(string line, int start, out int end)
    {
        end = 0;
        var escaped = false;
        var limit = Math.Min(line.Length, start + 14);
        for (var index = start + 1; index < limit; index++)
        {
            var character = line[index];
            if (!escaped && character == '\'')
            {
                if (index == start + 1)
                    return false;
                end = index + 1;
                return true;
            }

            if (!escaped && character == '\\')
                escaped = true;
            else
                escaped = false;
        }

        return false;
    }

    private static bool TryStartOcamlQuotedString(
        string line,
        int start,
        out int openingEnd,
        out string terminator)
    {
        openingEnd = 0;
        terminator = string.Empty;
        if (line[start] != '{')
            return false;

        var index = start + 1;
        while (index < line.Length
               && (char.IsAsciiLetterOrDigit(line[index]) || line[index] is '_' or '\''))
        {
            index++;
        }
        if (index >= line.Length || line[index] != '|')
            return false;

        terminator = $"|{line[(start + 1)..index]}}}";
        openingEnd = index + 1;
        return true;
    }

    private static bool TryStartRakuQuoteOperator(
        string line,
        int start,
        out int openingEnd,
        out char openDelimiter,
        out char closeDelimiter)
    {
        openingEnd = 0;
        openDelimiter = '\0';
        closeDelimiter = '\0';
        if (!TryReadRakuQuotePrefix(line, start, out var delimiterIndex, out var isHeredoc)
            || isHeredoc)
            return false;

        openDelimiter = line[delimiterIndex];
        closeDelimiter = openDelimiter switch
        {
            '(' => ')',
            '[' => ']',
            '{' => '}',
            '<' => '>',
            '/' or '!' or '#' or '|' or '\'' or '"' => openDelimiter,
            _ => '\0',
        };
        if (closeDelimiter == '\0')
            return false;
        if (openDelimiter == closeDelimiter)
            openDelimiter = '\0';

        openingEnd = delimiterIndex + 1;
        return true;
    }

    private static bool TryStartRakuHeredoc(
        string line,
        int start,
        out int openingEnd,
        out string terminator)
    {
        openingEnd = 0;
        terminator = string.Empty;
        if (!TryReadRakuQuotePrefix(line, start, out var delimiterIndex, out var isHeredoc)
            || !isHeredoc)
        {
            return false;
        }

        var openDelimiter = line[delimiterIndex];
        var closeDelimiter = openDelimiter switch
        {
            '(' => ')',
            '[' => ']',
            '{' => '}',
            '<' => '>',
            _ => openDelimiter,
        };
        var closeIndex = line.IndexOf(closeDelimiter, delimiterIndex + 1);
        if (closeIndex <= delimiterIndex + 1)
            return false;

        terminator = line[(delimiterIndex + 1)..closeIndex].Trim();
        if (terminator.Length == 0)
            return false;

        openingEnd = closeIndex + 1;
        return true;
    }

    private static bool TryReadRakuQuotePrefix(
        string line,
        int start,
        out int delimiterIndex,
        out bool isHeredoc)
    {
        delimiterIndex = 0;
        isHeredoc = false;
        if (line[start] is not ('q' or 'Q')
            || (start > 0 && (char.IsLetterOrDigit(line[start - 1]) || line[start - 1] == '_')))
        {
            return false;
        }

        var index = start + 1;
        if (index < line.Length && line[index] == 'q')
            index++;
        while (index < line.Length && line[index] == ':')
        {
            var nameStart = ++index;
            while (index < line.Length
                   && (char.IsAsciiLetterOrDigit(line[index]) || line[index] is '_' or '-'))
            {
                index++;
            }
            if (nameStart == index)
                return false;
            if (string.Equals(line[nameStart..index], "to", StringComparison.Ordinal))
                isHeredoc = true;
        }

        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;
        if (index >= line.Length)
            return false;

        delimiterIndex = index;
        return true;
    }

}
