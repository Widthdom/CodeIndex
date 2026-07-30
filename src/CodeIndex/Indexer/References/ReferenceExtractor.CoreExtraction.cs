using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    internal static List<ReferenceRecord> ExtractCore(
        ReferenceExtractionContext request)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        var fileId = request.FileId;
        var language = request.Language;
        var content = request.Content;
        var symbols = request.Symbols;
        var path = request.Path;
        var workspaceSymbols = request.WorkspaceSymbols;
        var requestedLanguage = request.RequestedLanguage;
        var isJsxFile = IsJsxFilePath(path);
        var isRazorFile = IsRazorFilePath(path)
            || requestedLanguage is "razor" or "blazor" or "cshtml";

        if (language == "ambiguous_m")
            return ExtractAmbiguousMReferences(request);
        if (language is "clojure" or "erlang" or "ocaml" or "raku")
            return ExtractFunctionalLanguageReferences(request);

        if (TryExtractStructuralMetadataReferences(
                fileId,
                language,
                content,
                symbols,
                path,
                request.ContentIsNormalized,
                request.HasOversizeLine,
                request.ConflictMarkerLine,
                request.MaxReferenceCount,
                request.CancellationToken,
                out var structuralMetadataReferences))
        {
            return structuralMetadataReferences;
        }

        if (!TryPrepareReferenceLines(
                language,
                content,
                isRazorFile,
                request.ContentIsNormalized,
                request.HasOversizeLine,
                request.ConflictMarkerLine,
                out var preparedInput))
        {
            return [];
        }
        request.CancellationToken.ThrowIfCancellationRequested();

        content = preparedInput.Content;
        var lines = preparedInput.Lines;
        var xamlReferenceEnabled = language == "xml"
            && XamlReferenceExtractor.IsXaml(lines);
        if (language == "xml" && !xamlReferenceEnabled)
            return [];

        var structuralLines = preparedInput.StructuralLines;
        var referenceStructuralLines =
            preparedInput.ReferenceStructuralLines;
        var preparedLines = preparedInput.PreparedLines;
        var scientificNativeDependencyLimit =
            ScientificNativeReferenceExtractor.Supports(language)
                ? GetSafetyLimits().MaxNamesPerLine
                : 0;
        var typeScriptTypeAliases = language == "typescript"
            ? TypeScriptReferenceExtractor.BuildTypeAliasTargets(preparedLines)
            : null;
        var swiftTypeAliases = language == "swift"
            ? SwiftReferenceExtractor.BuildTypeAliasTargets(preparedLines)
            : null;

        var csharpAttrTables = language == "csharp"
            && content.Contains('[', StringComparison.Ordinal)
                ? BuildCSharpAttributeRanges(preparedLines)
                : (null, null);
        var csharpAttrRanges = csharpAttrTables.Item1;
        var csharpAttrTopLevelRanges = csharpAttrTables.Item2;
        var definitionNamesComparer =
            GetDefinitionNamesComparer(language);
        var definitionNamesByLine = BuildDefinitionNamesByLine(
            language,
            symbols,
            request.ReportDiagnostic);
        var scientificDefinitionNameIndicesByLine =
            BuildScientificDefinitionNameIndicesByLine(
                language,
                lines,
                symbols,
                definitionNamesByLine);
        var allDefinitionNames = language == "stylus"
            ? BuildAllDefinitionNames(
                language,
                symbols,
                request.ReportDiagnostic)
            : null;
        var fileDefinitionNames = isRazorFile
            ? BuildFileDefinitionNames(symbols)
            : null;
        var sqlDefinitionLeafSpansByLine = language == "sql"
            ? SqlReferenceExtractor.BuildDefinitionLeafSpansByLine(
                lines,
                symbols)
            : null;
        var sqlWindowFunctionCallSiteSuppressions = language == "sql"
            ? SqlReferenceExtractor
                .BuildWindowFunctionCallSiteSuppressions(structuralLines)
            : null;
        var cobolCallableSymbols = language == "cobol"
            ? BuildCobolCallableSymbols(symbols)
            : null;
        var containerCandidates = BuildReferenceContainerCandidates(
            language,
            symbols,
            request.ReportDiagnostic);
        var containerResolver =
            new InnermostContainerResolver(
                containerCandidates,
                preferCallable: language == "csharp");
        if (language == "solidity")
        {
            return ExtractSolidityReferences(
                fileId,
                lines,
                preparedLines,
                containerResolver);
        }

        var swiftPropertyDefinitionsByLine = language == "swift"
            ? BuildSwiftPropertyDefinitionsByLine(
                language,
                symbols,
                request.ReportDiagnostic)
            : null;
        var csharpTypeNameSets = language == "csharp"
            ? BuildCSharpTypeNameSets(language, symbols)
            : (
                KnownTypeNames: EmptyCSharpStringSet,
                NonEnumTypeNames: EmptyCSharpStringSet);
        var csharpKnownTypeNames = csharpTypeNameSets.KnownTypeNames;
        var localCSharpQualifiedPatternLookups = language == "csharp"
            ? BuildCSharpQualifiedPatternLookups(
                symbols,
                csharpTypeNameSets.NonEnumTypeNames)
            : null;
        var workspaceCSharpQualifiedPatternLookups = language == "csharp"
            ? request.CSharpQualifiedPatternLookups
                ?? (workspaceSymbols is { Count: > 0 }
                    ? BuildCSharpQualifiedPatternLookups(workspaceSymbols)
                    : localCSharpQualifiedPatternLookups)
            : null;
        var csharpQualifiedPatternLookups = language == "csharp"
            ? new CSharpQualifiedPatternLookups(
                workspaceCSharpQualifiedPatternLookups!.EnumMemberLookup,
                workspaceCSharpQualifiedPatternLookups.ConstantPatternMemberLookup,
                localCSharpQualifiedPatternLookups!.TypePatternLookup)
            : new CSharpQualifiedPatternLookups(
                EmptyCSharpQualifiedEnumMemberLookup,
                EmptyCSharpQualifiedPatternLookup,
                EmptyCSharpQualifiedPatternLookup);
        var csharpQualifiedEnumMemberLookup =
            csharpQualifiedPatternLookups.EnumMemberLookup;
        var csharpQualifiedConstantPatternMemberLookup =
            csharpQualifiedPatternLookups.ConstantPatternMemberLookup;
        var csharpQualifiedTypePatternLookup =
            csharpQualifiedPatternLookups.TypePatternLookup;

        HashSet<string>? kotlinConstructorTypeNames = null;
        HashSet<string>? kotlinInfixFunctionNames = null;
        if (language == "kotlin")
        {
            var kotlinNameSets =
                KotlinReferenceExtractor.BuildNameSets(language, symbols);
            kotlinConstructorTypeNames =
                kotlinNameSets.ConstructorTypeNames;
            kotlinInfixFunctionNames = kotlinNameSets.InfixFunctionNames;
            KotlinReferenceExtractor.AddDeclaredInfixFunctionNames(
                lines,
                kotlinInfixFunctionNames);
        }

        var callableDefinitionNames = language == "csharp"
            ? BuildCallableDefinitionNames(language, symbols)
            : null;
        var stylusVariableDefinitionNames = language == "stylus"
            ? CssReferenceExtractor.BuildStylusVariableDefinitionNames(lines)
            : null;
        var dockerfileNameSets = language == "dockerfile"
            ? DockerfileReferenceExtractor.BuildNameSets(language, symbols)
            : default;
        var shellNameSets = language == "shell"
            ? ShellReferenceExtractor.BuildNameSets(language, symbols)
            : default;
        var dynamicDeclarativeState =
            DynamicDeclarativeReferenceExtractor.CreateState(
                language,
                preparedLines,
                referenceStructuralLines,
                symbols);
        IReadOnlyList<(int StartLine, int EndLine)> csharpNamespaceScopes =
            language == "csharp"
                ? BuildCSharpNamespaceScopes(symbols)
                : Array.Empty<(int StartLine, int EndLine)>();
        var csharpUsingImports = language == "csharp"
            ? BuildCSharpUsingImports(
                language,
                symbols,
                csharpKnownTypeNames,
                csharpNamespaceScopes,
                lines,
                structuralLines)
            : (
                Aliases: Array.Empty<CSharpUsingAliasRecord>(),
                Namespaces: Array.Empty<CSharpUsingNamespaceRecord>(),
                Statics: Array.Empty<CSharpUsingStaticRecord>());
        var csharpUsingAliases = csharpUsingImports.Aliases;
        var csharpUsingStatics = csharpUsingImports.Statics;
        var lookups = new CoreExtractionLookups(
            request,
            language,
            symbols,
            containerCandidates,
            preparedInput.CSharpLinesInsideMultilineStringContent,
            preparedLines,
            structuralLines,
            lines,
            csharpKnownTypeNames,
            csharpUsingAliases,
            csharpUsingImports.Namespaces);

        var references = CreateReferenceList(
            request.MaxReferenceCount,
            EstimateReferenceListInitialCapacity(lines.Length));
        var seen = CreateReferenceSeenSet(lines.Length);
        if (language == "csharp")
        {
            EmitCSharpAsyncIteratorReferences(
                fileId,
                lines,
                structuralLines,
                symbols,
                references,
                seen);
            EmitCSharpStaticInterfaceMemberImplementationReferences(
                fileId,
                lines,
                structuralLines,
                symbols,
                workspaceSymbols ?? symbols,
                request.CSharpStaticInterfaceMemberLookups,
                references,
                seen);
        }
        else if (language == "rust")
        {
            RustReferenceExtractor.EmitMultilineAttributeReferences(
                preparedLines,
                references,
                seen,
                fileId,
                (lineNumber, _) =>
                    FindInnermostContainer(
                        containerCandidates,
                        lineNumber));
        }

        var pendingCSharpMultiLineTypePattern =
            EmitCoreReferenceLines(
                new CoreReferenceLoopContext
                {
                    Request = request,
                    Preparation = preparedInput,
                    IsJsxFile = isJsxFile,
                    IsRazorFile = isRazorFile,
                    XamlReferenceEnabled = xamlReferenceEnabled,
                    ScientificNativeDependencyLimit =
                        scientificNativeDependencyLimit,
                    CSharpAttributeRanges = csharpAttrRanges,
                    CSharpAttributeTopLevelRanges =
                        csharpAttrTopLevelRanges,
                    DefinitionNamesComparer = definitionNamesComparer,
                    DefinitionNamesByLine = definitionNamesByLine,
                    ScientificDefinitionNameIndicesByLine =
                        scientificDefinitionNameIndicesByLine,
                    AllDefinitionNames = allDefinitionNames,
                    FileDefinitionNames = fileDefinitionNames,
                    SqlDefinitionLeafSpansByLine =
                        sqlDefinitionLeafSpansByLine,
                    SqlWindowFunctionCallSiteSuppressions =
                        sqlWindowFunctionCallSiteSuppressions,
                    CobolCallableSymbols = cobolCallableSymbols,
                    ContainerCandidates = containerCandidates,
                    ContainerResolver = containerResolver,
                    SwiftPropertyDefinitionsByLine =
                        swiftPropertyDefinitionsByLine,
                    CSharpQualifiedEnumMemberLookup =
                        csharpQualifiedEnumMemberLookup,
                    CSharpQualifiedConstantPatternMemberLookup =
                        csharpQualifiedConstantPatternMemberLookup,
                    CSharpQualifiedTypePatternLookup =
                        csharpQualifiedTypePatternLookup,
                    KotlinConstructorTypeNames =
                        kotlinConstructorTypeNames,
                    KotlinInfixFunctionNames = kotlinInfixFunctionNames,
                    CallableDefinitionNames = callableDefinitionNames,
                    StylusVariableDefinitionNames =
                        stylusVariableDefinitionNames,
                    DockerfileStageNames =
                        dockerfileNameSets.StageNames,
                    DockerfileVariableNames =
                        dockerfileNameSets.VariableNames,
                    ShellCallableNames = shellNameSets.CallableNames,
                    ShellGlobalAliasNames =
                        shellNameSets.GlobalAliasNames,
                    DynamicDeclarativeState = dynamicDeclarativeState,
                    CSharpUsingAliases = csharpUsingAliases,
                    CSharpUsingStatics = csharpUsingStatics,
                    Lookups = lookups,
                    TypeScriptTypeAliases = typeScriptTypeAliases,
                    SwiftTypeAliases = swiftTypeAliases,
                    References = references,
                    Seen = seen,
                });

        if (!ReferenceLimitReached(references) && language == "csharp")
        {
            CSharpReferenceExtractor.EmitSwitchExpressionTypePatternReferences(
                lines,
                preparedLines,
                containerCandidates,
                csharpQualifiedConstantPatternMemberLookup,
                csharpQualifiedTypePatternLookup,
                csharpUsingAliases,
                csharpUsingStatics,
                lookups.HasActiveSameFileCSharpTypeCandidate,
                references,
                seen,
                fileId);

            CSharpReferenceExtractor
                .FlushPendingMultiLineTypePatternReference(
                    ref pendingCSharpMultiLineTypePattern,
                    csharpQualifiedConstantPatternMemberLookup,
                    csharpUsingAliases,
                    csharpUsingStatics,
                    lookups.HasActiveSameFileCSharpTypeCandidate,
                    references,
                    seen,
                    fileId);
        }

        if (language == "csharp")
        {
            RewriteCSharpPropertyReceiverReferences(
                preparedLines,
                references,
                lookups);
            RemoveCSharpCallsDuplicatedByMemberReads(references);
        }

        lookups.ApplyCSharpUsingAliasReferenceNames(references);
        if (!ReferenceLimitReached(references))
        {
            lookups.EmitCSharpBclRegexWithoutTimeoutReferences(
                references,
                seen);
        }
        MarkMutualRecursionReferences(references);
        return references;
    }

    private static void RemoveCSharpCallsDuplicatedByMemberReads(
        List<ReferenceRecord> references)
    {
        var memberReadSites = references
            .Where(reference => reference.ReferenceKind == "member_read")
            .Select(reference => (
                reference.FileId,
                reference.Line,
                reference.Column,
                reference.SymbolName,
                reference.ContainerKind,
                reference.ContainerName))
            .ToHashSet();
        if (memberReadSites.Count == 0)
            return;

        references.RemoveAll(reference =>
            reference.ReferenceKind == "call"
            && memberReadSites.Contains((
                reference.FileId,
                reference.Line,
                reference.Column,
                reference.SymbolName,
                reference.ContainerKind,
                reference.ContainerName)));
    }

    private static void RewriteCSharpPropertyReceiverReferences(
        IReadOnlyList<string> preparedLines,
        List<ReferenceRecord> references,
        CoreExtractionLookups lookups)
    {
        foreach (var reference in references)
        {
            if (reference.ReferenceKind != "type_reference"
                || reference.Line <= 0
                || reference.Line > preparedLines.Count
                || reference.Column <= 0)
            {
                continue;
            }

            var line = preparedLines[reference.Line - 1];
            var tokenEnd =
                reference.Column - 1 + reference.SymbolName.Length;
            if (tokenEnd >= line.Length
                || !line.AsSpan(tokenEnd)
                    .TrimStart()
                    .StartsWith(".", StringComparison.Ordinal))
            {
                continue;
            }

            var owner = lookups.FindCSharpContainerCandidate(
                reference.ContainerName,
                reference.Line);
            var containingType = GetContainingTypeQualifiedName(owner);
            if (containingType == null
                || !lookups.HasCSharpFieldOrPropertyMember(
                    containingType,
                    reference.SymbolName))
            {
                continue;
            }

            reference.SymbolName =
                $"{containingType}.{reference.SymbolName}";
            reference.ReferenceKind = "reference";
        }
    }
}
