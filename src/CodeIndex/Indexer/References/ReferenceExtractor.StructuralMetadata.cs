using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static bool TryExtractStructuralMetadataReferences(
        long fileId,
        string language,
        string content,
        string? path,
        bool contentIsNormalized,
        bool? hasOversizeLine,
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
            out var normalizedContent,
            out var lines))
            return true;
        cancellationToken.ThrowIfCancellationRequested();

        references = language == "solution"
            ? ExtractSolutionReferences(fileId, lines)
            : DependencyPackageExtractor.ExtractReferences(fileId, normalizedContent, lines, path, language);
        return true;
    }

    private static bool TryPrepareStructuralMetadataReferenceContent(
        string content,
        bool contentIsNormalized,
        bool? hasOversizeLine,
        out string normalizedContent,
        out string[] lines)
    {
        normalizedContent = content;
        lines = [];

        if (string.IsNullOrEmpty(content))
            return false;

        if (hasOversizeLine ?? ChunkSplitter.HasOversizeLine(content))
            return false;

        if (FileIndexer.HasConflictMarkers(content))
            return false;

        if (!contentIsNormalized)
        {
            content = FileIndexer.NormalizeContentForPrepass(content);
        }

        normalizedContent = content;
        lines = content.Split('\n');
        return true;
    }

    private static List<ReferenceRecord> ExtractSolutionReferences(long fileId, string[] lines)
    {
        var references = CreateReferenceList(null);
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
        }

        return references;
    }
}
