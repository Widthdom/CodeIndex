using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private readonly record struct CoreCSharpAttributeLookups(
        List<(int start, int end)>?[]? Ranges,
        List<(int start, int end)>?[]? TopLevelRanges);

    private readonly record struct CoreDefinitionLookups(
        StringComparer Comparer,
        IReadOnlyDictionary<int, HashSet<string>> NamesByLine,
        IReadOnlyDictionary<int, Dictionary<string, HashSet<int>>>? ScientificNameIndicesByLine,
        IReadOnlySet<string>? AllNames,
        IReadOnlySet<string>? FileNames);

    private readonly record struct CoreSqlLookups(
        Dictionary<int, List<SqlReferenceExtractor.DefinitionLeafSpan>>? DefinitionLeafSpansByLine,
        HashSet<(int LineNumber, int ColumnIndex)>? WindowFunctionCallSiteSuppressions);

    private readonly record struct CoreContainerLookups(
        IReadOnlyList<SymbolRecord> Candidates,
        InnermostContainerResolver Resolver);

    private readonly record struct CorePreSolidityLookups(
        int ScientificNativeDependencyLimit,
        IReadOnlyList<TypeScriptReferenceExtractor.TypeAliasBinding>? TypeScriptTypeAliases,
        IReadOnlyList<SwiftReferenceExtractor.TypeAliasBinding>? SwiftTypeAliases,
        CoreCSharpAttributeLookups CSharpAttributes,
        CoreDefinitionLookups Definitions,
        CoreSqlLookups Sql,
        IReadOnlyList<SymbolRecord>? CobolCallableSymbols,
        CoreContainerLookups Containers);

    private readonly record struct CoreCSharpPatternLookups(
        IReadOnlySet<string> KnownTypeNames,
        IReadOnlyDictionary<string, List<(string EnumName, string? QualifiedEnumName, bool AllowShortNameFallback)>> QualifiedEnumMembers,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> QualifiedConstantPatternMembers,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> QualifiedTypePatterns);

    private readonly record struct CoreCSharpLoopLookups(
        IReadOnlySet<string> KnownTypeNames,
        IReadOnlyDictionary<string, List<(string EnumName, string? QualifiedEnumName, bool AllowShortNameFallback)>> QualifiedEnumMembers,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> QualifiedConstantPatternMembers,
        IReadOnlyDictionary<string, List<(string ContainerName, string? QualifiedContainerName, bool AllowShortNameFallback)>> QualifiedTypePatterns,
        IReadOnlyList<CSharpUsingAliasRecord> UsingAliases,
        IReadOnlyList<CSharpUsingNamespaceRecord> UsingNamespaces,
        IReadOnlyList<CSharpUsingStaticRecord> UsingStatics,
        HashSet<string>? CallableDefinitionNames);

    private readonly record struct CoreLanguageLoopLookups(
        IReadOnlyDictionary<int, SymbolRecord[]>? SwiftPropertyDefinitionsByLine,
        HashSet<string>? KotlinConstructorTypeNames,
        HashSet<string>? KotlinInfixFunctionNames,
        HashSet<string>? StylusVariableDefinitionNames,
        HashSet<string>? DockerfileStageNames,
        HashSet<string>? DockerfileVariableNames,
        HashSet<string>? ShellCallableNames,
        HashSet<string>? ShellGlobalAliasNames);

    private readonly record struct CorePostSolidityLookups(
        CoreCSharpLoopLookups CSharp,
        CoreLanguageLoopLookups Language,
        DynamicDeclarativeReferenceExtractor.ExtractionState? DynamicDeclarativeState,
        CoreExtractionLookups Extraction);

    private static CorePreSolidityLookups BuildCorePreSolidityLookups(
        CoreExtractionPreparation preparation)
    {
        var request = preparation.Request;
        var language = request.Language;
        var lines = preparation.Lines.Lines;
        var structuralLines = preparation.Lines.StructuralLines;
        var preparedLines = preparation.Lines.PreparedLines;
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

        var csharpAttributeTables = language == "csharp"
            && preparation.Lines.Content.Contains('[', StringComparison.Ordinal)
                ? BuildCSharpAttributeRanges(preparedLines)
                : (null, null);
        var definitionNamesComparer = GetDefinitionNamesComparer(language);
        var definitionNamesByLine = BuildDefinitionNamesByLine(
            language,
            request.Symbols,
            request.ReportDiagnostic);
        var scientificDefinitionNameIndicesByLine =
            BuildScientificDefinitionNameIndicesByLine(
                language,
                lines,
                request.Symbols,
                definitionNamesByLine);
        var allDefinitionNames = language == "stylus"
            ? BuildAllDefinitionNames(
                language,
                request.Symbols,
                request.ReportDiagnostic)
            : null;
        var fileDefinitionNames = preparation.IsRazorFile
            ? BuildFileDefinitionNames(request.Symbols)
            : null;
        var sqlDefinitionLeafSpansByLine = language == "sql"
            ? SqlReferenceExtractor.BuildDefinitionLeafSpansByLine(
                lines,
                request.Symbols)
            : null;
        var sqlWindowFunctionCallSiteSuppressions = language == "sql"
            ? SqlReferenceExtractor
                .BuildWindowFunctionCallSiteSuppressions(structuralLines)
            : null;
        var cobolCallableSymbols = language == "cobol"
            ? BuildCobolCallableSymbols(request.Symbols)
            : null;
        var containerCandidates = BuildReferenceContainerCandidates(
            language,
            request.Symbols,
            request.ReportDiagnostic);
        var containerResolver = new InnermostContainerResolver(
            containerCandidates,
            preferCallable: language == "csharp");

        return new CorePreSolidityLookups(
            scientificNativeDependencyLimit,
            typeScriptTypeAliases,
            swiftTypeAliases,
            new CoreCSharpAttributeLookups(
                csharpAttributeTables.Item1,
                csharpAttributeTables.Item2),
            new CoreDefinitionLookups(
                definitionNamesComparer,
                definitionNamesByLine,
                scientificDefinitionNameIndicesByLine,
                allDefinitionNames,
                fileDefinitionNames),
            new CoreSqlLookups(
                sqlDefinitionLeafSpansByLine,
                sqlWindowFunctionCallSiteSuppressions),
            cobolCallableSymbols,
            new CoreContainerLookups(
                containerCandidates,
                containerResolver));
    }

    private static CoreCSharpPatternLookups BuildCoreCSharpPatternLookups(
        CoreExtractionPreparation preparation)
    {
        var request = preparation.Request;
        if (request.Language != "csharp")
        {
            return new CoreCSharpPatternLookups(
                EmptyCSharpStringSet,
                EmptyCSharpQualifiedEnumMemberLookup,
                EmptyCSharpQualifiedPatternLookup,
                EmptyCSharpQualifiedPatternLookup);
        }

        var typeNameSets = BuildCSharpTypeNameSets(
            request.Language,
            request.Symbols);
        var localQualifiedPatternLookups =
            BuildCSharpQualifiedPatternLookups(
                request.Symbols,
                typeNameSets.NonEnumTypeNames);
        var workspaceQualifiedPatternLookups =
            request.CSharpQualifiedPatternLookups
            ?? (request.WorkspaceSymbols is { Count: > 0 }
                ? BuildCSharpQualifiedPatternLookups(request.WorkspaceSymbols)
                : localQualifiedPatternLookups);
        var qualifiedPatternLookups = new CSharpQualifiedPatternLookups(
            workspaceQualifiedPatternLookups.EnumMemberLookup,
            workspaceQualifiedPatternLookups.ConstantPatternMemberLookup,
            localQualifiedPatternLookups.TypePatternLookup);
        return new CoreCSharpPatternLookups(
            typeNameSets.KnownTypeNames,
            qualifiedPatternLookups.EnumMemberLookup,
            qualifiedPatternLookups.ConstantPatternMemberLookup,
            qualifiedPatternLookups.TypePatternLookup);
    }

    private static CorePostSolidityLookups BuildCorePostSolidityLookups(
        CoreExtractionPreparation preparation,
        CorePreSolidityLookups preSolidityLookups)
    {
        var request = preparation.Request;
        var language = request.Language;
        var lines = preparation.Lines.Lines;
        var structuralLines = preparation.Lines.StructuralLines;
        var referenceStructuralLines =
            preparation.Lines.ReferenceStructuralLines;
        var preparedLines = preparation.Lines.PreparedLines;

        var swiftPropertyDefinitionsByLine = language == "swift"
            ? BuildSwiftPropertyDefinitionsByLine(
                language,
                request.Symbols,
                request.ReportDiagnostic)
            : null;
        var csharpPatterns = BuildCoreCSharpPatternLookups(preparation);

        HashSet<string>? kotlinConstructorTypeNames = null;
        HashSet<string>? kotlinInfixFunctionNames = null;
        if (language == "kotlin")
        {
            var kotlinNameSets = KotlinReferenceExtractor.BuildNameSets(
                language,
                request.Symbols);
            kotlinConstructorTypeNames = kotlinNameSets.ConstructorTypeNames;
            kotlinInfixFunctionNames = kotlinNameSets.InfixFunctionNames;
            KotlinReferenceExtractor.AddDeclaredInfixFunctionNames(
                lines,
                kotlinInfixFunctionNames);
        }

        var callableDefinitionNames = language == "csharp"
            ? BuildCallableDefinitionNames(language, request.Symbols)
            : null;
        var stylusVariableDefinitionNames = language == "stylus"
            ? CssReferenceExtractor.BuildStylusVariableDefinitionNames(lines)
            : null;
        var dockerfileNameSets = language == "dockerfile"
            ? DockerfileReferenceExtractor.BuildNameSets(
                language,
                request.Symbols)
            : default;
        var shellNameSets = language == "shell"
            ? ShellReferenceExtractor.BuildNameSets(
                language,
                request.Symbols)
            : default;
        var dynamicDeclarativeState =
            DynamicDeclarativeReferenceExtractor.CreateState(
                language,
                preparedLines,
                referenceStructuralLines,
                request.Symbols);
        IReadOnlyList<(int StartLine, int EndLine)> csharpNamespaceScopes =
            language == "csharp"
                ? BuildCSharpNamespaceScopes(request.Symbols)
                : Array.Empty<(int StartLine, int EndLine)>();
        var csharpUsingImports = language == "csharp"
            ? BuildCSharpUsingImports(
                language,
                request.Symbols,
                csharpPatterns.KnownTypeNames,
                csharpNamespaceScopes,
                lines,
                structuralLines)
            : (
                Aliases: Array.Empty<CSharpUsingAliasRecord>(),
                Namespaces: Array.Empty<CSharpUsingNamespaceRecord>(),
                Statics: Array.Empty<CSharpUsingStaticRecord>());
        var csharpLookups = new CoreCSharpLoopLookups(
            csharpPatterns.KnownTypeNames,
            csharpPatterns.QualifiedEnumMembers,
            csharpPatterns.QualifiedConstantPatternMembers,
            csharpPatterns.QualifiedTypePatterns,
            csharpUsingImports.Aliases,
            csharpUsingImports.Namespaces,
            csharpUsingImports.Statics,
            callableDefinitionNames);
        var languageLookups = new CoreLanguageLoopLookups(
            swiftPropertyDefinitionsByLine,
            kotlinConstructorTypeNames,
            kotlinInfixFunctionNames,
            stylusVariableDefinitionNames,
            dockerfileNameSets.StageNames,
            dockerfileNameSets.VariableNames,
            shellNameSets.CallableNames,
            shellNameSets.GlobalAliasNames);
        var extractionLookups = new CoreExtractionLookups(
            request,
            language,
            request.Symbols,
            preSolidityLookups.Containers.Candidates,
            preparation.Lines.CSharpLinesInsideMultilineStringContent,
            preparedLines,
            structuralLines,
            lines,
            csharpLookups.KnownTypeNames,
            csharpLookups.UsingAliases,
            csharpLookups.UsingNamespaces);

        return new CorePostSolidityLookups(
            csharpLookups,
            languageLookups,
            dynamicDeclarativeState,
            extractionLookups);
    }
}
