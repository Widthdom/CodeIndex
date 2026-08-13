namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static void EmitCoreCallPrelude(
        in CoreCallReferenceContext call)
    {
        ref readonly var line = ref call.Line;
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

        if (line.Language == "batch")
        {
            BatchReferenceExtractor.EmitJumpTargetReferences(
                line.OriginalLine,
                line.PreparedLine,
                line.References,
                line.Seen,
                line.FileId,
                line.Context,
                line.LineNumber,
                line.ResolveContainerForCall);
        }

        if (line.Language == "assembly")
        {
            AssemblyReferenceExtractor.EmitInstructionTargetReferences(
                line.OriginalLine,
                line.References,
                line.Seen,
                line.FileId,
                line.Context,
                line.LineNumber,
                line.ResolveContainerForCall);
        }
    }

    private static void EmitCoreLanguageCallReferences(
        in CoreCallReferenceContext call,
        ref CoreCallEmissionState state)
    {
        ref readonly var line = ref call.Line;
        switch (line.Language)
        {
            case "commonlisp":
            case "racket":
                EmitLispCallReferences(in call);
                break;
            case "powershell":
                EmitPowerShellCallReferences(in call, ref state);
                break;
            case "shell":
                EmitShellCallReferences(in call, ref state);
                break;
            case "assembly":
                // Assembly references are operand-driven, not `name(...)` call syntax.
                break;
            default:
                EmitGeneralCoreCallReferences(in call, ref state);
                break;
        }
    }

    private static void EmitLispCallReferences(
        in CoreCallReferenceContext call)
    {
        ref readonly var line = ref call.Line;
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

    private static void EmitPowerShellCallReferences(
        in CoreCallReferenceContext call,
        ref CoreCallEmissionState state)
    {
        ref readonly var line = ref call.Line;
        PowerShellReferenceExtractor.EmitCallReferences(
            line.PreparedLine,
            GetOrCreateCallLikeReferenceEmitter(in call, ref state),
            line.References);
        if (ReferenceLimitReached(line.References))
            return;

        PowerShellReferenceExtractor.EmitSplatParameterReferences(
            line.PreparedLine,
            call.Lookups.GetPowerShellSplatAssignments,
            line.LineNumber,
            CreatePowerShellParameterReferenceEmitter(in line),
            line.References);
    }

    private static void EmitShellCallReferences(
        in CoreCallReferenceContext call,
        ref CoreCallEmissionState state)
    {
        ref readonly var line = ref call.Line;
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
            GetOrCreateCallLikeReferenceEmitter(in call, ref state));
    }
}
