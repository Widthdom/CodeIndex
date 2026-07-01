namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    private static void WriteNumberedExcerpt(int startLine, string content, string indent = "")
    {
        var lines = content.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            Console.WriteLine($"{indent}  {startLine + i,4}: {lines[i]}");
    }

    private static void WriteRepoMapSection(string title, IEnumerable<string> rows)
    {
        var materialized = rows.ToList();
        if (materialized.Count == 0)
            return;

        Console.WriteLine();
        Console.WriteLine($"{title}:");
        foreach (var row in materialized)
            Console.WriteLine($"  {row}");
    }
}
