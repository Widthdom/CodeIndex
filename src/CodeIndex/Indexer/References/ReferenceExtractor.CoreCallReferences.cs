using System.Text.RegularExpressions;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private readonly record struct CoreCallReferenceContext(
        CoreReferenceLineContext Line,
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
        CoreLineDefinitionState Definitions);

    private static void EmitCoreCallReferences(CoreCallReferenceContext call)
    {
        var line = call.Line;
        if (line.Language is "javascript" or "typescript")
        {
            JavaScriptReferenceExtractor.EmitOptionalMemberChainReferences(
                line.PreparedLine,
                line.References,
                line.Seen,
                line.FileId,
                line.Context,
                line.LineNumber,
                line.ResolveContainerForCall);

            JavaScriptReferenceExtractor.EmitDiscriminantStringGuardReferences(
                call.ReferenceStructuralLine,
                line.OriginalLine,
                line.References,
                line.Seen,
                line.FileId,
                line.Context,
                line.LineNumber,
                line.ResolveContainerForCall);

            JavaScriptReferenceExtractor.EmitParenlessConstructorReferences(
                line.PreparedLine,
                line.PreparedLines,
                line.LineIndex,
                line.References,
                line.Seen,
                line.FileId,
                line.Context,
                line.LineNumber,
                line.ResolveContainerForCall);
        }

        void AddCallLikeReference(string name, int callIndex) =>
            _ = TryAddCallLikeReference(
                name,
                callIndex,
                ScientificNativeReferenceExtractor.Supports(line.Language)
                    ? ScientificNativeReferenceExtractor.GetParenthesizedCallTargetQualifier(
                        line.Language,
                        line.PreparedLine,
                        callIndex)
                    : null);

        void AddPowerShellParameterReference(string name, int callIndex)
        {
            var callContainer = line.ResolveContainerForCall(callIndex);
            AddReference(line.References, line.Seen, line.FileId, name, callIndex, "parameter", line.Context, line.LineNumber, callContainer, line.Language);
        }

        bool TryAddCallLikeReference(
            string name,
            int callIndex,
            string? targetQualifier = null) =>
            TryAddCoreCallLikeReference(
                call,
                name,
                callIndex,
                targetQualifier);

        if (line.Language is "batch")
            BatchReferenceExtractor.EmitJumpTargetReferences(
                line.OriginalLine,
                line.PreparedLine,
                line.References,
                line.Seen,
                line.FileId,
                line.Context,
                line.LineNumber,
                line.ResolveContainerForCall);

        if (line.Language is "assembly")
            AssemblyReferenceExtractor.EmitInstructionTargetReferences(
                line.OriginalLine,
                line.References,
                line.Seen,
                line.FileId,
                line.Context,
                line.LineNumber,
                line.ResolveContainerForCall);

        HashSet<int>? matchedCallIndices = null;
        HashSet<int> GetMatchedCallIndices() => matchedCallIndices ??= [];
        var callScanLine = call.DynamicDeclarativeState?.GetCallScanLine(
            line.Language,
            line.LineNumber,
            line.PreparedLine) ?? line.PreparedLine;

        if (line.Language is "commonlisp" or "racket")
        {
            LispReferenceExtractor.EmitReferences(
                line.Language,
                line.PreparedLine,
                line.References,
                line.Seen,
                line.FileId,
                line.Context,
                line.LineNumber,
                line.ResolveContainerForCall,
                line.DefinitionNames);
        }
        else if (line.Language is "powershell")
        {
            PowerShellReferenceExtractor.EmitCallReferences(line.PreparedLine, AddCallLikeReference);
            PowerShellReferenceExtractor.EmitSplatParameterReferences(
                line.PreparedLine,
                call.Lookups.GetPowerShellSplatAssignments,
                line.LineNumber,
                AddPowerShellParameterReference);
        }
        else if (line.Language is "shell")
        {
            ShellReferenceExtractor.EmitReferences(
                line.PreparedLine,
                line.OriginalLine,
                line.References,
                line.Seen,
                line.FileId,
                line.Context,
                line.LineNumber,
                call.ShellCallableNames,
                call.ShellGlobalAliasNames,
                line.ResolveContainerForCall,
                AddCallLikeReference);
        }
        else if (line.Language is "assembly")
        {
            // Assembly line.References are operand-driven, not `name(...)` call syntax.
        }
        else
        {
            IReadOnlyList<ScientificNativeReferenceExtractor.DTemplateArgumentCallSpan>?
                dTemplateArgumentCallSpans = null;
            if (ScientificNativeReferenceExtractor.Supports(line.Language))
            {
                dTemplateArgumentCallSpans = ScientificNativeReferenceExtractor.EmitReferences(
                    line.Language,
                    line.PreparedLine,
                    line.OriginalLine,
                    line.References,
                    line.Seen,
                    line.FileId,
                    line.Context,
                    line.LineNumber,
                    line.ResolveContainerForCall,
                    AddCallLikeReference,
                    call.ScientificNativeDependencyLimit,
                    call.ReportDiagnostic);
            }

            var dTemplateArgumentCallSpanIndex = 0;
            if (line.Language is not ("tcl" or "prolog"))
            {
                foreach (Match match in CallRegex.Matches(callScanLine))
                {
                    var name = match.Groups["name"].Value;
                    var callIndex = match.Groups["name"].Index;
                    if (line.Language == "rust" && RustReferenceExtractor.IsRawIdentifierPrefix(line.PreparedLine, callIndex))
                        continue;
                    if (line.Language == "d"
                        && ScientificNativeReferenceExtractor.IsDTemplateArgumentCall(
                            dTemplateArgumentCallSpans,
                            ref dTemplateArgumentCallSpanIndex,
                            callIndex))
                    {
                        continue;
                    }
                    if (line.Language == "ada"
                        && callIndex > 0
                        && line.PreparedLine[callIndex - 1] == '\'')
                    {
                        continue;
                    }
                    if (line.Language == "objc" && IsObjCSelectorLiteralCall(line.PreparedLine, name, callIndex))
                        continue;
                    if (call.SqlSuppressedCallIndices != null && call.SqlSuppressedCallIndices.Contains(callIndex))
                        continue;
                    if (call.SqlWindowFunctionCallSiteSuppressions != null
                        && call.SqlWindowFunctionCallSiteSuppressions.Contains((line.LineNumber, callIndex)))
                        continue;
                    if (DynamicDeclarativeReferenceExtractor.ShouldSuppressGenericCall(
                            line.Language,
                            callScanLine,
                            name,
                            callIndex,
                            line.LineNumber,
                            call.DynamicDeclarativeState,
                            line.Language == "groovy"
                                ? line.ResolveContainerForCall(callIndex)
                                : null))
                    {
                        continue;
                    }
                    GetMatchedCallIndices().Add(callIndex);
                    if (TryAddCallLikeReference(
                            name,
                            callIndex,
                            ScientificNativeReferenceExtractor.Supports(line.Language)
                                ? ScientificNativeReferenceExtractor.GetParenthesizedCallTargetQualifier(
                                    line.Language,
                                    line.PreparedLine,
                                    callIndex)
                                : null))
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

            if (line.Language == "ruby")
            {
                RubyReferenceExtractor.EmitAdditionalCallReferences(
                    line.PreparedLine,
                    line.OriginalLine,
                    line.References,
                    line.Seen,
                    line.FileId,
                    line.Context,
                    line.LineNumber,
                    line.ResolveContainerForCall,
                    GetMatchedCallIndices(),
                    AddCallLikeReference);
            }
            else if (line.Language is "perl" or "ambiguous_pl")
            {
                PerlReferenceExtractor.EmitAdditionalReferences(
                    line.Language == "ambiguous_pl" ? callScanLine : line.PreparedLine,
                    line.OriginalLine,
                    line.References,
                    line.Seen,
                    line.FileId,
                    line.Context,
                    line.LineNumber,
                    line.ResolveContainerForCall,
                    AddCallLikeReference,
                    emitArrowCallReferences: line.Language != "ambiguous_pl"
                        || call.DynamicDeclarativeState?.HasPrologContainer(line.LineNumber) != true);
            }

            if (call.DynamicDeclarativeState != null)
            {
                DynamicDeclarativeReferenceExtractor.EmitAdditionalReferences(
                    line.Language,
                    callScanLine,
                    call.ReferenceStructuralLine,
                    call.DynamicDeclarativeState,
                    line.References,
                    line.Seen,
                    line.FileId,
                    line.Context,
                    line.LineNumber,
                    line.ResolveContainerForCall,
                    AddCallLikeReference);
            }

            if (line.Language == "go")
                LanguageReferenceExtractionSupport.EmitGoBranchLabelReferences(line.PreparedLine, AddCallLikeReference);

            if (line.Language == "swift")
                SwiftReferenceExtractor.EmitTrailingClosureReferences(line.PreparedLine, AddCallLikeReference);
            else if (line.Language == "kotlin")
            {
                KotlinReferenceExtractor.EmitInfixCallReferences(
                    line.PreparedLine,
                    line.OriginalLine,
                    call.KotlinInfixFunctionNames!,
                    AddCallLikeReference);
                KotlinReferenceExtractor.EmitTrailingLambdaReferences(line.PreparedLine, AddCallLikeReference);
            }

            if (line.Language == "fsharp")
            {
                FSharpReferenceExtractor.EmitAdditionalCallReferences(
                    line.PreparedLine,
                    AddCallLikeReference);
            }

            if (line.Language == "scala")
            {
                ScalaReferenceExtractor.EmitTrailingBlockCallReferences(
                    line.PreparedLine,
                    AddCallLikeReference);
                ScalaReferenceExtractor.EmitAdditionalReferences(
                    line.PreparedLine,
                    line.References,
                    line.Seen,
                    line.FileId,
                    line.Context,
                    line.LineNumber,
                    line.ResolveContainerForCall,
                    AddCallLikeReference);
            }
            else if (line.Language == "gradle")
            {
                void AddGradleDslReference(string name, int callIndex)
                {
                    var normalizedName = NormalizeAtPrefixedIdentifier(name);
                    var callContainer = line.ResolveContainerForCall(callIndex);
                    AddReference(line.References, line.Seen, line.FileId, normalizedName, callIndex, "call", line.Context, line.LineNumber, callContainer, line.Language);
                }

                GradleReferenceExtractor.EmitDslCallReferences(
                    line.PreparedLine,
                    AddGradleDslReference);
            }

            if (line.Language == "fortran")
                FortranReferenceExtractor.EmitAdditionalCallReferences(line.PreparedLine, AddCallLikeReference);
            else if (line.Language == "pascal")
                PascalReferenceExtractor.EmitAdditionalCallReferences(line.PreparedLine, AddCallLikeReference, line.DefinitionNames);
            else if (line.Language == "objc")
                ObjectiveCReferenceExtractor.EmitAdditionalCallReferences(line.PreparedLine, AddCallLikeReference, line.References, line.Seen, line.FileId, line.Context, line.LineNumber, line.ResolveContainerForCall);
            else if (line.Language == "haskell")
                HaskellReferenceExtractor.EmitAdditionalCallReferences(line.PreparedLine, AddCallLikeReference, line.DefinitionNames);
            else if (line.Language == "elixir")
                ElixirReferenceExtractor.EmitAdditionalCallReferences(line.PreparedLine, AddCallLikeReference, line.DefinitionNames);
            else if (line.Language == "lua")
                LuaReferenceExtractor.EmitAdditionalCallReferences(line.PreparedLine, AddCallLikeReference, line.References, line.Seen, line.FileId, line.Context, line.LineNumber, line.ResolveContainerForCall, line.DefinitionNames);
            else if (line.Language == "smalltalk")
                SmalltalkReferenceExtractor.EmitAdditionalCallReferences(line.PreparedLine, AddCallLikeReference, line.DefinitionNames);
            else if (line.Language == "vb")
                LanguageReferenceExtractionSupport.EmitAdditionalCallReferences(
                    "vb",
                    line.PreparedLine,
                    line.OriginalLine,
                    AddCallLikeReference,
                    line.References,
                    line.Seen,
                    line.FileId,
                    line.Context,
                    line.LineNumber,
                    line.ResolveContainerForCall,
                    line.DefinitionNames);

            // The flat CallRegex misses nested generic tails like `>>(` because `<[^>\n]+>`
            // stops at the first `>`. Add a depth-aware fallback so `Foo<Bar<int>>()` and
            // `new Dict<K, List<V>>()` still emit call/instantiate rows. See issue #263.
            // 平坦な CallRegex は `<[^>\n]+>` が最初の `>` で止まるため `>>(` 形を取りこぼす。
            // depth-aware な fallback を足し、`Foo<Bar<int>>()` や `new Dict<K, List<V>>()` でも
            // `call` / `instantiate` を発行する。issue #263 参照。
            if (line.Language is not ("tcl" or "prolog" or "ambiguous_pl")
                && MayContainNestedGenericSyntax(line.PreparedLine))
            {
                foreach (var candidate in EnumerateNestedGenericCallCandidates(line.PreparedLine, matchedCallIndices ?? EmptyMatchedIndices))
                {
                    if (TryAddCallLikeReference(candidate.Name, candidate.NameIndex))
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

        if (line.Language == "rust")
        {
            RustReferenceExtractor.EmitAdditionalCallReferences(
                line.PreparedLine,
                AddCallLikeReference);
            RustReferenceExtractor.EmitAttributeReferences(
                line.PreparedLine,
                line.References,
                line.Seen,
                line.FileId,
                line.Context,
                line.LineNumber,
                line.Container);
        }
    }
}
