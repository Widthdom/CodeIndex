namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static void EmitRubyOrPerlCallReferences(
        in CoreCallReferenceContext call,
        ref CoreCallEmissionState state)
    {
        ref readonly var line = ref call.Line;
        if (line.Language == "ruby")
        {
            var matchedCallIndices = GetOrCreateMatchedCallIndices(ref state);
            var addCallLikeReference = GetOrCreateCallLikeReferenceEmitter(
                in call,
                ref state);
            RubyReferenceExtractor.EmitAdditionalCallReferences(
                line.PreparedLine,
                line.OriginalLine,
                line.References,
                line.Seen,
                line.FileId,
                line.Context,
                line.LineNumber,
                line.ResolveContainerForCall,
                matchedCallIndices,
                addCallLikeReference);
        }
        else if (line.Language is "perl" or "ambiguous_pl")
        {
            PerlReferenceExtractor.EmitAdditionalReferences(
                line.Language == "ambiguous_pl"
                    ? state.CallScanLine
                    : line.PreparedLine,
                line.OriginalLine,
                line.References,
                line.Seen,
                line.FileId,
                line.Context,
                line.LineNumber,
                line.ResolveContainerForCall,
                GetOrCreateCallLikeReferenceEmitter(in call, ref state),
                emitArrowCallReferences: line.Language != "ambiguous_pl"
                    || call.DynamicDeclarativeState?.HasPrologContainer(
                        line.LineNumber) != true);
        }
    }

    private static void EmitDynamicDeclarativeCallReferences(
        in CoreCallReferenceContext call,
        ref CoreCallEmissionState state)
    {
        if (call.DynamicDeclarativeState == null)
            return;

        ref readonly var line = ref call.Line;
        DynamicDeclarativeReferenceExtractor.EmitAdditionalReferences(
            line.Language,
            state.CallScanLine,
            call.ReferenceStructuralLine,
            call.DynamicDeclarativeState,
            line.References,
            line.Seen,
            line.FileId,
            line.Context,
            line.LineNumber,
            line.ResolveContainerForCall,
            GetOrCreateCallLikeReferenceEmitter(in call, ref state));
    }

    private static void EmitGoCallReferences(
        in CoreCallReferenceContext call,
        ref CoreCallEmissionState state)
    {
        ref readonly var line = ref call.Line;
        if (line.Language != "go")
            return;

        LanguageReferenceExtractionSupport.EmitGoBranchLabelReferences(
            line.PreparedLine,
            GetOrCreateCallLikeReferenceEmitter(in call, ref state),
            line.References);
    }

    private static void EmitSwiftOrKotlinCallReferences(
        in CoreCallReferenceContext call,
        ref CoreCallEmissionState state)
    {
        ref readonly var line = ref call.Line;
        if (line.Language == "swift")
        {
            SwiftReferenceExtractor.EmitTrailingClosureReferences(
                line.PreparedLine,
                GetOrCreateCallLikeReferenceEmitter(in call, ref state),
                line.References);
        }
        else if (line.Language == "kotlin")
        {
            var addCallLikeReference = GetOrCreateCallLikeReferenceEmitter(
                in call,
                ref state);
            KotlinReferenceExtractor.EmitInfixCallReferences(
                line.PreparedLine,
                line.OriginalLine,
                call.KotlinInfixFunctionNames!,
                addCallLikeReference);
            KotlinReferenceExtractor.EmitTrailingLambdaReferences(
                line.PreparedLine,
                addCallLikeReference,
                line.References);
        }
    }

    private static void EmitFSharpCallReferences(
        in CoreCallReferenceContext call,
        ref CoreCallEmissionState state)
    {
        ref readonly var line = ref call.Line;
        if (line.Language != "fsharp")
            return;

        FSharpReferenceExtractor.EmitAdditionalCallReferences(
            line.PreparedLine,
            GetOrCreateCallLikeReferenceEmitter(in call, ref state),
            line.References);
    }

    private static void EmitScalaOrGradleCallReferences(
        in CoreCallReferenceContext call,
        ref CoreCallEmissionState state)
    {
        ref readonly var line = ref call.Line;
        if (line.Language == "scala")
        {
            var addCallLikeReference = GetOrCreateCallLikeReferenceEmitter(
                in call,
                ref state);
            ScalaReferenceExtractor.EmitTrailingBlockCallReferences(
                line.PreparedLine,
                addCallLikeReference,
                line.References);
            ScalaReferenceExtractor.EmitAdditionalReferences(
                line.PreparedLine,
                line.References,
                line.Seen,
                line.FileId,
                line.Context,
                line.LineNumber,
                line.ResolveContainerForCall,
                addCallLikeReference);
        }
        else if (line.Language == "gradle")
        {
            GradleReferenceExtractor.EmitDslCallReferences(
                line.PreparedLine,
                CreateGradleDslReferenceEmitter(in line),
                line.References);
        }
    }

    private static void EmitSecondaryLanguageCallReferences(
        in CoreCallReferenceContext call,
        ref CoreCallEmissionState state)
    {
        ref readonly var line = ref call.Line;
        if (line.Language == "fortran")
        {
            FortranReferenceExtractor.EmitAdditionalCallReferences(
                line.PreparedLine,
                GetOrCreateCallLikeReferenceEmitter(in call, ref state));
        }
        else if (line.Language == "pascal")
        {
            PascalReferenceExtractor.EmitAdditionalCallReferences(
                line.PreparedLine,
                GetOrCreateCallLikeReferenceEmitter(in call, ref state),
                line.DefinitionNames);
        }
        else if (line.Language == "objc")
        {
            ObjectiveCReferenceExtractor.EmitAdditionalCallReferences(
                line.PreparedLine,
                GetOrCreateCallLikeReferenceEmitter(in call, ref state),
                line.References,
                line.Seen,
                line.FileId,
                line.Context,
                line.LineNumber,
                line.ResolveContainerForCall);
        }
        else if (line.Language == "haskell")
        {
            HaskellReferenceExtractor.EmitAdditionalCallReferences(
                line.PreparedLine,
                GetOrCreateCallLikeReferenceEmitter(in call, ref state),
                line.DefinitionNames);
        }
        else if (line.Language == "elixir")
        {
            ElixirReferenceExtractor.EmitAdditionalCallReferences(
                line.PreparedLine,
                GetOrCreateCallLikeReferenceEmitter(in call, ref state),
                line.DefinitionNames);
        }
        else if (line.Language == "lua")
        {
            LuaReferenceExtractor.EmitAdditionalCallReferences(
                line.PreparedLine,
                GetOrCreateCallLikeReferenceEmitter(in call, ref state),
                line.References,
                line.Seen,
                line.FileId,
                line.Context,
                line.LineNumber,
                line.ResolveContainerForCall,
                line.DefinitionNames);
        }
        else if (line.Language == "smalltalk")
        {
            SmalltalkReferenceExtractor.EmitAdditionalCallReferences(
                line.PreparedLine,
                GetOrCreateCallLikeReferenceEmitter(in call, ref state),
                line.DefinitionNames);
        }
        else if (line.Language == "vb")
        {
            LanguageReferenceExtractionSupport.EmitAdditionalCallReferences(
                "vb",
                line.PreparedLine,
                line.OriginalLine,
                GetOrCreateCallLikeReferenceEmitter(in call, ref state),
                line.References,
                line.Seen,
                line.FileId,
                line.Context,
                line.LineNumber,
                line.ResolveContainerForCall,
                line.DefinitionNames);
        }
    }

    private static void EmitRustPostCallReferences(
        in CoreCallReferenceContext call,
        ref CoreCallEmissionState state)
    {
        ref readonly var line = ref call.Line;
        if (line.Language != "rust")
            return;

        RustReferenceExtractor.EmitAdditionalCallReferences(
            line.PreparedLine,
            GetOrCreateCallLikeReferenceEmitter(in call, ref state));
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
