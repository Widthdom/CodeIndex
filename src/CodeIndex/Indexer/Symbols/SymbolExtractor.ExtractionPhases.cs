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
        private bool[]? _csharpSwitchExpressionLines;
        private bool _csharpSwitchExpressionLinesInitialized;
        private bool[]? _cssQualifiedRuleAncestors;
        private string[]? _javaScriptTypeScriptSanitizedLines;

        public PatternScanInputs(string lang, string? filePath, string[] lines)
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
            CSharpMatchLines = lang == "csharp"
                ? BuildCSharpMatchLines(lines, out csharpMatchColumnToRaw)
                : null;
            CSharpMatchColumnToRaw = csharpMatchColumnToRaw;
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
        public Func<CSharpLexState[]>? GetCSharpLineStartStates { get; }
        public Func<JavaScriptScopePrivacyFlags[][]>? GetPrivateScopeColumns { get; }
        public Func<bool[]?>? GetCSharpSwitchExpressionLines { get; }
        public Func<bool[]?>? GetCssQualifiedRuleAncestors { get; }

        public string[] GetJavaScriptTypeScriptSanitizedLines() =>
            _javaScriptTypeScriptSanitizedLines ??= BuildJavaScriptTypeScriptSanitizedLines(_lines);

        public DartClassBodyScope GetDartInsideClassBody() =>
            _dartInsideClassBody ??= BuildDartClassBodyScope(StructuralLines);

        public CSharpTypeBodyScope GetCSharpInsideTypeBody() =>
            _csharpInsideTypeBody ??= BuildCSharpTypeBodyScope(StructuralLines);

        public CSharpCallableParameterScope GetCSharpCallableParameterScope() =>
            _csharpCallableParameterScope ??= BuildCSharpCallableParameterScope(
                StructuralLines,
                GetCSharpInsideTypeBody());

        private CSharpLexState[] BuildCSharpLineStartStates() =>
            _csharpLineStartStates ??= SymbolExtractor.BuildCSharpLineStartStates(_lines);

        private JavaScriptScopePrivacyFlags[][] BuildPrivateScopeColumns() =>
            _privateScopeColumns ??= BuildJavaScriptTypeScriptPrivateScopeColumns(_lines, _lang);

        private bool[]? BuildCSharpSwitchExpressionLines()
        {
            if (!_csharpSwitchExpressionLinesInitialized)
            {
                _csharpSwitchExpressionLinesInitialized = true;
                _csharpSwitchExpressionLines = LinesContain(StructuralLines, "switch", StringComparison.Ordinal)
                    ? FindCSharpSwitchExpressionLines(StructuralLines)
                    : null;
            }

            return _csharpSwitchExpressionLines;
        }

        private bool[] BuildCssQualifiedRuleAncestors() =>
            _cssQualifiedRuleAncestors ??= FindCssQualifiedRuleAncestors(CssScannerLines!);
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
        Dictionary<int, PrologMultilineHead>? prologMultilineHeads)
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
            ExtractCppSameLineClassBodyMembers(fileId, lines, symbols);
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
        string[] lines,
        SymbolExtractionList symbols,
        SymbolExtractionState extractionState,
        Func<CSharpLexState[]>? getCSharpLineStartStates,
        List<PendingRecordPrimaryComponents>? pendingRecordPrimaryComponents)
    {
        AssignContainers(symbols, lines, getCSharpLineStartStates);
        if (lang is "shell" or "powershell")
            AddScriptScopeSymbol(fileId, lines, symbols);
        if (lang == "csharp")
            NormalizeCSharpImplicitPartialConstructorReturnTypes(symbols);
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
