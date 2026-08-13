using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private readonly record struct CorePythonReferenceLinePreparation(
        string HeaderLine,
        PythonLogicalHeaderReferenceLine? HeaderMap,
        SymbolRecord? HeaderContainer,
        Func<int, SymbolRecord?> ResolveClassContainer,
        Func<int, SymbolRecord?> ResolveFunctionContainer,
        string TypeFactoryLine,
        PythonLogicalHeaderReferenceLine? TypeFactoryMap);

    private static Func<int, SymbolRecord?> CreatePythonDefinitionContainerResolver(
        in CoreReferenceLineContext line,
        CoreExtractionLookups lookups,
        SymbolRecord? headerContainer,
        string definitionKind)
    {
        var resolveContainerForCall = line.ResolveContainerForCall;
        var lineNumber = line.LineNumber;
        return column =>
        {
            if (headerContainer != null)
                return headerContainer;

            var container = resolveContainerForCall(column);
            if (container != null)
                return container;

            var definitionContainers =
                lookups.GetPythonDefinitionContainersByLineAndKind();
            if (definitionContainers == null)
                return null;
            return definitionContainers.TryGetValue(
                (lineNumber, definitionKind),
                out var symbol)
                ? symbol
                : null;
        };
    }

    private static CorePythonReferenceLinePreparation PrepareCorePythonReferenceLine(
        in CoreReferenceLineContext line,
        CoreExtractionLookups lookups)
    {
        var headerLine = line.PreparedLine;
        var headerMap = default(PythonLogicalHeaderReferenceLine?);
        SymbolRecord? headerSymbol = null;
        lookups.GetPythonHeaderSymbolsByLine()?.TryGetValue(line.LineNumber, out headerSymbol);
        if (headerSymbol?.Signature != null
            && TryBuildPythonLogicalHeaderReferenceLine(
                line.Lines,
                line.LineIndex,
                headerSymbol.StartColumn ?? 0,
                out var builtHeaderMap))
        {
            headerLine = builtHeaderMap.Text;
            headerMap = builtHeaderMap;
        }

        var typeFactoryLine = line.PreparedLine;
        var typeFactoryMap = default(PythonLogicalHeaderReferenceLine?);
        if (line.PreparedLine.Contains("TypeVar", StringComparison.Ordinal)
            || line.PreparedLine.Contains("ParamSpec", StringComparison.Ordinal))
        {
            var startColumn = line.OriginalLine.IndexOfAny(['T', 'P']);
            if (startColumn < 0)
                startColumn = 0;
            if (TryBuildPythonLogicalStatementReferenceLine(
                    line.Lines,
                    line.LineIndex,
                    startColumn,
                    out var builtTypeFactoryMap))
            {
                typeFactoryLine = builtTypeFactoryMap.Text;
                typeFactoryMap = builtTypeFactoryMap;
            }
        }

        var headerContainer = headerSymbol ?? line.Container;
        var resolveClassContainer = headerLine.IndexOf("class", StringComparison.Ordinal) >= 0
            ? CreatePythonDefinitionContainerResolver(
                in line,
                lookups,
                headerContainer,
                "class")
            : line.ResolveContainerForCall;
        var resolveFunctionContainer = headerLine.IndexOf("def", StringComparison.Ordinal) >= 0
            ? CreatePythonDefinitionContainerResolver(
                in line,
                lookups,
                headerContainer,
                "function")
            : line.ResolveContainerForCall;
        return new CorePythonReferenceLinePreparation(
            headerLine,
            headerMap,
            headerContainer,
            resolveClassContainer,
            resolveFunctionContainer,
            typeFactoryLine,
            typeFactoryMap);
    }

    private static void EmitPythonLineReferences(
        in CoreReferenceLineContext line,
        CoreExtractionLookups lookups)
    {
        var preparation = PrepareCorePythonReferenceLine(in line, lookups);
        var referenceStart = line.References.Count;
        EmitPythonRuntimeTypeReferences(in line);
        EmitPythonDeclarationTypeReferences(in line, in preparation);

        var typeFactoryReferenceStart = line.References.Count;
        EmitPythonTypingAndFrameworkReferences(
            in line,
            preparation.TypeFactoryLine);
        if (preparation.TypeFactoryMap.HasValue)
        {
            RemapPythonLogicalHeaderReferences(
                line.References,
                typeFactoryReferenceStart,
                preparation.TypeFactoryMap.Value,
                line.Lines);
        }

        PythonReferenceExtractor.EmitDynamicImportReferences(
            line.PreparedLine, line.OriginalLine, line.References, line.Seen,
            line.FileId, line.Context, line.LineNumber, line.Container);
        if (preparation.HeaderMap.HasValue)
        {
            RemapPythonLogicalHeaderReferences(
                line.References,
                referenceStart,
                preparation.HeaderMap.Value,
                line.Lines);
        }
    }

    private static void EmitPythonRuntimeTypeReferences(
        in CoreReferenceLineContext line)
    {
        PythonReferenceExtractor.EmitDecoratorReferences(
            line.PreparedLine, line.References, line.Seen, line.FileId, line.Context,
            line.LineNumber, line.Container, line.DefinitionNames, line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitRaiseReferences(
            line.PreparedLine, line.References, line.Seen, line.FileId, line.Context,
            line.LineNumber, line.Container, line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitExceptReferences(
            line.PreparedLine, line.References, line.Seen, line.FileId, line.Context,
            line.LineNumber, line.Container, line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitIsInstanceReferences(
            line.PreparedLine, line.References, line.Seen, line.FileId, line.Context,
            line.LineNumber, line.Container, line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitIsSubclassReferences(
            line.PreparedLine, line.References, line.Seen, line.FileId, line.Context,
            line.LineNumber, line.Container, line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitCastReferences(
            line.PreparedLine, line.References, line.Seen, line.FileId, line.Context,
            line.LineNumber, line.Container, line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitAssertTypeReferences(
            line.PreparedLine, line.References, line.Seen, line.FileId, line.Context,
            line.LineNumber, line.Container, line.IsIgnoredCallName);
    }

    private static void EmitPythonDeclarationTypeReferences(
        in CoreReferenceLineContext line,
        in CorePythonReferenceLinePreparation preparation)
    {
        PythonReferenceExtractor.EmitClassBaseReferences(
            preparation.HeaderLine, line.References, line.Seen, line.FileId, line.Context,
            line.LineNumber, preparation.HeaderContainer, preparation.ResolveClassContainer,
            line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitFunctionReturnReferences(
            preparation.HeaderLine, line.References, line.Seen, line.FileId, line.Context,
            line.LineNumber, preparation.HeaderContainer, preparation.ResolveFunctionContainer,
            line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitFunctionParameterReferences(
            preparation.HeaderLine, line.References, line.Seen, line.FileId, line.Context,
            line.LineNumber, preparation.HeaderContainer, preparation.ResolveFunctionContainer,
            line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitVariableAnnotationReferences(
            line.PreparedLine, line.References, line.Seen, line.FileId, line.Context,
            line.LineNumber, line.Container, line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitTypeAliasReferences(
            line.PreparedLine, line.References, line.Seen, line.FileId, line.Context,
            line.LineNumber, line.Container, line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitNewTypeReferences(
            line.PreparedLine, line.References, line.Seen, line.FileId, line.Context,
            line.LineNumber, line.Container, line.IsIgnoredCallName);
    }

    private static void EmitPythonTypingAndFrameworkReferences(
        in CoreReferenceLineContext line,
        string typeFactoryLine)
    {
        PythonReferenceExtractor.EmitTypeVarBoundReferences(
            typeFactoryLine, line.References, line.Seen, line.FileId, line.Context,
            line.LineNumber, line.Container, line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitTypeVarConstraintReferences(
            typeFactoryLine, line.References, line.Seen, line.FileId, line.Context,
            line.LineNumber, line.Container, line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitGetTypeHintsReferences(
            line.PreparedLine, line.References, line.Seen, line.FileId, line.Context,
            line.LineNumber, line.Container, line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitDataclassesFieldsReferences(
            line.PreparedLine, line.References, line.Seen, line.FileId, line.Context,
            line.LineNumber, line.Container, line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitDataclassFieldReferences(
            line.PreparedLines, line.Lines, line.LineIndex, line.References, line.Seen,
            line.FileId, line.Container, line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitAttrsFieldsReferences(
            line.PreparedLine, line.References, line.Seen, line.FileId, line.Context,
            line.LineNumber, line.Container, line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitPydanticTypeAdapterReferences(
            line.PreparedLine, line.References, line.Seen, line.FileId, line.Context,
            line.LineNumber, line.Container, line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitPytestRaisesReferences(
            line.PreparedLine, line.References, line.Seen, line.FileId, line.Context,
            line.LineNumber, line.Container, line.IsIgnoredCallName);
        PythonReferenceExtractor.EmitContextlibSuppressReferences(
            line.PreparedLine, line.References, line.Seen, line.FileId, line.Context,
            line.LineNumber, line.Container, line.IsIgnoredCallName);
    }
}
