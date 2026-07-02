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
        if (!HasCallStartCandidate(preparedLine))
        {
            return;
        }

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
        Dictionary<string, List<SplatAssignment>>? assignments = null;
        for (var index = 0; index < preparedLines.Length; index++)
        {
            var line = preparedLines[index];
            if (line.IndexOf("@{", StringComparison.Ordinal) < 0
                || line.IndexOf('$') < 0
                || line.IndexOf('=') < 0)
            {
                continue;
            }

            foreach (Match match in SplatAssignmentStartRegex.Matches(line))
            {
                var start = match.Index + match.Length;
                var builder = new System.Text.StringBuilder(Math.Max(0, line.Length - start));
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
                assignments ??= new Dictionary<string, List<SplatAssignment>>(StringComparer.OrdinalIgnoreCase);
                if (!assignments.TryGetValue(name, out var namedAssignments))
                {
                    namedAssignments = [];
                    assignments[name] = namedAssignments;
                }

                namedAssignments.Add(new SplatAssignment(index + 1, endLine + 1, keys));
            }
        }

        return assignments ?? [];
    }

    public static void EmitSplatParameterReferences(
        string preparedLine,
        Dictionary<string, List<SplatAssignment>> splatAssignments,
        int lineNumber,
        Action<string, int> addParameterReference)
    {
        if (splatAssignments.Count == 0 || preparedLine.IndexOf('@') < 0)
            return;
        if (!CallRegex.IsMatch(preparedLine))
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
        if (text.IndexOf('=') < 0)
            return [];

        List<string>? keys = null;
        foreach (Match match in HashtableKeyRegex.Matches(text))
        {
            var key = match.Groups["quoted"].Success
                ? match.Groups["quoted"].Value
                : match.Groups["bare"].Value;

            if (keys == null)
            {
                keys = [key];
            }
            else if (!ContainsPowerShellHashtableKey(keys, key))
            {
                keys.Add(key);
            }
        }

        return keys ?? [];
    }

    private static bool ContainsPowerShellHashtableKey(List<string> keys, string key)
    {
        foreach (var existing in keys)
        {
            if (string.Equals(existing, key, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool HasCallStartCandidate(string line)
    {
        var expectCommand = true;
        for (var index = 0; index < line.Length; index++)
        {
            var ch = line[index];
            if (char.IsWhiteSpace(ch) && expectCommand)
            {
                continue;
            }

            if (expectCommand && IsCallNameStart(ch))
            {
                return true;
            }

            expectCommand = ch is '|' or ';' or '&' or '{' or '=';
        }

        return false;
    }

    private static bool IsCallNameStart(char ch) => ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';

    private static bool IsAssignmentKey(string line, int cursor)
    {
        while (cursor < line.Length && char.IsWhiteSpace(line[cursor]))
            cursor++;
        return cursor < line.Length && line[cursor] == '=';
    }

    public readonly record struct SplatAssignment(int StartLine, int EndLine, IReadOnlyList<string> Keys);
}
