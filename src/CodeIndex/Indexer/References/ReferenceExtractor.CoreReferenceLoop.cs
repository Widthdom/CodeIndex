using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private sealed class CoreReferenceLoopContext
    {
        internal required ReferenceExtractionContext Request { get; init; }
        internal required ReferenceLinePreparation Preparation { get; init; }
        internal required bool IsJsxFile { get; init; }
        internal required bool IsRazorFile { get; init; }
        internal required bool XamlReferenceEnabled { get; init; }
        internal required int ScientificNativeDependencyLimit { get; init; }
        internal required List<(int start, int end)>?[]? CSharpAttributeRanges { get; init; }
        internal required List<(int start, int end)>?[]? CSharpAttributeTopLevelRanges { get; init; }
        internal required StringComparer DefinitionNamesComparer { get; init; }
        internal required IReadOnlyDictionary<int, HashSet<string>> DefinitionNamesByLine { get; init; }
        internal IReadOnlyDictionary<int, Dictionary<string, HashSet<int>>>? ScientificDefinitionNameIndicesByLine { get; init; }
        internal IReadOnlySet<string>? AllDefinitionNames { get; init; }
        internal IReadOnlySet<string>? FileDefinitionNames { get; init; }
        internal Dictionary<int, List<SqlReferenceExtractor.DefinitionLeafSpan>>? SqlDefinitionLeafSpansByLine { get; init; }
        internal HashSet<(int LineNumber, int ColumnIndex)>? SqlWindowFunctionCallSiteSuppressions { get; init; }
        internal IReadOnlyList<SymbolRecord>? CobolCallableSymbols { get; init; }
        internal required IReadOnlyList<SymbolRecord> ContainerCandidates { get; init; }
        internal required InnermostContainerResolver ContainerResolver { get; init; }
        internal IReadOnlyDictionary<int, SymbolRecord[]>? SwiftPropertyDefinitionsByLine { get; init; }
        internal required IReadOnlyDictionary<string, List<(string EnumName, string? QualifiedEnumName, bool AllowShortNameFallback)>> CSharpQualifiedEnumMemberLookup { get; init; }
        internal required IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> CSharpQualifiedConstantPatternMemberLookup { get; init; }
        internal required IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> CSharpQualifiedTypePatternLookup { get; init; }
        internal HashSet<string>? KotlinConstructorTypeNames { get; init; }
        internal HashSet<string>? KotlinInfixFunctionNames { get; init; }
        internal HashSet<string>? CallableDefinitionNames { get; init; }
        internal HashSet<string>? StylusVariableDefinitionNames { get; init; }
        internal HashSet<string>? DockerfileStageNames { get; init; }
        internal HashSet<string>? DockerfileVariableNames { get; init; }
        internal HashSet<string>? ShellCallableNames { get; init; }
        internal HashSet<string>? ShellGlobalAliasNames { get; init; }
        internal DynamicDeclarativeReferenceExtractor.ExtractionState? DynamicDeclarativeState { get; init; }
        internal required IReadOnlyList<CSharpUsingAliasRecord> CSharpUsingAliases { get; init; }
        internal required IReadOnlyList<CSharpUsingStaticRecord> CSharpUsingStatics { get; init; }
        internal required CoreExtractionLookups Lookups { get; init; }
        internal IReadOnlyList<TypeScriptReferenceExtractor.TypeAliasBinding>? TypeScriptTypeAliases { get; init; }
        internal IReadOnlyList<SwiftReferenceExtractor.TypeAliasBinding>? SwiftTypeAliases { get; init; }
        internal required List<ReferenceRecord> References { get; init; }
        internal required ReferenceDedupeSet Seen { get; init; }
    }

    private sealed class CoreReferenceLoopMutableState
    {
        internal bool CSharpInDelimitedDocComment;
        internal bool JvmInDelimitedDocComment;
        internal bool PhpInDocblock;
        internal SymbolRecord? PhpDocblockContainer;
        internal HashSet<string>? PhpDocblockPropertyNames;
        internal MarkupSchemaReferenceExtractor.MarkupState? MarkupSchemaState;
        internal CssReferenceExtractor.SassLoudCommentState? SassPreparedCommentState;
        internal CssReferenceExtractor.SassLoudCommentState? SassOriginalCommentState;
        internal bool SassStylusPreparedInBlockComment;
        internal bool SassStylusOriginalInBlockComment;

        internal CoreReferenceLoopMutableState(string language)
        {
            if (language is "graphql" or "html" or "markdown")
            {
                MarkupSchemaState =
                    new MarkupSchemaReferenceExtractor.MarkupState();
            }
            if (language == "sass")
            {
                SassPreparedCommentState =
                    new CssReferenceExtractor.SassLoudCommentState();
                SassOriginalCommentState =
                    new CssReferenceExtractor.SassLoudCommentState();
            }
        }
    }

    private static CSharpMultiLineTypePatternState EmitCoreReferenceLines(
        CoreReferenceLoopContext loop)
    {
        var request = loop.Request;
        var fileId = request.FileId;
        var language = request.Language;
        var symbols = request.Symbols;
        var workspaceSymbols = request.WorkspaceSymbols;
        var input = loop.Preparation;
        var lines = input.Lines;
        var preparedLines = input.PreparedLines;
        var structuralLines = input.StructuralLines;
        var referenceStructuralLines = input.ReferenceStructuralLines;
        var references = loop.References;
        var seen = loop.Seen;
        var containerCandidates = loop.ContainerCandidates;
        var containerResolver = loop.ContainerResolver;
        var lookups = loop.Lookups;
        var csharpAttrRanges = loop.CSharpAttributeRanges;
        var csharpAttrTopLevelRanges = loop.CSharpAttributeTopLevelRanges;
        var csharpUsingAliases = loop.CSharpUsingAliases;
        var csharpUsingStatics = loop.CSharpUsingStatics;
        var dynamicDeclarativeState = loop.DynamicDeclarativeState;
        Func<string, bool> isIgnoredCallName =
            name => IsIgnoredCallName(language, name);
        var pendingCSharpMultiLineTypePattern =
            default(CSharpMultiLineTypePatternState);
        var pendingCSharpWhereConstraint = language == "csharp"
            ? new CSharpWhereConstraintState()
            : null;
        var csharpLocalNamesByFunction = language == "csharp"
            ? new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
            : null;
        var sqlState = language == "sql"
            ? SqlReferenceExtractor.CreateState()
            : null;
        var xamlInXmlComment = false;
        var xamlBindingPropertyElementState = language == "xml"
            ? new XamlReferenceExtractor.BindingPropertyElementState()
            : null;
        var xamlBindingMarkupExtensionState = language == "xml"
            ? new XamlReferenceExtractor.BindingMarkupExtensionState()
            : null;
        var mutableState = new CoreReferenceLoopMutableState(language);
        var shaderState = ShaderReferenceExtractor.CreateState(
            language,
            preparedLines,
            symbols,
            workspaceSymbols,
            request.ReportDiagnostic);

        for (var i = 0; i < lines.Length; i++)
        {
            if (ReferenceLimitReached(references))
                break;

            if ((i & 0x3f) == 0)
                request.CancellationToken.ThrowIfCancellationRequested();

            var lineNumber = i + 1;
            var originalLine = lines[i];
            var languageLines = PrepareCoreLanguageLine(
                loop,
                mutableState,
                i,
                originalLine);
            var preparedLine = languageLines.PreparedLine;
            var preparedLineIsWhiteSpace =
                string.IsNullOrWhiteSpace(preparedLine);
            var originalLineForLanguage =
                languageLines.OriginalLineForLanguage;
            var csharpAttrRangesOnLine = csharpAttrRanges?[i];
            var csharpAttrTopLevelOnLine = csharpAttrTopLevelRanges?[i];
            if (EmitCoreDocumentationAndSpecialLineReferences(
                    loop,
                    mutableState,
                    i,
                    originalLine,
                    preparedLine,
                    preparedLineIsWhiteSpace,
                    csharpAttrRangesOnLine,
                    out var sourceContext))
            {
                continue;
            }

            if (preparedLineIsWhiteSpace)
            {
                if (language == "csharp"
                    && (pendingCSharpMultiLineTypePattern.WaitingForHead
                        || pendingCSharpMultiLineTypePattern
                            .PendingTypeExpression != null))
                {
                    continue;
                }

                if (language == "csharp")
                {
                    CSharpReferenceExtractor
                        .FlushPendingMultiLineTypePatternReference(
                            ref pendingCSharpMultiLineTypePattern,
                            loop.CSharpQualifiedConstantPatternMemberLookup,
                            csharpUsingAliases,
                            csharpUsingStatics,
                            lookups.HasActiveSameFileCSharpTypeCandidate,
                            references,
                            seen,
                            fileId);
                }
                continue;
            }

            if (sourceContext.Length == 0)
                continue;

            var definitionNames =
                loop.DefinitionNamesByLine.TryGetValue(
                    lineNumber,
                    out var namesOnLine)
                    ? namesOnLine
                    : null;
            Dictionary<string, HashSet<int>>?
                scientificDefinitionNameIndices = null;
            loop.ScientificDefinitionNameIndicesByLine?.TryGetValue(
                lineNumber,
                out scientificDefinitionNameIndices);
            List<SqlReferenceExtractor.DefinitionLeafSpan>?
                sqlDefinitionLeafSpans = null;
            if (language == "sql")
            {
                loop.SqlDefinitionLeafSpansByLine?.TryGetValue(
                    lineNumber,
                    out sqlDefinitionLeafSpans);
            }
            var container = containerResolver.Find(lineNumber);
            var definitionState = new CoreLineDefinitionState(
                language,
                sourceContext,
                preparedLine,
                definitionNames,
                loop.DefinitionNamesComparer,
                scientificDefinitionNameIndices,
                sqlDefinitionLeafSpans);
            var csharpLineHasWhereClause = language == "csharp"
                && preparedLine.IndexOf(
                    "where",
                    StringComparison.Ordinal) >= 0
                && CSharpWhereClauseRegex.IsMatch(preparedLine);

            (SymbolRecord Synthetic, int NameIndex, int OpenBraceIndex,
                int CloseBraceIndex)? javaSameLineCtor = null;
            if (language == "java")
            {
                javaSameLineCtor =
                    JavaReferenceExtractor.TryBuildSameLineCtorSpan(
                        preparedLine,
                        lineNumber,
                        lookups.GetEnclosingTypeCandidates);
            }

            SymbolRecord? ResolveContainerForCall(int column)
            {
                if (language == "csharp")
                {
                    var containerMatchesPrimaryCtor = false;
                    foreach (var (
                                 rangeStart,
                                 rangeStartColumn,
                                 rangeEnd,
                                 rangeEndColumn,
                                 syntheticRecordCtor) in
                             lookups.GetRecordPrimaryCtorRanges())
                    {
                        containerMatchesPrimaryCtor |=
                            ReferenceEquals(container, syntheticRecordCtor)
                            || (container?.Kind == "function"
                                && container.FileId == syntheticRecordCtor.FileId
                                && container.StartLine == syntheticRecordCtor.StartLine
                                && string.Equals(
                                    container.Name,
                                    syntheticRecordCtor.Name,
                                    StringComparison.Ordinal));
                        if (lineNumber < rangeStart || lineNumber > rangeEnd)
                            continue;
                        if (lineNumber == rangeStart
                            && column < rangeStartColumn)
                        {
                            continue;
                        }
                        if (lineNumber == rangeEnd && column >= rangeEndColumn)
                            continue;
                        return syntheticRecordCtor;
                    }

                    if (containerMatchesPrimaryCtor)
                    {
                        return FindInnermostClassLike(
                            lookups.GetEnclosingTypeCandidates(),
                            lineNumber);
                    }
                }

                if (javaSameLineCtor != null)
                {
                    var info = javaSameLineCtor.Value;
                    if (info.CloseBraceIndex >= 0
                        && column > info.OpenBraceIndex
                        && column < info.CloseBraceIndex)
                    {
                        return info.Synthetic;
                    }
                }

                if (language == "csharp")
                {
                    if (csharpLineHasWhereClause)
                    {
                        var declarationRangeContainer =
                            FindInnermostCSharpDeclarationRangeContainer(
                                containerCandidates,
                                structuralLines[i],
                                lineNumber,
                                column);
                        if (declarationRangeContainer != null)
                            return declarationRangeContainer;
                    }

                    var sameLineContainer =
                        FindInnermostSameLineCSharpContainer(
                            lookups
                                .GetCSharpSameLineContainerCandidatesByLine(),
                            structuralLines[i],
                            lineNumber,
                            column);
                    if (sameLineContainer != null)
                        return sameLineContainer;

                    if (csharpLineHasWhereClause
                        && container?.Kind == "function"
                        && container.StartLine == lineNumber
                        && (!TryFindCSharpFunctionNameColumn(
                                structuralLines[i],
                                container.Name,
                                out var containerNameColumn)
                            || column < containerNameColumn))
                    {
                        return null;
                    }
                }

                return dynamicDeclarativeState?.ResolveContainer(
                    lineNumber,
                    column,
                    container) ?? container;
            }

            SymbolRecord? ResolveSwiftPropertyContainerForCall(int column)
            {
                if (loop.SwiftPropertyDefinitionsByLine != null
                    && loop.SwiftPropertyDefinitionsByLine.TryGetValue(
                        lineNumber,
                        out var sameLineProperties))
                {
                    foreach (var property in sameLineProperties)
                    {
                        if ((property.StartColumn ?? 0) <= column)
                            return property;
                    }
                }

                return ResolveContainerForCall(column);
            }

            var lineContext = new CoreReferenceLineContext(
                fileId,
                language,
                lines,
                preparedLines,
                i,
                preparedLine,
                originalLine,
                sourceContext,
                lineNumber,
                references,
                seen,
                container,
                definitionNames,
                ResolveContainerForCall,
                isIgnoredCallName);

            if (shaderState is not null)
            {
                ShaderReferenceExtractor.EmitLineReferences(
                    shaderState,
                    preparedLine,
                    originalLine,
                    references,
                    seen,
                    fileId,
                    sourceContext,
                    lineNumber,
                    ResolveContainerForCall);
            }

            if (ReferenceLimitReached(references))
                break;

            if (loop.IsJsxFile
                && language is "javascript" or "typescript")
            {
                EmitJsxElementReferences(lineContext);
            }

            if (ReferenceLimitReached(references))
                break;

            var typeContext = new CoreTypeReferenceContext(
                lineContext,
                lookups,
                containerCandidates,
                symbols,
                structuralLines,
                loop.CSharpQualifiedConstantPatternMemberLookup,
                loop.CSharpQualifiedTypePatternLookup,
                csharpUsingAliases,
                csharpUsingStatics,
                csharpLocalNamesByFunction,
                pendingCSharpWhereConstraint,
                loop.KotlinConstructorTypeNames,
                input.TypeScriptNamespaceAliases,
                loop.TypeScriptTypeAliases,
                loop.SwiftTypeAliases,
                ResolveSwiftPropertyContainerForCall,
                input.GoImportBlockLines,
                input.LuaReferenceLines,
                originalLineForLanguage,
                loop.AllDefinitionNames,
                loop.StylusVariableDefinitionNames,
                loop.XamlReferenceEnabled,
                xamlBindingPropertyElementState,
                xamlBindingMarkupExtensionState);
            if (EmitCoreTypeReferences(
                    typeContext,
                    ref pendingCSharpMultiLineTypePattern,
                    ref xamlInXmlComment))
            {
                continue;
            }

            if (ReferenceLimitReached(references))
                break;

            EmitInfrastructureLineReferences(
                lineContext,
                loop.DockerfileStageNames,
                loop.DockerfileVariableNames,
                loop.CobolCallableSymbols);

            if (ReferenceLimitReached(references))
                break;

            var sqlSuppressedCallIndices = EmitSqlLineReferences(
                lineContext,
                structuralLines[i],
                sqlState,
                definitionState);

            if (ReferenceLimitReached(references))
                break;

            if (language is "csharp" or "java")
                EmitParenlessInitializerReferences(lineContext);

            EmitPhpAndScssLineReferences(lineContext);

            if (ReferenceLimitReached(references))
                break;

            var callContext = new CoreCallReferenceContext(
                lineContext,
                lookups,
                javaSameLineCtor,
                csharpAttrRangesOnLine,
                loop.KotlinConstructorTypeNames,
                loop.KotlinInfixFunctionNames,
                loop.ShellCallableNames,
                loop.ShellGlobalAliasNames,
                dynamicDeclarativeState,
                referenceStructuralLines[i],
                loop.ScientificNativeDependencyLimit,
                request.ReportDiagnostic,
                sqlSuppressedCallIndices,
                loop.SqlWindowFunctionCallSiteSuppressions,
                definitionState);
            EmitCoreCallReferences(callContext);

            if (ReferenceLimitReached(references))
                break;

            EmitCoreMethodAndMemberReferences(
                loop,
                lineContext,
                preparedLine,
                csharpAttrRangesOnLine,
                ResolveContainerForCall);

            if (ReferenceLimitReached(references))
                break;

            if (input.JsTaggedTemplatesByLine != null
                && input.JsTaggedTemplatesByLine.TryGetValue(
                    lineNumber,
                    out var tagHitsOnLine))
            {
                EmitJavaScriptTaggedTemplateReferences(
                    lineContext,
                    tagHitsOnLine);
            }

            if (ReferenceLimitReached(references))
                break;

            EmitMetadataLineReferences(
                lineContext,
                csharpAttrTopLevelOnLine);

            if (ReferenceLimitReached(references))
                break;

            if (loop.IsRazorFile && language == "csharp")
            {
                RazorReferenceExtractor.EmitReferences(
                    input.RazorReferenceLines?[i] ?? originalLine,
                    references,
                    seen,
                    fileId,
                    sourceContext,
                    lineNumber,
                    ResolveContainerForCall,
                    definitionNames,
                    loop.FileDefinitionNames,
                    input.RazorImplementedTypeNames);
            }

            if (ReferenceLimitReached(references))
                break;

            if (language == "python")
            {
                EmitPythonLineReferences(
                    lineContext,
                    lookups);
            }
            if (language == "r")
                EmitRLineReferences(lineContext);
        }

        return pendingCSharpMultiLineTypePattern;
    }

    private static (
        string PreparedLine,
        string OriginalLineForLanguage) PrepareCoreLanguageLine(
        CoreReferenceLoopContext loop,
        CoreReferenceLoopMutableState state,
        int lineIndex,
        string originalLine)
    {
        var input = loop.Preparation;
        var preparedLine = input.LuaPreparedLines?[lineIndex]
            ?? input.LispReferenceLines?[lineIndex]
            ?? input.PreparedLines[lineIndex];
        var originalLineForLanguage = originalLine;
        if (loop.Request.Language == "sass")
        {
            preparedLine = CssReferenceExtractor.MaskSassBlockCommentLine(
                preparedLine,
                state.SassPreparedCommentState!);
            originalLineForLanguage =
                CssReferenceExtractor.MaskSassBlockCommentLine(
                    originalLine,
                    state.SassOriginalCommentState!);
        }
        else if (loop.Request.Language == "stylus")
        {
            preparedLine =
                CssReferenceExtractor.MaskSassStylusBlockCommentLine(
                    preparedLine,
                    ref state.SassStylusPreparedInBlockComment);
            originalLineForLanguage =
                CssReferenceExtractor.MaskSassStylusBlockCommentLine(
                    originalLine,
                    ref state.SassStylusOriginalInBlockComment);
        }

        return (preparedLine, originalLineForLanguage);
    }

    private static bool EmitCoreDocumentationAndSpecialLineReferences(
        CoreReferenceLoopContext loop,
        CoreReferenceLoopMutableState state,
        int lineIndex,
        string originalLine,
        string preparedLine,
        bool preparedLineIsWhiteSpace,
        List<(int start, int end)>? csharpAttributeRangesOnLine,
        out string sourceContext)
    {
        var request = loop.Request;
        var input = loop.Preparation;
        var lineNumber = lineIndex + 1;
        if (request.Language is "csharp" or "java" or "kotlin" or "r" or "php")
        {
            Func<SymbolRecord?>? getPhpLineContainer = null;
            if (request.Language == "php")
            {
                SymbolRecord? phpLineContainer = null;
                var phpLineContainerResolved = false;

                SymbolRecord? GetPhpLineContainer()
                {
                    if (!phpLineContainerResolved)
                    {
                        phpLineContainer =
                            loop.ContainerResolver.Find(lineNumber);
                        phpLineContainerResolved = true;
                    }

                    return phpLineContainer;
                }

                getPhpLineContainer = GetPhpLineContainer;
            }

            var documentationLine = new CoreDocumentationLineContext(
                request.FileId,
                request.Language,
                input.Lines,
                input.PreparedLines,
                input.StructuralLines,
                lineIndex,
                lineNumber,
                originalLine,
                preparedLine,
                loop.References,
                loop.Seen,
                loop.ContainerCandidates,
                loop.ContainerResolver,
                loop.Lookups,
                input.CSharpLinesInsideMultilineStringContent,
                input.CSharpLinesInsideBlockComment,
                csharpAttributeRangesOnLine,
                loop.CSharpAttributeRanges,
                getPhpLineContainer);
            EmitCoreDocumentationReferences(
                documentationLine,
                ref state.CSharpInDelimitedDocComment,
                ref state.JvmInDelimitedDocComment,
                ref state.PhpInDocblock,
                ref state.PhpDocblockContainer,
                ref state.PhpDocblockPropertyNames);
        }

        if (preparedLineIsWhiteSpace
            && request.Language
                is not ("cmake"
                    or "justfile"
                    or "makefile"
                    or "msbuild"
                    or "graphql"
                    or "html"
                    or "markdown"))
        {
            sourceContext = string.Empty;
            return false;
        }

        sourceContext = originalLine.Trim();
        if (request.Language
                is "cmake" or "justfile" or "makefile" or "msbuild"
            && sourceContext.Length > 0)
        {
            BuildAutomationReferenceExtractor.EmitReferences(
                request.Language,
                originalLine,
                sourceContext,
                lineNumber,
                loop.References,
                loop.Seen,
                request.FileId,
                loop.ContainerResolver.Find(lineNumber));
            return true;
        }

        if (request.Language is not ("graphql" or "html" or "markdown")
            || sourceContext.Length == 0)
        {
            return false;
        }

        MarkupSchemaReferenceExtractor.EmitReferences(
            request.Language,
            originalLine,
            sourceContext,
            lineNumber,
            loop.References,
            loop.Seen,
            request.FileId,
            loop.ContainerResolver.Find(lineNumber),
            state.MarkupSchemaState);
        return true;
    }

    private static void EmitCoreMethodAndMemberReferences(
        CoreReferenceLoopContext loop,
        CoreReferenceLineContext line,
        string preparedLine,
        List<(int start, int end)>? csharpAttributeRanges,
        Func<int, SymbolRecord?> resolveContainerForCall)
    {
        var language = line.Language;
        if (language == "csharp")
        {
            EmitMethodGroupReferences(
                language,
                preparedLine,
                loop.CallableDefinitionNames,
                line.References,
                line.Seen,
                line.FileId,
                line.Context,
                line.LineNumber,
                resolveContainerForCall);
        }
        else if (language == "java")
        {
            JavaReferenceExtractor.EmitMethodReferenceReferences(
                preparedLine,
                line.References,
                line.Seen,
                line.FileId,
                line.Context,
                line.LineNumber,
                resolveContainerForCall);
        }
        else if (language == "kotlin")
        {
            KotlinReferenceExtractor.EmitMethodReferenceReferences(
                preparedLine,
                line.References,
                line.Seen,
                line.FileId,
                line.Context,
                line.LineNumber,
                resolveContainerForCall);
        }
        else if (language == "scala")
        {
            ScalaReferenceExtractor.EmitMethodReferenceReferences(
                preparedLine,
                line.References,
                line.Seen,
                line.FileId,
                line.Context,
                line.LineNumber,
                resolveContainerForCall);
        }

        if (language == "csharp")
        {
            CSharpReferenceExtractor.EmitStaticMemberQualifierReferences(
                preparedLine,
                csharpAttributeRanges,
                line.References,
                line.Seen,
                line.FileId,
                line.Context,
                line.LineNumber,
                resolveContainerForCall);
        }

        if (language != "csharp"
            || loop.CSharpQualifiedEnumMemberLookup.Count == 0)
        {
            return;
        }

        CSharpReferenceExtractor.EmitQualifiedEnumMemberReferences(
            preparedLine,
            loop.CSharpQualifiedEnumMemberLookup,
            csharpAttributeRanges,
            loop.CSharpUsingAliases,
            loop.Lookups.GetCSharpValueReceiverNames,
            loop.Lookups.GetCSharpFunctionValueReceiverNames,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            resolveContainerForCall);
    }
}
