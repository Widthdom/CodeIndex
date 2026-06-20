using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
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
