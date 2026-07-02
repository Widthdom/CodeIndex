using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static bool TryExtractStructuralMetadataReferences(
        long fileId,
        string language,
        string content,
        IReadOnlyList<SymbolRecord> symbols,
        string? path,
        bool contentIsNormalized,
        bool? hasOversizeLine,
        int? conflictMarkerLine,
        int? maxReferenceCount,
        CancellationToken cancellationToken,
        out List<ReferenceRecord> references)
    {
        references = [];
        if (language is not ("solution" or "dependency_manifest" or "dependency_lock"))
            return false;

        if (!TryPrepareStructuralMetadataReferenceContent(
            content,
            contentIsNormalized,
            hasOversizeLine,
            conflictMarkerLine,
            out var normalizedContent,
            out var lines))
            return true;
        cancellationToken.ThrowIfCancellationRequested();

        references = language == "solution"
            ? ExtractSolutionReferences(fileId, lines, maxReferenceCount)
            : DependencyPackageExtractor.ExtractReferences(fileId, normalizedContent, lines, symbols, path, language, maxReferenceCount);
        return true;
    }

    private static bool TryPrepareStructuralMetadataReferenceContent(
        string content,
        bool contentIsNormalized,
        bool? hasOversizeLine,
        int? conflictMarkerLine,
        out string normalizedContent,
        out string[] lines)
    {
        normalizedContent = content;
        lines = [];

        if (string.IsNullOrEmpty(content))
            return false;

        if (hasOversizeLine ?? ChunkSplitter.HasOversizeLine(content))
            return false;

        if ((conflictMarkerLine ?? FileIndexer.GetConflictMarkerLine(content)) > 0)
            return false;

        if (!contentIsNormalized)
        {
            content = FileIndexer.NormalizeContentForPrepass(content);
        }

        normalizedContent = content;
        lines = content.Split('\n');
        return true;
    }

    private static List<ReferenceRecord> ExtractSolutionReferences(long fileId, string[] lines, int? maxReferenceCount)
    {
        var references = CreateReferenceList(maxReferenceCount);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in SolutionFileParser.ExtractProjects(lines))
        {
            AddReference(
                references,
                seen,
                fileId,
                project.NormalizedProjectPath,
                project.PathIndex,
                "project_reference",
                project.Context,
                project.LineNumber,
                new SymbolRecord
                {
                    Kind = "project",
                    Name = project.Name,
                },
                "solution");
            if (ReferenceLimitReached(references))
                break;
        }

        return references;
    }
}
