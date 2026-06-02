using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

internal static class PowerShellReferenceExtractor
{
    // PowerShell cmdlet / function calls are statement-start or pipeline-stage forms such as
    // `Get-ChildItem -Path .`, `Write-Host "x"`, and `$items | ForEach-Object { ... }`.
    // PowerShell の cmdlet / function 呼び出しは statement-start / pipeline 形で現れる。
    private static readonly Regex CallRegex = new(
        @"(?:^|[|;&{=]\s*)\s*(?<name>[A-Za-z_][A-Za-z0-9_]*(?:-[A-Za-z][A-Za-z0-9_]*)*)\b",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex SplatTokenRegex = new(
        @"(?<![\w$])@(?<name>[A-Za-z_][A-Za-z0-9_]*)\b",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex SplatAssignmentStartRegex = new(
        @"\$(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*@\{",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex HashtableKeyRegex = new(
        @"(?<![$@])(?:'(?<quoted>[A-Za-z_][A-Za-z0-9_]*)'|""(?<quoted>[A-Za-z_][A-Za-z0-9_]*)""|(?<bare>[A-Za-z_][A-Za-z0-9_]*))\s*=",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public static void EmitCallReferences(string preparedLine, Action<string, int> addCallLikeReference)
    {
        foreach (Match match in CallRegex.Matches(preparedLine))
        {
            var name = match.Groups["name"].Value;
            var callIndex = match.Groups["name"].Index;
            if (IsAssignmentKey(preparedLine, callIndex + name.Length))
                continue;
            addCallLikeReference(name, callIndex);
        }
    }

    public static Dictionary<string, List<SplatAssignment>> BuildSplatAssignments(string[] preparedLines)
    {
        var assignments = new Dictionary<string, List<SplatAssignment>>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < preparedLines.Length; index++)
        {
            var line = preparedLines[index];
            foreach (Match match in SplatAssignmentStartRegex.Matches(line))
            {
                var start = match.Index + match.Length;
                var builder = new System.Text.StringBuilder();
                var endLine = index;
                var depth = 1;
                var firstFragment = true;

                for (var scanLine = index; scanLine < preparedLines.Length && depth > 0; scanLine++)
                {
                    var text = preparedLines[scanLine];
                    var scanStart = scanLine == index ? start : 0;
                    if (!firstFragment)
                        builder.Append(' ');
                    firstFragment = false;

                    for (var scan = scanStart; scan < text.Length; scan++)
                    {
                        var ch = text[scan];
                        if (ch == '{')
                            depth++;
                        else if (ch == '}')
                        {
                            depth--;
                            if (depth == 0)
                            {
                                endLine = scanLine;
                                break;
                            }
                        }

                        if (depth > 0)
                            builder.Append(ch);
                    }
                }

                var keys = ExtractHashtableKeys(builder.ToString());
                if (keys.Count == 0)
                    continue;

                var name = match.Groups["name"].Value;
                if (!assignments.TryGetValue(name, out var namedAssignments))
                {
                    namedAssignments = [];
                    assignments[name] = namedAssignments;
                }

                namedAssignments.Add(new SplatAssignment(index + 1, endLine + 1, keys));
            }
        }

        return assignments;
    }

    public static void EmitSplatParameterReferences(
        string preparedLine,
        Dictionary<string, List<SplatAssignment>> splatAssignments,
        int lineNumber,
        Action<string, int> addParameterReference)
    {
        if (splatAssignments.Count == 0 || !CallRegex.IsMatch(preparedLine))
            return;

        foreach (Match splat in SplatTokenRegex.Matches(preparedLine))
        {
            var name = splat.Groups["name"].Value;
            if (!splatAssignments.TryGetValue(name, out var candidates))
                continue;

            SplatAssignment? latest = null;
            foreach (var candidate in candidates)
            {
                if (candidate.StartLine <= lineNumber)
                    latest = candidate;
            }

            if (latest == null)
                continue;

            foreach (var key in latest.Value.Keys)
                addParameterReference(key, splat.Index);
        }
    }

    private static List<string> ExtractHashtableKeys(string text)
    {
        var keys = new List<string>();
        foreach (Match match in HashtableKeyRegex.Matches(text))
        {
            var key = match.Groups["quoted"].Success
                ? match.Groups["quoted"].Value
                : match.Groups["bare"].Value;
            if (!keys.Contains(key, StringComparer.OrdinalIgnoreCase))
                keys.Add(key);
        }

        return keys;
    }

    private static bool IsAssignmentKey(string line, int cursor)
    {
        while (cursor < line.Length && char.IsWhiteSpace(line[cursor]))
            cursor++;
        return cursor < line.Length && line[cursor] == '=';
    }

    public readonly record struct SplatAssignment(int StartLine, int EndLine, IReadOnlyList<string> Keys);
}
