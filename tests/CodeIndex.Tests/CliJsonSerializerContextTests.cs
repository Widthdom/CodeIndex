using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public class CliJsonSerializerContextTests
{
    public static IEnumerable<object[]> CliJsonRootTypes()
    {
        yield return [typeof(BackfillFoldJsonResult)];
        yield return [typeof(CalleeResult)];
        yield return [typeof(CallerResult)];
        yield return [typeof(CliJsonMessage)];
        yield return [typeof(CompactSearchResult)];
        yield return [typeof(CommandErrorJsonResult)];
        yield return [typeof(DefinitionResult)];
        yield return [typeof(Dictionary<string, int>)];
        yield return [typeof(Dictionary<string, long>)];
        yield return [typeof(ExactZeroHintResult)];
        yield return [typeof(FileDependencyResult)];
        yield return [typeof(FileExcerptResult)];
        yield return [typeof(FileFindResult)];
        yield return [typeof(FileIssue)];
        yield return [typeof(FileResult)];
        yield return [typeof(FreshnessHintResult)];
        yield return [typeof(GroupedHotspotResult)];
        yield return [typeof(GroupedSymbolHotspotJsonResult)];
        yield return [typeof(ImpactAnalysisResult)];
        yield return [typeof(ImpactResult)];
        yield return [typeof(IndexDryRunJsonResult)];
        yield return [typeof(IndexFreshnessCheckResult)];
        yield return [typeof(IndexFullScanJsonResult)];
        yield return [typeof(IndexFullScanSummaryJsonResult)];
        yield return [typeof(IndexUpdateJsonResult)];
        yield return [typeof(IndexUpdateSummaryJsonResult)];
        yield return [typeof(LanguageEntryJsonResult)];
        yield return [typeof(LanguagesJsonResult)];
        yield return [typeof(List<CalleeResult>)];
        yield return [typeof(List<CallerResult>)];
        yield return [typeof(List<CliJsonMessage>)];
        yield return [typeof(List<DefinitionResult>)];
        yield return [typeof(List<FileDependencyResult>)];
        yield return [typeof(List<FileIssue>)];
        yield return [typeof(List<GroupedSymbolHotspotJsonResult>)];
        yield return [typeof(List<int>)];
        yield return [typeof(List<ImpactResult>)];
        yield return [typeof(List<ReferenceResult>)];
        yield return [typeof(List<string>)];
        yield return [typeof(List<SymbolHotspotJsonResult>)];
        yield return [typeof(List<SymbolResult>)];
        yield return [typeof(List<UnusedSymbolResult>)];
        yield return [typeof(OutlineResult)];
        yield return [typeof(OutlineSymbol)];
        yield return [typeof(QueryCountFilesJsonResult)];
        yield return [typeof(QueryCountJsonResult)];
        yield return [typeof(QueryCountResult)];
        yield return [typeof(QueryFindCountJsonResult)];
        yield return [typeof(QueryPathErrorJsonResult)];
        yield return [typeof(ReferenceResult)];
        yield return [typeof(RepoEntrypointResult)];
        yield return [typeof(RepoFileSummaryResult)];
        yield return [typeof(RepoLanguageResult)];
        yield return [typeof(RepoMapResult)];
        yield return [typeof(RepoModuleResult)];
        yield return [typeof(SearchHighlight)];
        yield return [typeof(SearchResult)];
        yield return [typeof(StatusHeadFreshness)];
        yield return [typeof(StatusResult)];
        yield return [typeof(SymbolAnalysisResult)];
        yield return [typeof(SymbolHotspotJsonResult)];
        yield return [typeof(SymbolResult)];
        yield return [typeof(UnusedSymbolResult)];
    }

    [Theory]
    [MemberData(nameof(CliJsonRootTypes))]
    public void CliJsonSerializerContext_CoversEveryCliJsonRootType(Type type)
    {
        Assert.NotNull(CliJsonSerializerContext.Default.GetTypeInfo(type));
    }
}
