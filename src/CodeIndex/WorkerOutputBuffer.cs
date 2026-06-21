using System.Text;

namespace CodeIndex;

internal sealed class WorkerOutputBuffer(int maxCharacters = 4096, int maxLines = 64, int maxLineCharacters = 512)
{
    private readonly Queue<string> lines = new();
    private int characterCount;
    private bool truncated;

    internal void AppendLine(string line)
    {
        if (line.Length > maxLineCharacters)
        {
            line = line[^maxLineCharacters..];
            truncated = true;
        }

        lines.Enqueue(line);
        characterCount += line.Length + Environment.NewLine.Length;

        while (lines.Count > maxLines || characterCount > maxCharacters)
        {
            var removed = lines.Dequeue();
            characterCount -= removed.Length + Environment.NewLine.Length;
            truncated = true;
        }
    }

    internal string GetCapturedText()
    {
        if (lines.Count == 0)
            return string.Empty;

        var builder = new StringBuilder(characterCount + 64);
        if (truncated)
            builder.AppendLine("[cdidx] worker stderr truncated.");
        foreach (var line in lines)
            builder.AppendLine(line);
        return builder.ToString();
    }
}
