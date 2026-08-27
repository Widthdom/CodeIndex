using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private sealed class PatternScanInputs
    {
        private readonly string _lang;
        private readonly string[] _lines;
        private CSharpLexState[]? _csharpLineStartStates;
        private DartClassBodyScope? _dartInsideClassBody;
        private JavaScriptScopePrivacyFlags[][]? _privateScopeColumns;
        private CSharpTypeBodyScope? _csharpInsideTypeBody;
        private CSharpCallableParameterScope? _csharpCallableParameterScope;
        private CSharpDeclarationStartScope? _csharpDeclarationStartScope;
        private bool[]? _csharpSwitchExpressionLines;
        private bool _csharpSwitchExpressionLinesInitialized;
        private bool[]? _cssQualifiedRuleAncestors;
        private string[]? _javaScriptTypeScriptSanitizedLines;

        public PatternScanInputs(
            string lang,
            string? filePath,
            string[] lines,
            IReadOnlyList<SymbolPattern> applicablePatterns,
            bool applyRequiredLiteralMatchInputGate,
            RequiredLiteralGateCounts? requiredLiteralGateCounts,
            bool applyCSharpRegexProbeOptimizations,
            CSharpRegexProbeCounts? csharpRegexProbeCounts)
        {
            _lang = lang;
            _lines = lines;
            PythonModulePrefix = lang == "python"
                ? GetPythonModulePrefix(filePath)
                : null;

            var structuralMaskLanguage = lang == "cython" ? "python" : lang;
            var structuralLines = StructuralLineMasker.MaskLines(structuralMaskLanguage, lines);
            if (lang is "d" or "julia" or "matlab" or "nim")
                structuralLines = ScientificNativeCommentMasker.MaskBlockComments(lang, structuralLines);
            structuralLines = DynamicDeclarativeReferenceExtractor.MaskNonCodeLines(
                lang,
                structuralLines);
            if (lang == "tcl")
            {
                structuralLines = DynamicDeclarativeReferenceExtractor.MaskTclContinuedCommentLines(
                    lines,
                    structuralLines);
                structuralLines = DynamicDeclarativeReferenceExtractor.MaskTclNonScriptLines(
                    structuralLines);
            }

            StructuralLines = structuralLines;
            ScientificBodyScannerLines = lang is "julia" or "matlab"
                ? PrepareScientificBodyScannerLines(structuralLines, lang)
                : null;
            MatlabExplicitOuterClosureByLine = lang == "matlab" && ScientificBodyScannerLines != null
                ? BuildMatlabExplicitOuterClosureMap(ScientificBodyScannerLines)
                : null;
            CssScannerLines = lang == "css"
                ? MaskCssScannerLines(lines)
                : null;
            SassStylusScannerLines = lang is "sass" or "stylus"
                ? MaskSassStylusBlockCommentLines(lang, lines)
                : null;
            ShellScannerLines = lang == "shell"
                ? MaskShellHeredocLines(lines)
                : null;
            if (lang is "prolog" or "ambiguous_pl")
            {
                PrologMultilineHeads = [];
                PrologClauseContinuationLines = BuildPrologClauseContinuationLines(
                    structuralLines,
                    PrologMultilineHeads);
            }
            PowershellEnumBodyLines = lang == "powershell"
                ? FindPowerShellEnumBodyLines(structuralLines)
                : null;

            int[]?[] csharpMatchColumnToRaw = null!;
            string[]? csharpScopeLines = null;
            bool[]? csharpTestMethodAttributedDeclarationLines = null;
            CSharpMatchLines = lang == "csharp"
                ? BuildCSharpMatchLines(
                    lines,
                    applicablePatterns,
                    applyRequiredLiteralMatchInputGate,
                    requiredLiteralGateCounts,
                    applyCSharpRegexProbeOptimizations,
                    csharpRegexProbeCounts,
                    out csharpMatchColumnToRaw,
                    out csharpScopeLines,
                    out csharpTestMethodAttributedDeclarationLines)
                : null;
            CSharpMatchColumnToRaw = csharpMatchColumnToRaw;
            CSharpScopeLines = csharpScopeLines;
            CSharpTestMethodAttributedDeclarationLines = csharpTestMethodAttributedDeclarationLines;
            GetCSharpLineStartStates = lang == "csharp"
                ? BuildCSharpLineStartStates
                : null;
            GetPrivateScopeColumns = lang is "javascript" or "typescript"
                ? BuildPrivateScopeColumns
                : null;
            GetCSharpSwitchExpressionLines = lang == "csharp"
                ? BuildCSharpSwitchExpressionLines
                : null;
            GetCssQualifiedRuleAncestors = lang == "css"
                ? BuildCssQualifiedRuleAncestors
                : null;
        }

        public string? PythonModulePrefix { get; }
        public string[] StructuralLines { get; }
        public string[]? ScientificBodyScannerLines { get; }
        public bool[]? MatlabExplicitOuterClosureByLine { get; }
        public string[]? CssScannerLines { get; }
        public string[]? SassStylusScannerLines { get; }
        public string[]? ShellScannerLines { get; }
        public bool[]? PrologClauseContinuationLines { get; }
        public Dictionary<int, PrologMultilineHead>? PrologMultilineHeads { get; }
        public bool[]? PowershellEnumBodyLines { get; }
        public int[]?[] CSharpMatchColumnToRaw { get; }
        public string[]? CSharpMatchLines { get; }
        public string[]? CSharpScopeLines { get; }
        public bool[]? CSharpTestMethodAttributedDeclarationLines { get; }
        public Func<CSharpLexState[]>? GetCSharpLineStartStates { get; }
        public Func<JavaScriptScopePrivacyFlags[][]>? GetPrivateScopeColumns { get; }
        public Func<bool[]?>? GetCSharpSwitchExpressionLines { get; }
        public Func<bool[]?>? GetCssQualifiedRuleAncestors { get; }

        public string[] GetJavaScriptTypeScriptSanitizedLines() =>
            _javaScriptTypeScriptSanitizedLines ??= BuildJavaScriptTypeScriptSanitizedLines(_lines);

        public DartClassBodyScope GetDartInsideClassBody() =>
            _dartInsideClassBody ??= BuildDartClassBodyScope(StructuralLines);

        public CSharpTypeBodyScope GetCSharpInsideTypeBody() =>
            _csharpInsideTypeBody ??= BuildCSharpTypeBodyScope(CSharpScopeLines!);

        public CSharpCallableParameterScope GetCSharpCallableParameterScope() =>
            _csharpCallableParameterScope ??= BuildCSharpCallableParameterScope(
                CSharpScopeLines!,
                GetCSharpInsideTypeBody());

        public CSharpDeclarationStartScope GetCSharpDeclarationStartScope() =>
            _csharpDeclarationStartScope ??= BuildCSharpDeclarationStartScope(
                CSharpScopeLines!,
                GetCSharpInsideTypeBody());

        private CSharpLexState[] BuildCSharpLineStartStates() =>
            _csharpLineStartStates ??= SymbolExtractor.BuildCSharpLineStartStates(_lines);

        private JavaScriptScopePrivacyFlags[][] BuildPrivateScopeColumns()
        {
            if (_privateScopeColumns != null)
                return _privateScopeColumns;

            // Keep the raw-text fast gate ahead of snapshot creation. Files with neither a
            // block nor an arrow cannot introduce a private function/class scope, so they do
            // not need the JS/TS lexer solely for this map.
            // block / arrow のない file は scope map 専用の lex を行わない。
            _privateScopeColumns = !LinesContainAny(_lines, '{', "=>", StringComparison.Ordinal)
                ? BuildEmptyJavaScriptTypeScriptPrivateScopeColumns(_lines.Length)
                : BuildJavaScriptTypeScriptPrivateScopeColumns(
                    GetJavaScriptTypeScriptSanitizedLines(),
                    _lang);
            return _privateScopeColumns;
        }

        private bool[]? BuildCSharpSwitchExpressionLines()
        {
            if (!_csharpSwitchExpressionLinesInitialized)
            {
                _csharpSwitchExpressionLinesInitialized = true;
                _csharpSwitchExpressionLines = LinesContain(CSharpScopeLines!, "switch", StringComparison.Ordinal)
                    ? FindCSharpSwitchExpressionLines(CSharpScopeLines!)
                    : null;
            }

            return _csharpSwitchExpressionLines;
        }

        private bool[] BuildCssQualifiedRuleAncestors() =>
            _cssQualifiedRuleAncestors ??= FindCssQualifiedRuleAncestors(CssScannerLines!);
    }

    private struct PatternScanState
    {
        public PatternScanState()
        {
            FSharpTypeBodyState = FSharpTypeBodyState.None;
            GoImportBlock = false;
            CSharpSuppressedContinuationUntil = -1;
            CSharpSuppressedContinuationResumeLine = -1;
            CSharpSuppressedContinuationResumeRawColumn = 0;
        }

        public FSharpTypeBodyState FSharpTypeBodyState;
        public bool GoImportBlock;
        public int CSharpSuppressedContinuationUntil;
        public int CSharpSuppressedContinuationResumeLine;
        public int CSharpSuppressedContinuationResumeRawColumn;
    }

    private readonly record struct PreparedPatternLine(
        string SourceLine,
        string MatchLine,
        string? CssScannerLine,
        FortranContinuationMatchCandidate? FortranContinuationCandidate,
        int PatternStartOffset,
        int PrologContinuationResumeOffset);

    private static bool TryPreparePatternLine(
        long fileId,
        string lang,
        string? filePath,
        string? projectRoot,
        string[] lines,
        PatternScanInputs scanInputs,
        ref PatternScanState scanState,
        SymbolExtractionList symbols,
        SymbolExtractionState extractionState,
        HashSet<string>? dockerfileStageNames,
        int lineIndex,
        out PreparedPatternLine preparedLine)
    {
        preparedLine = default;
        if (lang == "csharp" && lineIndex <= scanState.CSharpSuppressedContinuationUntil)
            return false;

        var line = lines[lineIndex];
        if (lang == "csharp" && IsCSharpLineCommentOnly(line))
            return false;

        if (lang == "go"
            && TryHandleGoBlockLine(
                fileId,
                line,
                lineIndex,
                symbols,
                extractionState,
                ref scanState.GoImportBlock))
        {
            return false;
        }

        if (lang == "go")
            TryAddGoLabelSymbol(fileId, line, lineIndex, symbols, extractionState);
        if (lang == "r"
            && TryAddRPacmanPackageLoaderSymbols(
                fileId,
                line,
                lineIndex + 1,
                symbols,
                extractionState))
        {
            return false;
        }

        if (lang == "dockerfile")
            AddDockerfileAdditionalSymbols(fileId, line, lineIndex + 1, symbols, dockerfileStageNames!);

        var structuralLine = scanInputs.StructuralLines[lineIndex];
        var cssScannerLine = scanInputs.CssScannerLines?[lineIndex];
        var matchLine = structuralLine;
        if (lang == "css" && cssScannerLine != null)
        {
            // Use raw CSS text for symbol-name matching so quoted selector payloads and
            // @import values stay queryable, while brace/depth scans still rely on the
            // separately masked scanner lines.
            // CSS のシンボル名マッチは raw line を使い、引用付きセレクタや @import 値を
            // 保持する。brace/depth 判定だけ別の scanner line を使う。
            matchLine = line;
        }
        else if (lang is "sass" or "stylus" && scanInputs.SassStylusScannerLines != null)
        {
            matchLine = scanInputs.SassStylusScannerLines[lineIndex];
        }
        else if (lang == "shell" && scanInputs.ShellScannerLines != null)
        {
            matchLine = scanInputs.ShellScannerLines[lineIndex];
        }
        else if (lang == "csharp")
        {
            matchLine = scanInputs.CSharpMatchLines![lineIndex];
        }

        var fortranContinuationCandidate = lang == "fortran"
            ? TryBuildFortranContinuationMatchLine(lines, lineIndex)
            : null;
        if (fortranContinuationCandidate != null)
            matchLine = fortranContinuationCandidate.Value.MatchLine;

        if (lang == "fsharp")
        {
            TryAddFSharpTypeMemberSymbols(
                symbols,
                fileId,
                line,
                lineIndex + 1,
                ref scanState.FSharpTypeBodyState);
        }

        if (lang == "fsharp"
            && TryAddFSharpRecordFieldsFromContext(
                symbols,
                fileId,
                lines,
                lineIndex,
                line,
                lineIndex + 1))
        {
            return false;
        }

        if (lang == "fsharp"
            && TryAddFSharpActivePatternSymbols(symbols, fileId, line, lineIndex + 1))
        {
            return false;
        }

        if (lang == "fsharp"
            && TryAddFSharpOperatorSymbols(symbols, fileId, line, lineIndex + 1))
        {
            return false;
        }

        if (lang == "php")
            ExtractPhpImportSymbols(symbols, line, lineIndex + 1);

        if (lang is "javascript" or "typescript")
        {
            AddJavaScriptTypeScriptModuleSymbolsForLine(
                fileId,
                lang,
                filePath,
                projectRoot,
                lines,
                scanInputs,
                lineIndex,
                symbols);
        }

        if (lang is "javascript" or "typescript"
            && TryHandleJavaScriptTypeScriptImportEqualsLine(
                fileId,
                lang,
                filePath,
                projectRoot,
                line,
                lineIndex + 1,
                symbols))
        {
            return false;
        }

        if (lang == "cpp" && TryAddCppIndentedAlias(fileId, line, lineIndex + 1, symbols))
            return false;

        // Batch `rem` / `@rem` / `::` comment lines contain the same `&` / `(` / `else` /
        // `do` boundary tokens that the property regex now accepts for inline `set`
        // capture, so `REM & set FAKE=1` or `:: else set FAKE=2` would otherwise leak a
        // phantom property. Short-circuit those lines before any pattern fires — batch
        // labels never match on `::` / `rem` lines anyway because the label regex
        // requires `:<name-char>`, not `::` or `r`.
        // batch の `rem` / `@rem` / `::` コメント行は、inline `set` 捕捉のために property 正規表現が
        // 受け付ける `&` / `(` / `else` / `do` の境界トークンを含みうるため、`REM & set FAKE=1` や
        // `:: else set FAKE=2` が偽 property を出す恐れがある。パターン適用前に当該行ごと
        // 早期スキップする — batch ラベル側は `::` / `rem` 行ではそもそも `:<名前文字>` の要件を
        // 満たさないため影響を受けない。
        if (lang == "batch" && IsBatchCommentLine(line))
            return false;

        if (string.IsNullOrWhiteSpace(matchLine))
            return false;

        var patternStartOffset = lang is "javascript" or "typescript"
            ? FindNextJavaScriptTypeScriptStatementStart(matchLine, 0)
            : 0;
        if (lang == "csharp" && patternStartOffset == 0)
        {
            var firstNonWhitespace = 0;
            while (firstNonWhitespace < matchLine.Length && char.IsWhiteSpace(matchLine[firstNonWhitespace]))
                firstNonWhitespace++;

            if (firstNonWhitespace < matchLine.Length
                && matchLine[firstNonWhitespace] is '}' or ';' or '"')
            {
                patternStartOffset = FindNextSameLineNonClosingBraceStatementStart(
                    matchLine,
                    firstNonWhitespace + 1,
                    lang);
            }
        }

        if (lang == "csharp" && lineIndex == scanState.CSharpSuppressedContinuationResumeLine)
        {
            patternStartOffset = Math.Max(
                patternStartOffset,
                TranslateCSharpRawColumnToCollapsed(
                    scanInputs.CSharpMatchColumnToRaw,
                    lineIndex,
                    scanState.CSharpSuppressedContinuationResumeRawColumn,
                    matchLine.Length,
                    line.Length));
        }

        var prologContinuationResumeOffset = -1;
        if (scanInputs.PrologClauseContinuationLines?[lineIndex] == true)
        {
            var clauseTerminatorColumn = FindFirstTopLevelPrologClauseTerminator(matchLine);
            if (clauseTerminatorColumn >= 0)
            {
                prologContinuationResumeOffset = clauseTerminatorColumn + 1;
                patternStartOffset = Math.Max(patternStartOffset, prologContinuationResumeOffset);
            }
        }

        preparedLine = new PreparedPatternLine(
            line,
            matchLine,
            cssScannerLine,
            fortranContinuationCandidate,
            patternStartOffset,
            prologContinuationResumeOffset);
        return true;
    }

    private static void AddJavaScriptTypeScriptModuleSymbolsForLine(
        long fileId,
        string lang,
        string? filePath,
        string? projectRoot,
        string[] lines,
        PatternScanInputs scanInputs,
        int lineIndex,
        SymbolExtractionList symbols)
    {
        var line = lines[lineIndex];
        if (line.IndexOf("import", StringComparison.Ordinal) < 0
            && line.IndexOf("require", StringComparison.Ordinal) < 0
            && line.IndexOf("URL", StringComparison.Ordinal) < 0
            && line.IndexOf("importScripts", StringComparison.Ordinal) < 0
            && line.IndexOf("serviceWorker", StringComparison.Ordinal) < 0
            && line.IndexOf("register", StringComparison.Ordinal) < 0
            && line.IndexOf("addModule", StringComparison.Ordinal) < 0
            && line.IndexOf("Worker", StringComparison.Ordinal) < 0)
        {
            return;
        }

        var sanitizedLines = scanInputs.GetJavaScriptTypeScriptSanitizedLines();
        var sanitizedLine = sanitizedLines[lineIndex];
        if (sanitizedLine.IndexOf("import", StringComparison.Ordinal) >= 0)
        {
            ExtractJavaScriptTypeScriptDynamicImportSymbols(
                fileId,
                lang,
                filePath,
                projectRoot,
                lines,
                sanitizedLines,
                lineIndex,
                symbols);
            ExtractJavaScriptTypeScriptStaticImportModuleSymbols(
                fileId,
                lang,
                filePath,
                projectRoot,
                lines,
                sanitizedLines,
                lineIndex,
                symbols);
            ExtractJavaScriptTypeScriptImportMetaResolveModuleSymbols(
                fileId,
                lang,
                filePath,
                projectRoot,
                lines,
                sanitizedLines,
                lineIndex,
                symbols);
        }

        if (sanitizedLine.IndexOf("require", StringComparison.Ordinal) >= 0)
        {
            ExtractJavaScriptTypeScriptRequireModuleSymbols(
                fileId,
                lang,
                filePath,
                projectRoot,
                lines,
                sanitizedLines,
                lineIndex,
                symbols);
        }

        if (sanitizedLine.IndexOf("URL", StringComparison.Ordinal) >= 0)
        {
            ExtractJavaScriptTypeScriptNewUrlModuleSymbols(
                fileId,
                lang,
                filePath,
                projectRoot,
                lines,
                sanitizedLines,
                lineIndex,
                symbols);
        }

        if (sanitizedLine.IndexOf("importScripts", StringComparison.Ordinal) >= 0)
        {
            ExtractJavaScriptTypeScriptImportScriptsModuleSymbols(
                fileId,
                lang,
                filePath,
                projectRoot,
                lines,
                sanitizedLines,
                lineIndex,
                symbols);
        }

        if (sanitizedLine.IndexOf("serviceWorker", StringComparison.Ordinal) >= 0
            || sanitizedLine.IndexOf("register", StringComparison.Ordinal) >= 0)
        {
            ExtractJavaScriptTypeScriptServiceWorkerRegisterModuleSymbols(
                fileId,
                lang,
                filePath,
                projectRoot,
                lines,
                sanitizedLines,
                lineIndex,
                symbols);
        }

        if (sanitizedLine.IndexOf("addModule", StringComparison.Ordinal) >= 0)
        {
            ExtractJavaScriptTypeScriptWorkletAddModuleSymbols(
                fileId,
                lang,
                filePath,
                projectRoot,
                lines,
                sanitizedLines,
                lineIndex,
                symbols);
        }

        if (sanitizedLine.IndexOf("Worker", StringComparison.Ordinal) >= 0)
        {
            ExtractJavaScriptTypeScriptWorkerConstructorModuleSymbols(
                fileId,
                lang,
                filePath,
                projectRoot,
                lines,
                sanitizedLines,
                lineIndex,
                symbols);
        }
    }

    private static bool TryExtractSpecializedSymbols(
        long fileId,
        string? lang,
        string content,
        string? filePath,
        string? projectRoot,
        CancellationToken cancellationToken,
        out List<SymbolRecord> symbols)
    {
        switch (lang)
        {
            case "xml":
                {
                    var lines = SplitContentLines(content);
                    symbols = ExtractXmlSymbols(fileId, content, lines);
                    return true;
                }
            case "json":
                symbols = ExtractJsonSymbols(fileId, content, SplitContentLines(content));
                return true;
            case "jsonl":
                symbols = ExtractJsonLinesSymbols(fileId, content, SplitContentLines(content));
                return true;
            case "toml":
            case "gitignore":
            case "gitattributes":
            case "editorconfig":
            case "dockerignore":
            case "config":
                symbols = ExtractRepositoryMetadataSymbols(fileId, lang, SplitContentLines(content));
                return true;
            case "yaml":
                symbols = ExtractYamlSymbols(fileId, SplitContentLines(content));
                return true;
            case "msbuild":
                symbols = ExtractMsBuildSymbols(fileId, content, SplitContentLines(content));
                return true;
            case "solution":
                symbols = ExtractSolutionSymbols(fileId, SplitContentLines(content));
                return true;
            case "app_manifest":
                symbols = ExtractAppManifestSymbols(fileId, content, SplitContentLines(content));
                return true;
            case "dependency_manifest":
            case "dependency_lock":
                symbols = DependencyPackageExtractor.ExtractSymbols(
                    fileId,
                    content,
                    SplitContentLines(content),
                    filePath,
                    lang);
                return true;
            case "ambiguous_m":
                {
                    var matlabContent = AmbiguousMContentMasker.MaskComments(
                        content,
                        maskMatlabComments: true,
                        maskObjectiveCComments: true);
                    var objectiveCContent = AmbiguousMContentMasker.MaskComments(
                        content,
                        maskMatlabComments: true,
                        maskObjectiveCComments: true,
                        preserveObjectiveCModuloExpressions: true);
                    symbols = ExtractCore(
                        fileId,
                        "matlab",
                        matlabContent,
                        contentIsNormalized: true,
                        hasOversizeLine: false,
                        conflictMarkerLine: 0,
                        filePath,
                        projectRoot,
                        patternConfigsAlreadyLoaded: true,
                        cancellationToken);
                    symbols.AddRange(ExtractCore(
                        fileId,
                        "objc",
                        objectiveCContent,
                        contentIsNormalized: true,
                        hasOversizeLine: false,
                        conflictMarkerLine: 0,
                        filePath,
                        projectRoot,
                        patternConfigsAlreadyLoaded: true,
                        cancellationToken));
                    return true;
                }
            case "markdown":
                {
                    var lines = SplitContentLines(content);
                    symbols = ExtractMarkdownSymbols(fileId, lines);
                    AssignContainers(symbols, lines, null);
                    PopulateDeclaredContainerQualifiedNames(symbols);
                    return true;
                }
            default:
                symbols = null!;
                return false;
        }
    }

    private static void AddSupplementalSymbols(
        long fileId,
        string? originalLang,
        string lang,
        string content,
        string? filePath,
        string[] lines,
        string[] structuralLines,
        SymbolExtractionList symbols,
        SymbolExtractionState extractionState,
        Func<JavaScriptScopePrivacyFlags[][]>? getPrivateScopeColumns,
        Func<string[]> getJavaScriptTypeScriptSanitizedLines,
        string[]? csharpMatchLines,
        string? pythonModulePrefix,
        Dictionary<int, PrologMultilineHead>? prologMultilineHeads,
        IReadOnlyList<SymbolPattern> applicablePatterns,
        bool applyRequiredLiteralMatchInputGate,
        RequiredLiteralGateCounts? requiredLiteralGateCounts)
    {
        if (lang == "javascript")
            ExtractJavaScriptBareMethods(fileId, lines, symbols, getPrivateScopeColumns!, getJavaScriptTypeScriptSanitizedLines);
        else if (lang == "typescript")
            ExtractTypeScriptBareMethods(fileId, lines, symbols, getPrivateScopeColumns!, getJavaScriptTypeScriptSanitizedLines);
        else if (lang == "csharp")
            ExtractCSharpEnumMembers(fileId, lines, structuralLines, csharpMatchLines!, symbols);
        else if (lang == "java")
        {
            ExtractJavaEnumMembers(fileId, lines, symbols);
            ExtractJavaCompactConstructors(fileId, lines, symbols);
            ExtractJavaModuleDirectiveSymbols(fileId, lines, structuralLines, symbols, extractionState);
        }
        else if (lang == "vb")
            ExtractVisualBasicEnumMembers(fileId, lines, symbols);

        if (lang == "cobol")
            ExtractCobolParagraphSymbols(fileId, lines, symbols, extractionState);
        if (string.Equals(originalLang, "svelte", StringComparison.Ordinal))
            ExtractSvelteReactiveSymbols(fileId, lines, symbols);
        if (lang == "rust")
        {
            ExtractRustUseSymbols(fileId, lines, symbols, extractionState);
            ExtractRustMultilineImplSymbols(fileId, lines, symbols, extractionState);
            ExtractRustAssociatedTypeDefaultSymbols(fileId, lines, structuralLines, symbols);
        }
        if (lang == "go")
            ExtractGoGroupedDeclarations(fileId, lines, symbols, extractionState);
        if (lang == "cpp")
        {
            ExtractCppSameLineClassBodyMembers(
                fileId,
                lines,
                applicablePatterns,
                symbols,
                applyRequiredLiteralMatchInputGate,
                requiredLiteralGateCounts);
            ExtractCppBalancedCallableSymbols(fileId, lines, structuralLines, symbols, extractionState);
            ExtractCppFriendDeclarationSymbols(fileId, lines, symbols, extractionState);
        }
        if (lang is "verilog" or "systemverilog")
            ExtractHdlInlineParameterSymbols(fileId, lines, symbols, extractionState);
        if (string.Equals(NormalizePluginLanguage(originalLang), "cuda", StringComparison.Ordinal))
            ClassifyCudaFunctionSubKinds(symbols);
        if (lang == "python")
        {
            ExtractPythonAllExportSymbols(fileId, lines, symbols, pythonModulePrefix);
            ExtractPythonClassAttributeSymbols(fileId, lines, symbols);
            ExtractPythonWalrusSymbols(fileId, lines, symbols);
        }
        if (lang == "perl")
            ExtractPerlHashConstantSymbols(fileId, lines, symbols, extractionState);
        if (lang == "php")
        {
            ExtractPhpAdditionalPropertySymbols(fileId, lines, symbols);
            ExtractPhpPromotedConstructorProperties(fileId, lines, symbols);
            ExtractPhpDocblockMethodSymbols(fileId, lines, symbols);
            ExtractPhpDocblockPropertySymbols(fileId, lines, symbols);
            ExtractPhpTraitAliasSymbols(fileId, lines, symbols);
            ExtractPhpDocblockTypeAliasSymbols(fileId, lines, symbols);
            ExtractPhpDocblockImportTypeSymbols(fileId, lines, symbols);
            ExtractPhpPropertyHookSupplementalSymbols(fileId, lines, structuralLines, symbols);
        }
        if (lang == "swift")
            ExtractSwiftPropertySupplementalSymbols(fileId, lines, structuralLines, symbols);
        if (lang == "sql")
        {
            var sqlSyntheticSymbolLines = MaskSqlSyntheticSymbolLines(lines);
            ExtractSqlCteSymbols(fileId, content, lines, symbols, extractionState);
            ExtractSqlDefinerSymbols(fileId, lines, sqlSyntheticSymbolLines, symbols, extractionState);
            ExtractSqlRoutineResultColumnSymbols(fileId, lines, sqlSyntheticSymbolLines, symbols, extractionState);
            ExtractSqlGeneratedColumnSymbols(fileId, lines, sqlSyntheticSymbolLines, symbols, extractionState);
        }
        if (lang == "graphql")
            ExtractGraphQLMemberSymbols(fileId, content, lines, symbols);
        if (lang is "csharp" or "python" or "javascript" or "typescript")
            ExtractSectionHeadingSymbols(fileId, lang, lines, symbols);
        if (IsRazorLanguage(originalLang) || IsRazorFilePath(filePath))
            ExtractRazorDirectiveSymbols(fileId, lines, symbols);
        if (prologMultilineHeads is { Count: > 0 })
        {
            AddPrologMultilineHeadSymbols(
                fileId,
                lines,
                symbols,
                extractionState,
                prologMultilineHeads);
        }
        if (lang == "tcl")
        {
            DynamicDeclarativeReferenceExtractor.AddTclInlineProcSymbols(
                fileId,
                lines,
                structuralLines,
                symbols);
        }
    }

    private static void FinalizePatternSymbols(
        long fileId,
        string lang,
        string? filePath,
        string? projectRoot,
        string[] lines,
        string[] structuralLines,
        SymbolExtractionList symbols,
        SymbolExtractionState extractionState,
        Func<CSharpLexState[]>? getCSharpLineStartStates,
        List<PendingRecordPrimaryComponents>? pendingRecordPrimaryComponents)
    {
        AssignContainers(
            symbols,
            lines,
            getCSharpLineStartStates,
            filePath,
            projectRoot);
        if (lang is "shell" or "powershell")
            AddScriptScopeSymbol(fileId, lines, symbols);
        if (lang == "csharp")
        {
            AddCSharpTopLevelScopeSymbol(fileId, lines, structuralLines, symbols);
            NormalizeCSharpImplicitPartialConstructorReturnTypes(symbols);
        }
        if (lang == "go")
        {
            AssignGoMethodReceiverContainers(symbols);
            ClassifyGoFunctionRoles(symbols, filePath);
        }
        MaterializeRecordPrimaryComponentSymbols(symbols, pendingRecordPrimaryComponents);
        if (lang is "javascript" or "typescript")
            ClassifyJavaScriptTypeScriptReactHooks(symbols);
        if (lang == "scala")
            ClassifyScalaCompanions(symbols);
        KotlinSymbolNameNormalizer.NormalizeSecondaryConstructorNames(symbols);
        if (lang == "shell")
            ExpandShellAliasSymbols(fileId, lines, symbols, extractionState);
        PopulateDeclaredContainerQualifiedNames(symbols);
        if (lang == "nim")
        {
            foreach (var symbol in symbols)
                symbol.IdentityNameFolded = NimIdentifierIdentity.Fold(symbol.Name);
        }
    }
}
