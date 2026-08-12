using CodeIndex.Models;

namespace CodeIndex.Database;

public partial class DbReader
{
    public List<FileDependencyResult> GetFileDependencyCycleCandidates(
        int limit,
        out int candidateRowCount,
        string? lang = null,
        IReadOnlyList<string>? pathPatterns = null,
        IReadOnlyList<string>? excludePathPatterns = null,
        bool excludeTests = false,
        bool reverse = false,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string>? dependencySymbols = null,
        IReadOnlyList<string>? dependencySymbolFamilies = null,
        bool suppressDependencyNoise = false)
    {
        candidateRowCount = 0;
        lang = NormalizeQueryLanguage(lang);
        if (!_hasReferencesTable || limit <= 0)
            return [];

        cancellationToken.ThrowIfCancellationRequested();
        var request = new DependencyQueryRequest(
            limit,
            lang,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            reverse,
            dependencySymbols,
            dependencySymbolFamilies,
            suppressDependencyNoise);
        return ExecuteDependencyCycleQuery(
            BuildDependencyCycleQueryPlan(request),
            cancellationToken,
            out candidateRowCount);
    }
}
