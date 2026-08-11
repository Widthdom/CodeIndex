using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static CoreReferenceLineFlow EmitCoreOrderedReferencePhases(
        CoreReferenceLoopContext loop,
        CoreReferenceLoopState state,
        CorePreparedReferenceLine prepared)
    {
        var initialFlow = EmitCoreInitialReferencePhases(
            loop,
            state,
            prepared);
        if (initialFlow != CoreReferenceLineFlow.Continue)
            return initialFlow;

        return EmitCoreRemainingReferencePhases(loop, state, prepared);
    }

    private static CoreReferenceLineFlow EmitCoreInitialReferencePhases(
        CoreReferenceLoopContext loop,
        CoreReferenceLoopState state,
        CorePreparedReferenceLine prepared)
    {
        var request = loop.Request;
        var input = loop.Preparation;
        var line = prepared.Line;
        if (state.ShaderState is not null)
        {
            ShaderReferenceExtractor.EmitLineReferences(
                state.ShaderState,
                line.PreparedLine,
                line.OriginalLine,
                line.References,
                line.Seen,
                line.FileId,
                line.Context,
                line.LineNumber,
                line.ResolveContainerForCall);
        }

        if (ReferenceLimitReached(line.References))
            return CoreReferenceLineFlow.StopExtraction;

        if (loop.IsJsxFile
            && line.Language is "javascript" or "typescript")
        {
            EmitJsxElementReferences(line);
        }

        if (ReferenceLimitReached(line.References))
            return CoreReferenceLineFlow.StopExtraction;

        var typeContext = new CoreTypeReferenceContext(
            line,
            loop.Lookups,
            loop.ContainerCandidates,
            request.Symbols,
            input.StructuralLines,
            loop.CSharpQualifiedConstantPatternMemberLookup,
            loop.CSharpQualifiedTypePatternLookup,
            loop.CSharpUsingAliases,
            loop.CSharpUsingStatics,
            state.CSharpLocalNamesByFunction,
            state.PendingCSharpWhereConstraint,
            loop.KotlinConstructorTypeNames,
            input.TypeScriptNamespaceAliases,
            loop.TypeScriptTypeAliases,
            loop.SwiftTypeAliases,
            prepared.ContainerResolver
                .ResolveSwiftPropertyContainerForCall,
            input.GoImportBlockLines,
            input.LuaReferenceLines,
            prepared.OriginalLineForLanguage,
            loop.AllDefinitionNames,
            loop.StylusVariableDefinitionNames,
            loop.XamlReferenceEnabled,
            state.XamlBindingPropertyElementState,
            state.XamlBindingMarkupExtensionState);
        if (EmitCoreTypeReferences(
                typeContext,
                ref state.PendingCSharpMultiLineTypePattern,
                ref state.XamlInXmlComment))
        {
            return CoreReferenceLineFlow.LineConsumed;
        }

        return ReferenceLimitReached(line.References)
            ? CoreReferenceLineFlow.StopExtraction
            : CoreReferenceLineFlow.Continue;
    }

    private static CoreReferenceLineFlow EmitCoreRemainingReferencePhases(
        CoreReferenceLoopContext loop,
        CoreReferenceLoopState state,
        CorePreparedReferenceLine prepared)
    {
        var request = loop.Request;
        var input = loop.Preparation;
        var line = prepared.Line;
        EmitInfrastructureLineReferences(
            line,
            loop.DockerfileStageNames,
            loop.DockerfileVariableNames,
            loop.CobolCallableSymbols);

        if (ReferenceLimitReached(line.References))
            return CoreReferenceLineFlow.StopExtraction;

        var sqlSuppressedCallIndices = EmitSqlLineReferences(
            line,
            input.StructuralLines[line.LineIndex],
            state.SqlState,
            prepared.DefinitionState);

        if (ReferenceLimitReached(line.References))
            return CoreReferenceLineFlow.StopExtraction;

        if (line.Language is "csharp" or "java")
            EmitParenlessInitializerReferences(line);

        EmitPhpAndScssLineReferences(line);

        if (ReferenceLimitReached(line.References))
            return CoreReferenceLineFlow.StopExtraction;

        var callContext = new CoreCallReferenceContext(
            line,
            loop.Lookups,
            prepared.JavaSameLineCtor,
            prepared.CSharpAttributeRanges,
            loop.KotlinConstructorTypeNames,
            loop.KotlinInfixFunctionNames,
            loop.ShellCallableNames,
            loop.ShellGlobalAliasNames,
            loop.DynamicDeclarativeState,
            input.ReferenceStructuralLines[line.LineIndex],
            loop.ScientificNativeDependencyLimit,
            request.ReportDiagnostic,
            sqlSuppressedCallIndices,
            loop.SqlWindowFunctionCallSiteSuppressions,
            prepared.DefinitionState);
        EmitCoreCallReferences(callContext);

        if (ReferenceLimitReached(line.References))
            return CoreReferenceLineFlow.StopExtraction;

        EmitCoreMethodAndMemberReferences(
            loop,
            line,
            prepared.CSharpAttributeRanges,
            prepared.ContainerResolver.ResolveContainerForCall);

        if (ReferenceLimitReached(line.References))
            return CoreReferenceLineFlow.StopExtraction;

        if (input.JsTaggedTemplatesByLine != null
            && input.JsTaggedTemplatesByLine.TryGetValue(
                line.LineNumber,
                out var tagHitsOnLine))
        {
            EmitJavaScriptTaggedTemplateReferences(line, tagHitsOnLine);
        }

        if (ReferenceLimitReached(line.References))
            return CoreReferenceLineFlow.StopExtraction;

        EmitMetadataLineReferences(
            line,
            prepared.CSharpAttributeTopLevelRanges);

        if (ReferenceLimitReached(line.References))
            return CoreReferenceLineFlow.StopExtraction;

        if (loop.IsRazorFile && line.Language == "csharp")
        {
            RazorReferenceExtractor.EmitReferences(
                input.RazorReferenceLines?[line.LineIndex]
                    ?? line.OriginalLine,
                line.References,
                line.Seen,
                line.FileId,
                line.Context,
                line.LineNumber,
                line.ResolveContainerForCall,
                line.DefinitionNames,
                loop.FileDefinitionNames,
                input.RazorImplementedTypeNames);
        }

        if (ReferenceLimitReached(line.References))
            return CoreReferenceLineFlow.StopExtraction;

        if (line.Language == "python")
            EmitPythonLineReferences(line, loop.Lookups);
        if (line.Language == "r")
            EmitRLineReferences(line);

        return CoreReferenceLineFlow.Continue;
    }

    private static void EmitCoreMethodAndMemberReferences(
        CoreReferenceLoopContext loop,
        CoreReferenceLineContext line,
        List<(int start, int end)>? csharpAttributeRanges,
        Func<int, SymbolRecord?> resolveContainerForCall)
    {
        var language = line.Language;
        if (language == "csharp")
        {
            EmitMethodGroupReferences(
                language,
                line.PreparedLine,
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
                line.PreparedLine,
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
                line.PreparedLine,
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
                line.PreparedLine,
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
                line.PreparedLine,
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
            line.PreparedLine,
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
