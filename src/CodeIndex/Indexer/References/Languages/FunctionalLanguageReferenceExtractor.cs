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
        "finally", "throw", "quote", "var", "set!", "new", ".",
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
        @"^\s*-(?:spec|callback)\b",
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
        internal int ClojureParenDepth;
        internal int CallableBaseDepth;
        internal int RakuBraceDepth;
        internal bool RakuCallableBodyOpened;
        internal char RakuQuoteOpenDelimiter;
        internal char RakuQuoteCloseDelimiter;
        internal int RakuQuoteDepth;
        internal bool ErlangQuotedAtom;
        internal bool ErlangSpecificationMode;
        internal string? OcamlQuotedStringTerminator;
        internal SymbolRecord? ActiveCallable;
    }

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
                        request.FileId, maskedLine, context, lineNumber, definition, typeDefinition, container, references, seen);
                    break;
                case "raku":
                    EmitRakuReferences(
                        request.FileId, maskedLine, context, lineNumber, definition, typeDefinition, container, references, seen);
                    break;
            }

            AdvanceFunctionalCallableState(request.Language, maskedLine, state);
        }

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
                break;
            case "erlang":
                if (state.ActiveCallable != null && maskedLine.TrimEnd().EndsWith(".", StringComparison.Ordinal))
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
            var trimmed = line.TrimStart();
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
        if (line[start] is not ('q' or 'Q')
            || (start > 0 && (char.IsLetterOrDigit(line[start - 1]) || line[start - 1] == '_')))
        {
            return false;
        }

        var index = start + 1;
        if (index < line.Length && line[index] == 'q')
            index++;
        if (index >= line.Length)
            return false;

        openDelimiter = line[index];
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

        openingEnd = index + 1;
        return true;
    }

    private static void EmitClojureReferences(
        long fileId,
        string line,
        string context,
        int lineNumber,
        SymbolRecord? typeDefinition,
        SymbolRecord? container,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        FunctionalReferenceState state)
    {
        if (line.Contains(":require", StringComparison.Ordinal))
            state.ClojureRequireMode = true;

        if (state.ClojureRequireMode)
        {
            EmitClojureRequireEntries();
            if (line.Contains(')'))
            {
                state.ClojureRequireMode = false;
                state.ClojureRequireBracketDepth = 0;
            }
        }

        var relationMatch = ClojureTypeRelationRegex.Match(line);
        if (relationMatch.Success)
        {
            var typeContainer = typeDefinition ?? container;
            var types = relationMatch.Groups["types"];
            foreach (Match typeMatch in Regex.Matches(
                         types.Value,
                         @"(?<name>[A-Z][\w.*+!?<>=-]*)",
                         RegexOptions.CultureInvariant,
                         ExtractionRegexTimeout))
            {
                AddReference(
                    references,
                    seen,
                    fileId,
                    typeMatch.Groups["name"].Value,
                    types.Index + typeMatch.Groups["name"].Index,
                    "type_reference",
                    context,
                    lineNumber,
                    typeContainer,
                    "clojure");
            }
        }

        var isProtocolHeader = Regex.IsMatch(
            line,
            @"^\s*\(\s*defprotocol\b",
            RegexOptions.CultureInvariant,
            ExtractionRegexTimeout);
        if (isProtocolHeader)
        {
            state.ClojureProtocolMode = true;
            state.ClojureProtocolBaseDepth = state.ClojureParenDepth;
        }

        if (state.ClojureProtocolMode
            || Regex.IsMatch(
                line,
                @"^\s*\(\s*(?:ns|defrecord|deftype|extend-type)\b",
                RegexOptions.CultureInvariant,
                ExtractionRegexTimeout))
        {
            return;
        }

        foreach (Match match in ClojureCallHeadRegex.Matches(line))
        {
            var fullName = match.Groups["name"].Value;
            var separator = fullName.LastIndexOf('/');
            var name = separator >= 0 ? fullName[(separator + 1)..] : fullName;
            if (ClojureIgnoredCallHeads.Contains(name))
                continue;

            AddReference(
                references,
                seen,
                fileId,
                name,
                match.Groups["name"].Index + Math.Max(0, separator + 1),
                "call",
                context,
                lineNumber,
                container,
                "clojure");
        }

        void EmitClojureRequireEntries()
        {
            for (var index = 0; index < line.Length; index++)
            {
                if (line[index] == '[')
                {
                    if (state.ClojureRequireBracketDepth == 0)
                    {
                        var match = ClojureRequireEntryRegex.Match(line, index);
                        if (match.Success && match.Index == index)
                        {
                            AddFunctionalReference(
                                references,
                                seen,
                                fileId,
                                match.Groups["name"],
                                "import",
                                context,
                                lineNumber,
                                container,
                                "clojure");
                            if (match.Groups["alias"].Success)
                            {
                                AddFunctionalReference(
                                    references,
                                    seen,
                                    fileId,
                                    match.Groups["name"],
                                    "alias",
                                    context,
                                    lineNumber,
                                    container,
                                    "clojure");
                            }
                        }
                    }

                    state.ClojureRequireBracketDepth++;
                }
                else if (line[index] == ']' && state.ClojureRequireBracketDepth > 0)
                {
                    state.ClojureRequireBracketDepth--;
                }
            }
        }
    }

    private static void EmitErlangReferences(
        long fileId,
        string line,
        string context,
        int lineNumber,
        SymbolRecord? container,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        FunctionalReferenceState state)
    {
        AddFunctionalMatchReference(ErlangImportRegex.Match(line), "import");
        AddFunctionalMatchReference(ErlangBehaviourRegex.Match(line), "type_reference");
        if (ErlangSpecificationAttributeRegex.IsMatch(line))
            state.ErlangSpecificationMode = true;
        if (state.ErlangSpecificationMode)
        {
            if (line.TrimEnd().EndsWith(".", StringComparison.Ordinal))
                state.ErlangSpecificationMode = false;
            return;
        }

        var remoteCallSpans = new List<(int Start, int End)>();
        foreach (Match match in ErlangRemoteCallRegex.Matches(line))
        {
            remoteCallSpans.Add((match.Index, match.Index + match.Length));
            AddFunctionalReference(references, seen, fileId, match.Groups["module"], "reference", context, lineNumber, container, "erlang");
            AddFunctionalReference(references, seen, fileId, match.Groups["name"], "call", context, lineNumber, container, "erlang");
        }

        var definitionMatch = ErlangFunctionDefinitionRegex.Match(line);
        foreach (Match match in ErlangLocalCallRegex.Matches(line))
        {
            if (remoteCallSpans.Any(span => match.Index >= span.Start && match.Index < span.End))
                continue;

            var name = match.Groups["name"].Value;
            if (ErlangIgnoredCalls.Contains(name))
                continue;

            if (definitionMatch.Success
                && match.Groups["name"].Index == definitionMatch.Groups["name"].Index)
            {
                continue;
            }

            AddFunctionalReference(references, seen, fileId, match.Groups["name"], "call", context, lineNumber, container, "erlang");
        }

        void AddFunctionalMatchReference(Match match, string kind)
        {
            if (match.Success)
                AddFunctionalReference(references, seen, fileId, match.Groups["name"], kind, context, lineNumber, container, "erlang");
        }
    }

    private static void EmitOcamlReferences(
        long fileId,
        string line,
        string context,
        int lineNumber,
        SymbolRecord? definition,
        SymbolRecord? typeDefinition,
        SymbolRecord? container,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen)
    {
        AddMatch(OcamlImportRegex.Match(line), "import");
        AddMatch(OcamlModuleAliasRegex.Match(line), "alias");
        var typeAliasTarget = OcamlTypeAliasTargetRegex.Match(line);
        if (typeAliasTarget.Success)
        {
            AddOcamlTypeReference(typeAliasTarget.Groups["name"]);
        }
        foreach (Match match in OcamlTypeReferenceRegex.Matches(line))
            AddOcamlTypeReference(match.Groups["name"]);

        if (Regex.IsMatch(
                line,
                @"^\s*(?:module|type|class|open|include|val|external)\b",
                RegexOptions.CultureInvariant,
                ExtractionRegexTimeout))
        {
            return;
        }

        var qualifiedCallSpans = new List<(int Start, int End)>();
        foreach (Match match in OcamlQualifiedCallRegex.Matches(line))
        {
            qualifiedCallSpans.Add((match.Index, match.Index + match.Length));
            AddFunctionalReference(references, seen, fileId, match.Groups["module"], "reference", context, lineNumber, container, "ocaml");
            AddFunctionalReference(references, seen, fileId, match.Groups["name"], "call", context, lineNumber, container, "ocaml");
        }

        var skippedDefinition = false;
        foreach (Match match in OcamlBareCallRegex.Matches(line))
        {
            if (qualifiedCallSpans.Any(span => match.Index >= span.Start && match.Index < span.End))
                continue;

            var name = match.Groups["name"].Value;
            if (OcamlIgnoredCalls.Contains(name))
                continue;

            if (!skippedDefinition
                && definition != null
                && string.Equals(definition.Name, name, StringComparison.Ordinal))
            {
                skippedDefinition = true;
                continue;
            }

            AddFunctionalReference(references, seen, fileId, match.Groups["name"], "call", context, lineNumber, container, "ocaml");
        }

        void AddMatch(Match match, string kind)
        {
            if (match.Success)
                AddFunctionalReference(references, seen, fileId, match.Groups["name"], kind, context, lineNumber, container, "ocaml");
        }

        void AddOcamlTypeReference(Group group)
        {
            if (group.Success && !OcamlIgnoredTypeReferences.Contains(group.Value))
            {
                AddFunctionalReference(
                    references,
                    seen,
                    fileId,
                    group,
                    "type_reference",
                    context,
                    lineNumber,
                    typeDefinition ?? container,
                    "ocaml");
            }
        }
    }

    private static void EmitRakuReferences(
        long fileId,
        string line,
        string context,
        int lineNumber,
        SymbolRecord? definition,
        SymbolRecord? typeDefinition,
        SymbolRecord? container,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen)
    {
        var importMatch = RakuImportRegex.Match(line);
        if (importMatch.Success)
        {
            AddFunctionalReference(references, seen, fileId, importMatch.Groups["name"], "import", context, lineNumber, container, "raku");
            if (importMatch.Groups["angleAlias"].Success || importMatch.Groups["alias"].Success)
                AddFunctionalReference(references, seen, fileId, importMatch.Groups["name"], "alias", context, lineNumber, container, "raku");
        }

        if (typeDefinition != null)
        {
            foreach (Match match in RakuTypeRelationRegex.Matches(line))
                AddFunctionalReference(references, seen, fileId, match.Groups["name"], "type_reference", context, lineNumber, typeDefinition, "raku");
        }
        foreach (Match match in RakuReturnTypeRegex.Matches(line))
            AddFunctionalReference(references, seen, fileId, match.Groups["name"], "type_reference", context, lineNumber, container, "raku");
        if (typeDefinition != null)
            return;

        var qualifiedCallSpans = new List<(int Start, int End)>();
        foreach (Match match in RakuQualifiedCallRegex.Matches(line))
        {
            qualifiedCallSpans.Add((match.Index, match.Index + match.Length));
            AddFunctionalReference(references, seen, fileId, match.Groups["module"], "reference", context, lineNumber, container, "raku");
            AddFunctionalReference(references, seen, fileId, match.Groups["name"], "call", context, lineNumber, container, "raku");
        }
        foreach (Match match in RakuMethodCallRegex.Matches(line))
        {
            qualifiedCallSpans.Add((match.Index, match.Index + match.Length));
            AddFunctionalReference(references, seen, fileId, match.Groups["name"], "call", context, lineNumber, container, "raku");
        }

        var skippedDefinition = false;
        foreach (Match match in RakuBareCallRegex.Matches(line))
        {
            if (qualifiedCallSpans.Any(span => match.Index >= span.Start && match.Index < span.End))
                continue;

            var name = match.Groups["name"].Value;
            if (RakuIgnoredCalls.Contains(name))
                continue;

            if (!skippedDefinition
                && definition != null
                && string.Equals(definition.Name, name, StringComparison.Ordinal))
            {
                skippedDefinition = true;
                continue;
            }

            AddFunctionalReference(references, seen, fileId, match.Groups["name"], "call", context, lineNumber, container, "raku");
        }
    }

    private static void AddFunctionalReference(
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        Group group,
        string referenceKind,
        string context,
        int lineNumber,
        SymbolRecord? container,
        string language)
    {
        if (!group.Success || ReferenceLimitReached(references))
            return;

        AddReference(
            references,
            seen,
            fileId,
            group.Value,
            group.Index,
            referenceKind,
            context,
            lineNumber,
            container,
            language);
    }
}
