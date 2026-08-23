using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private enum CoreReferenceLineFlow
    {
        Continue,
        LineConsumed,
        StopExtraction,
    }

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

    private sealed class CoreReferenceLoopState
    {
        internal CSharpMultiLineTypePatternState PendingCSharpMultiLineTypePattern;
        internal readonly CSharpWhereConstraintState? PendingCSharpWhereConstraint;
        internal readonly Dictionary<string, HashSet<string>>? CSharpLocalNamesByFunction;
        internal readonly SqlReferenceExtractor.State? SqlState;
        internal bool XamlInXmlComment;
        internal readonly XamlReferenceExtractor.BindingPropertyElementState? XamlBindingPropertyElementState;
        internal readonly XamlReferenceExtractor.BindingMarkupExtensionState? XamlBindingMarkupExtensionState;
        internal CoreDocumentationState Documentation;
        internal readonly MarkupSchemaReferenceExtractor.MarkupState? MarkupSchemaState;
        internal readonly CssReferenceExtractor.SassLoudCommentState? SassPreparedCommentState;
        internal readonly CssReferenceExtractor.SassLoudCommentState? SassOriginalCommentState;
        internal bool SassStylusPreparedInBlockComment;
        internal bool SassStylusOriginalInBlockComment;
        internal readonly ShaderReferenceExtractor.State? ShaderState;
        internal readonly Func<string, bool> IsIgnoredCallName;
        internal readonly CoreReferenceLineContainerResolver LineContainerResolver;

        internal CoreReferenceLoopState(CoreReferenceLoopContext loop)
        {
            var language = loop.Request.Language;
            var preparation = loop.Preparation;
            PendingCSharpWhereConstraint = language == "csharp"
                ? new CSharpWhereConstraintState()
                : null;
            CSharpLocalNamesByFunction = language == "csharp"
                ? new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
                : null;
            SqlState = language == "sql"
                ? SqlReferenceExtractor.CreateState()
                : null;
            XamlBindingPropertyElementState = language == "xml"
                ? new XamlReferenceExtractor.BindingPropertyElementState()
                : null;
            XamlBindingMarkupExtensionState = language == "xml"
                ? new XamlReferenceExtractor.BindingMarkupExtensionState()
                : null;
            if (language is "graphql" or "html" or "markdown")
            {
                MarkupSchemaState =
                    MarkupSchemaReferenceExtractor.CreateState(
                        language,
                        preparation.Lines);
            }
            if (language == "sass")
            {
                SassPreparedCommentState =
                    new CssReferenceExtractor.SassLoudCommentState();
                SassOriginalCommentState =
                    new CssReferenceExtractor.SassLoudCommentState();
            }

            ShaderState = ShaderReferenceExtractor.CreateState(
                language,
                preparation.PreparedLines,
                loop.Request.Symbols,
                loop.Request.WorkspaceSymbols,
                loop.Request.ReportDiagnostic);
            IsIgnoredCallName =
                name => ReferenceExtractor.IsIgnoredCallName(language, name);
            LineContainerResolver = new CoreReferenceLineContainerResolver(loop);
        }
    }

    private readonly record struct CorePreparedReferenceLine(
        string OriginalLineForLanguage,
        List<(int start, int end)>? CSharpAttributeRanges,
        List<(int start, int end)>? CSharpAttributeTopLevelRanges,
        CoreLineDefinitionState DefinitionState,
        (SymbolRecord Synthetic, int NameIndex, int OpenBraceIndex, int CloseBraceIndex)? JavaSameLineCtor,
        CoreReferenceLineContainerResolver ContainerResolver)
    {
        public readonly CoreReferenceLineContext Line;

        public CorePreparedReferenceLine(
            in CoreReferenceLineContext line,
            string originalLineForLanguage,
            List<(int start, int end)>? cSharpAttributeRanges,
            List<(int start, int end)>? cSharpAttributeTopLevelRanges,
            CoreLineDefinitionState definitionState,
            (SymbolRecord Synthetic, int NameIndex, int OpenBraceIndex, int CloseBraceIndex)? javaSameLineCtor,
            CoreReferenceLineContainerResolver containerResolver)
            : this(
                originalLineForLanguage,
                cSharpAttributeRanges,
                cSharpAttributeTopLevelRanges,
                definitionState,
                javaSameLineCtor,
                containerResolver)
        {
            Line = line;
        }
    }

    private static CSharpMultiLineTypePatternState EmitCoreReferenceLines(
        CoreReferenceLoopContext loop)
    {
        var state = new CoreReferenceLoopState(loop);
        for (var lineIndex = 0;
             lineIndex < loop.Preparation.Lines.Length;
             lineIndex++)
        {
            if (ReferenceLimitReached(loop.References))
                break;

            if ((lineIndex & 0x3f) == 0)
                loop.Request.CancellationToken.ThrowIfCancellationRequested();

            var flow = ProcessCoreReferenceLine(loop, state, lineIndex);
            if (flow == CoreReferenceLineFlow.StopExtraction)
                break;
        }

        return state.PendingCSharpMultiLineTypePattern;
    }
}
