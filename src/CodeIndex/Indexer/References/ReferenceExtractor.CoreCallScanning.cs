using System.Text.RegularExpressions;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static void EmitGeneralCoreCallReferences(
        in CoreCallReferenceContext call,
        ref CoreCallEmissionState state)
    {
        var dTemplateArgumentCallSpans = EmitScientificNativeCallReferences(
            in call,
            ref state);
        EmitParenthesizedCoreCallReferences(
            in call,
            dTemplateArgumentCallSpans,
            ref state);
        EmitRubyOrPerlCallReferences(in call, ref state);
        EmitDynamicDeclarativeCallReferences(in call, ref state);
        EmitGoCallReferences(in call, ref state);
        EmitSwiftOrKotlinCallReferences(in call, ref state);
        EmitFSharpCallReferences(in call, ref state);
        EmitScalaOrGradleCallReferences(in call, ref state);
        EmitSecondaryLanguageCallReferences(in call, ref state);
        EmitNestedGenericCallReferences(in call, ref state);
    }

    private static IReadOnlyList<ScientificNativeReferenceExtractor.DTemplateArgumentCallSpan>?
        EmitScientificNativeCallReferences(
            in CoreCallReferenceContext call,
            ref CoreCallEmissionState state)
    {
        if (!state.IsScientificNativeLanguage)
            return null;

        ref readonly var line = ref call.Line;
        return ScientificNativeReferenceExtractor.EmitReferences(
            line.Language,
            line.PreparedLine,
            line.OriginalLine,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.ResolveContainerForCall,
            GetOrCreateCallLikeReferenceEmitter(in call, ref state),
            call.ScientificNativeDependencyLimit,
            call.ReportDiagnostic);
    }

    private static void EmitParenthesizedCoreCallReferences(
        in CoreCallReferenceContext call,
        IReadOnlyList<ScientificNativeReferenceExtractor.DTemplateArgumentCallSpan>?
            dTemplateArgumentCallSpans,
        ref CoreCallEmissionState state)
    {
        ref readonly var line = ref call.Line;
        if (line.Language is "tcl" or "prolog"
            || state.CallScanLine.IndexOf('(') < 0)
        {
            return;
        }

        var dTemplateArgumentCallSpanIndex = 0;
        foreach (Match match in EnumerateReferenceMatches(
                     CallRegex,
                     state.CallScanLine,
                     line.References))
        {
            var name = match.Groups["name"].Value;
            var callIndex = match.Groups["name"].Index;
            if (ShouldSuppressParenthesizedCoreCall(
                    in call,
                    state.CallScanLine,
                    name,
                    callIndex,
                    dTemplateArgumentCallSpans,
                    ref dTemplateArgumentCallSpanIndex))
            {
                continue;
            }

            GetOrCreateMatchedCallIndices(ref state).Add(callIndex);
            EmitParenthesizedCoreCallMatch(
                in call,
                name,
                callIndex,
                ref state);
        }
    }

    private static bool ShouldSuppressParenthesizedCoreCall(
        in CoreCallReferenceContext call,
        string callScanLine,
        string name,
        int callIndex,
        IReadOnlyList<ScientificNativeReferenceExtractor.DTemplateArgumentCallSpan>?
            dTemplateArgumentCallSpans,
        ref int dTemplateArgumentCallSpanIndex)
    {
        ref readonly var line = ref call.Line;
        if (line.Language == "rust"
            && RustReferenceExtractor.IsRawIdentifierPrefix(
                line.PreparedLine,
                callIndex))
        {
            return true;
        }

        if (line.Language == "d"
            && ScientificNativeReferenceExtractor.IsDTemplateArgumentCall(
                dTemplateArgumentCallSpans,
                ref dTemplateArgumentCallSpanIndex,
                callIndex))
        {
            return true;
        }

        if (line.Language == "ada"
            && callIndex > 0
            && line.PreparedLine[callIndex - 1] == '\'')
        {
            return true;
        }

        if (line.Language == "objc"
            && IsObjCSelectorLiteralCall(line.PreparedLine, name, callIndex))
        {
            return true;
        }

        if (call.SqlSuppressedCallIndices?.Contains(callIndex) == true)
            return true;

        if (call.SqlWindowFunctionCallSiteSuppressions?.Contains(
                (line.LineNumber, callIndex)) == true)
        {
            return true;
        }

        return DynamicDeclarativeReferenceExtractor.ShouldSuppressGenericCall(
            line.Language,
            callScanLine,
            name,
            callIndex,
            line.LineNumber,
            call.DynamicDeclarativeState,
            line.Language == "groovy"
                ? line.ResolveContainerForCall(callIndex)
                : null);
    }

    private static void EmitParenthesizedCoreCallMatch(
        in CoreCallReferenceContext call,
        string name,
        int callIndex,
        ref CoreCallEmissionState state)
    {
        ref readonly var line = ref call.Line;
        var targetQualifier = state.IsScientificNativeLanguage
            ? ScientificNativeReferenceExtractor.GetParenthesizedCallTargetQualifier(
                line.Language,
                line.PreparedLine,
                callIndex)
            : null;
        if (TryAddCoreCallLikeReference(
                in call,
                name,
                callIndex,
                state.IsScientificNativeLanguage,
                targetQualifier))
        {
            EmitGenericInvocationTypeArgumentReferences(
                line.Language,
                line.PreparedLine,
                callIndex,
                line.References,
                line.Seen,
                line.FileId,
                line.Context,
                line.LineNumber,
                line.ResolveContainerForCall(callIndex));
        }

        if (line.Language == "ruby")
        {
            RubyReferenceExtractor.EmitCommandTargetReferences(
                name,
                callIndex,
                line.OriginalLine,
                line.References,
                line.Seen,
                line.FileId,
                line.Context,
                line.LineNumber,
                line.ResolveContainerForCall);
        }
    }

    private static void EmitNestedGenericCallReferences(
        in CoreCallReferenceContext call,
        ref CoreCallEmissionState state)
    {
        ref readonly var line = ref call.Line;
        // CallRegex stops at the first `>`. Keep the depth-aware fallback for
        // nested tails such as `Foo<Bar<int>>()` (issue #263).
        if (line.Language is "tcl" or "prolog" or "ambiguous_pl"
            || line.PreparedLine.IndexOf('(') < 0
            || !MayContainNestedGenericSyntax(line.PreparedLine))
        {
            return;
        }

        var matchedCallIndices = state.MatchedCallIndices ?? EmptyMatchedIndices;
        foreach (var candidate in EnumerateNestedGenericCallCandidates(
                     line.PreparedLine,
                     matchedCallIndices))
        {
            if (ReferenceLimitReached(line.References))
                break;

            if (TryAddCoreCallLikeReference(
                    in call,
                    candidate.Name,
                    candidate.NameIndex,
                    state.IsScientificNativeLanguage))
            {
                EmitGenericInvocationTypeArgumentReferences(
                    line.Language,
                    line.PreparedLine,
                    candidate.NameIndex,
                    line.References,
                    line.Seen,
                    line.FileId,
                    line.Context,
                    line.LineNumber,
                    line.ResolveContainerForCall(candidate.NameIndex));
            }
        }
    }
}
