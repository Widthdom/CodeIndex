using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static CoreReferenceLoopContext CreateCoreReferenceLoopContext(
        CoreExtractionPreparation preparation,
        CorePreSolidityLookups preSolidityLookups)
    {
        var request = preparation.Request;
        var lines = preparation.Lines.Lines;
        var postSolidityLookups = BuildCorePostSolidityLookups(
            preparation,
            preSolidityLookups);
        var references = CreateReferenceList(
            request.MaxReferenceCount,
            EstimateReferenceListInitialCapacity(lines.Length));
        var seen = CreateReferenceSeenSet(lines.Length);

        return new CoreReferenceLoopContext
        {
            Request = request,
            Preparation = preparation.Lines,
            IsJsxFile = preparation.IsJsxFile,
            IsRazorFile = preparation.IsRazorFile,
            XamlReferenceEnabled = preparation.XamlReferenceEnabled,
            ScientificNativeDependencyLimit =
                preSolidityLookups.ScientificNativeDependencyLimit,
            CSharpAttributeRanges =
                preSolidityLookups.CSharpAttributes.Ranges,
            CSharpAttributeTopLevelRanges =
                preSolidityLookups.CSharpAttributes.TopLevelRanges,
            DefinitionNamesComparer =
                preSolidityLookups.Definitions.Comparer,
            DefinitionNamesByLine =
                preSolidityLookups.Definitions.NamesByLine,
            ScientificDefinitionNameIndicesByLine =
                preSolidityLookups.Definitions.ScientificNameIndicesByLine,
            AllDefinitionNames =
                preSolidityLookups.Definitions.AllNames,
            FileDefinitionNames =
                preSolidityLookups.Definitions.FileNames,
            SqlDefinitionLeafSpansByLine =
                preSolidityLookups.Sql.DefinitionLeafSpansByLine,
            SqlWindowFunctionCallSiteSuppressions =
                preSolidityLookups.Sql.WindowFunctionCallSiteSuppressions,
            CobolCallableSymbols = preSolidityLookups.CobolCallableSymbols,
            ContainerCandidates = preSolidityLookups.Containers.Candidates,
            ContainerResolver = preSolidityLookups.Containers.Resolver,
            SwiftPropertyDefinitionsByLine =
                postSolidityLookups.Language.SwiftPropertyDefinitionsByLine,
            CSharpQualifiedEnumMemberLookup =
                postSolidityLookups.CSharp.QualifiedEnumMembers,
            CSharpQualifiedConstantPatternMemberLookup =
                postSolidityLookups.CSharp.QualifiedConstantPatternMembers,
            CSharpQualifiedTypePatternLookup =
                postSolidityLookups.CSharp.QualifiedTypePatterns,
            KotlinConstructorTypeNames =
                postSolidityLookups.Language.KotlinConstructorTypeNames,
            KotlinInfixFunctionNames =
                postSolidityLookups.Language.KotlinInfixFunctionNames,
            CallableDefinitionNames =
                postSolidityLookups.CSharp.CallableDefinitionNames,
            StylusVariableDefinitionNames =
                postSolidityLookups.Language.StylusVariableDefinitionNames,
            DockerfileStageNames =
                postSolidityLookups.Language.DockerfileStageNames,
            DockerfileVariableNames =
                postSolidityLookups.Language.DockerfileVariableNames,
            ShellCallableNames =
                postSolidityLookups.Language.ShellCallableNames,
            ShellGlobalAliasNames =
                postSolidityLookups.Language.ShellGlobalAliasNames,
            DynamicDeclarativeState =
                postSolidityLookups.DynamicDeclarativeState,
            CSharpUsingAliases = postSolidityLookups.CSharp.UsingAliases,
            CSharpUsingStatics = postSolidityLookups.CSharp.UsingStatics,
            Lookups = postSolidityLookups.Extraction,
            TypeScriptTypeAliases =
                preSolidityLookups.TypeScriptTypeAliases,
            SwiftTypeAliases = preSolidityLookups.SwiftTypeAliases,
            References = references,
            Seen = seen,
        };
    }
}
