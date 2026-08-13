using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private readonly record struct CoreCallReferenceContext(
        CoreExtractionLookups Lookups,
        (SymbolRecord Synthetic, int NameIndex, int OpenBraceIndex, int CloseBraceIndex)? JavaSameLineCtor,
        List<(int start, int end)>? CSharpAttributeRanges,
        HashSet<string>? KotlinConstructorTypeNames,
        HashSet<string>? KotlinInfixFunctionNames,
        HashSet<string>? ShellCallableNames,
        HashSet<string>? ShellGlobalAliasNames,
        DynamicDeclarativeReferenceExtractor.ExtractionState? DynamicDeclarativeState,
        string ReferenceStructuralLine,
        int ScientificNativeDependencyLimit,
        Action<ReferenceExtractionDiagnostic>? ReportDiagnostic,
        HashSet<int>? SqlSuppressedCallIndices,
        HashSet<(int LineNumber, int ColumnIndex)>? SqlWindowFunctionCallSiteSuppressions,
        CoreLineDefinitionState Definitions)
    {
        public readonly CoreReferenceLineContext Line;

        public CoreCallReferenceContext(
            in CoreReferenceLineContext line,
            CoreExtractionLookups lookups,
            (SymbolRecord Synthetic, int NameIndex, int OpenBraceIndex, int CloseBraceIndex)? javaSameLineCtor,
            List<(int start, int end)>? cSharpAttributeRanges,
            HashSet<string>? kotlinConstructorTypeNames,
            HashSet<string>? kotlinInfixFunctionNames,
            HashSet<string>? shellCallableNames,
            HashSet<string>? shellGlobalAliasNames,
            DynamicDeclarativeReferenceExtractor.ExtractionState? dynamicDeclarativeState,
            string referenceStructuralLine,
            int scientificNativeDependencyLimit,
            Action<ReferenceExtractionDiagnostic>? reportDiagnostic,
            HashSet<int>? sqlSuppressedCallIndices,
            HashSet<(int LineNumber, int ColumnIndex)>? sqlWindowFunctionCallSiteSuppressions,
            CoreLineDefinitionState definitions)
            : this(
                lookups,
                javaSameLineCtor,
                cSharpAttributeRanges,
                kotlinConstructorTypeNames,
                kotlinInfixFunctionNames,
                shellCallableNames,
                shellGlobalAliasNames,
                dynamicDeclarativeState,
                referenceStructuralLine,
                scientificNativeDependencyLimit,
                reportDiagnostic,
                sqlSuppressedCallIndices,
                sqlWindowFunctionCallSiteSuppressions,
                definitions)
        {
            Line = line;
        }
    }

    private struct CoreCallEmissionState(
        string callScanLine,
        bool isScientificNativeLanguage)
    {
        public string CallScanLine { get; } = callScanLine;
        public bool IsScientificNativeLanguage { get; } = isScientificNativeLanguage;
        public Action<string, int>? CallLikeReferenceEmitter { get; set; }
        public HashSet<int>? MatchedCallIndices { get; set; }
    }

    private static Action<string, int> CreateCallLikeReferenceEmitter(
        CoreCallReferenceContext call,
        bool isScientificNativeLanguage) =>
        (name, callIndex) =>
        {
            ref readonly var line = ref call.Line;
            _ = TryAddCoreCallLikeReference(
                in call,
                name,
                callIndex,
                isScientificNativeLanguage,
                isScientificNativeLanguage
                    ? ScientificNativeReferenceExtractor.GetParenthesizedCallTargetQualifier(
                        line.Language,
                        line.PreparedLine,
                        callIndex)
                    : null);
        };

    private static Action<string, int> GetOrCreateCallLikeReferenceEmitter(
        in CoreCallReferenceContext call,
        ref CoreCallEmissionState state) =>
        state.CallLikeReferenceEmitter ??=
            CreateCallLikeReferenceEmitter(call, state.IsScientificNativeLanguage);

    private static HashSet<int> GetOrCreateMatchedCallIndices(
        ref CoreCallEmissionState state) =>
        state.MatchedCallIndices ??= [];

    private static Action<string, int> CreatePowerShellParameterReferenceEmitter(
        in CoreReferenceLineContext line)
    {
        var references = line.References;
        var seen = line.Seen;
        var fileId = line.FileId;
        var context = line.Context;
        var lineNumber = line.LineNumber;
        var language = line.Language;
        var resolveContainerForCall = line.ResolveContainerForCall;
        return (name, callIndex) =>
        {
            var callContainer = resolveContainerForCall(callIndex);
            AddReference(
                references,
                seen,
                fileId,
                name,
                callIndex,
                "parameter",
                context,
                lineNumber,
                callContainer,
                language,
                sourceLength: name.Length);
        };
    }

    private static Action<string, int> CreateGradleDslReferenceEmitter(
        in CoreReferenceLineContext line)
    {
        var references = line.References;
        var seen = line.Seen;
        var fileId = line.FileId;
        var context = line.Context;
        var lineNumber = line.LineNumber;
        var language = line.Language;
        var resolveContainerForCall = line.ResolveContainerForCall;
        return (name, callIndex) =>
        {
            var normalizedName = NormalizeAtPrefixedIdentifier(name);
            var callContainer = resolveContainerForCall(callIndex);
            AddReference(
                references,
                seen,
                fileId,
                normalizedName,
                callIndex,
                "call",
                context,
                lineNumber,
                callContainer,
                language,
                sourceLength: name.Length);
        };
    }

    private static void EmitCoreCallReferences(
        in CoreCallReferenceContext call)
    {
        ref readonly var line = ref call.Line;
        EmitCoreCallPrelude(in call);

        var callScanLine = call.DynamicDeclarativeState?.GetCallScanLine(
            line.Language,
            line.LineNumber,
            line.PreparedLine) ?? line.PreparedLine;
        var state = new CoreCallEmissionState(
            callScanLine,
            ScientificNativeReferenceExtractor.Supports(line.Language));
        EmitCoreLanguageCallReferences(in call, ref state);

        if (ReferenceLimitReached(line.References))
            return;

        EmitRustPostCallReferences(in call, ref state);
    }
}
