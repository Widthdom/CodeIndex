using System.Text.RegularExpressions;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static class ShellReferenceExtractor
{
    private static readonly Regex CommandCallRegex = new(
        @"(?:^\s*(?!(?:if|then|do|else|elif|while|until|time|fi)\b)|[|;&{]\s*|&&\s*|\|\|\s*|!\s+|\b(?:if|then|do|else|elif|while|until|time)\s+)(?<name>[A-Za-z_][A-Za-z0-9_-]*)(?=\s|$|[;|&}])",
        RegexOptions.Compiled);

    private static readonly Regex SourceReferenceRegex = new(
        @"(?:^\s*(?!(?:if|then|do|else|elif|while|until|time|fi)\b)|[|;&{]\s*|&&\s*|\|\|\s*|!\s+|\b(?:if|then|do|else|elif|while|until|time)\s+)(?:source|\.)\s+(?<name>(?:'[^']*'|""[^""]*""|[^;\s&#|}]+))",
        RegexOptions.Compiled);

    private static readonly Regex GlobalAliasSignatureRegex = new(
        @"^alias(?:\s+-[^\s=]+)*\s+-g\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static HashSet<string>? BuildCallableNames(string language, IReadOnlyList<SymbolRecord> symbols)
    {
        if (language != "shell")
            return null;

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
        {
            if (symbol.Kind is not ("function" or "alias") || string.IsNullOrWhiteSpace(symbol.Name))
                continue;

            names.Add(symbol.Name);
        }

        return names;
    }

    public static HashSet<string>? BuildGlobalAliasNames(string language, IReadOnlyList<SymbolRecord> symbols)
    {
        if (language != "shell")
            return null;

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "alias" || string.IsNullOrWhiteSpace(symbol.Name))
                continue;

            var signature = symbol.Signature?.TrimStart();
            if (string.IsNullOrWhiteSpace(signature))
                continue;

            if (!GlobalAliasSignatureRegex.IsMatch(signature))
                continue;

            names.Add(symbol.Name);
        }

        return names;
    }

    public static void EmitReferences(
        string preparedLine,
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        HashSet<string>? callableNames,
        HashSet<string>? globalAliasNames,
        Func<int, SymbolRecord?> resolveContainerForCall,
        Action<string, int> addCallLikeReference)
    {
        foreach (Match match in CommandCallRegex.Matches(preparedLine))
        {
            var name = match.Groups["name"].Value;
            if (callableNames == null || !callableNames.Contains(name))
                continue;

            var callIndex = match.Groups["name"].Index;
            addCallLikeReference(name, callIndex);
        }

        var shellSourceLine = StripComment(originalLine);

        // CommandCallRegex requires a statement-start anchor (line start, `;`, `&&`, etc.),
        // so calls embedded in `$(...)` command substitution or `` `...` `` backticks are
        // never matched against `preparedLine` alone. PrepareLine also blanks backtick
        // spans via StringLiteralRegex. Re-scan the raw line so nested `helper` calls in
        // `result=$(helper)` or ``output=`helper arg` `` still emit call edges (#1499).
        EmitSubstitutionCalls(
            shellSourceLine,
            0,
            shellSourceLine.Length,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            callableNames,
            resolveContainerForCall);

        foreach (Match match in SourceReferenceRegex.Matches(shellSourceLine))
        {
            var name = NormalizeSourceTargetToken(match.Groups["name"].Value);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var sourceIndex = match.Groups["name"].Index;
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                name,
                sourceIndex,
                "reference",
                context,
                lineNumber,
                resolveContainerForCall(sourceIndex));
        }

        if (globalAliasNames == null || globalAliasNames.Count == 0)
            return;

        var trimmedPreparedLine = preparedLine.TrimStart();
        if (trimmedPreparedLine.StartsWith("alias", StringComparison.Ordinal))
            return;

        foreach (var aliasName in globalAliasNames)
        {
            var searchIndex = 0;
            while (searchIndex < preparedLine.Length)
            {
                var aliasIndex = preparedLine.IndexOf(aliasName, searchIndex, StringComparison.Ordinal);
                if (aliasIndex < 0)
                    break;

                if (!IsGlobalAliasReferenceBoundary(preparedLine, aliasIndex, aliasName.Length))
                {
                    searchIndex = aliasIndex + aliasName.Length;
                    continue;
                }

                addCallLikeReference(aliasName, aliasIndex);
                searchIndex = aliasIndex + aliasName.Length;
            }
        }
    }

    private static void EmitSubstitutionCalls(
        string line,
        int spanStart,
        int spanEnd,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        HashSet<string>? callableNames,
        Func<int, SymbolRecord?> resolveContainerForCall)
    {
        if (callableNames == null || callableNames.Count == 0)
            return;
        if (spanStart < 0 || spanEnd > line.Length || spanStart >= spanEnd)
            return;

        var i = spanStart;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        while (i < spanEnd)
        {
            var ch = line[i];

            if (inSingleQuote)
            {
                if (ch == '\'')
                    inSingleQuote = false;
                i++;
                continue;
            }

            if (ch == '\\' && i + 1 < spanEnd)
            {
                i += 2;
                continue;
            }

            if (!inDoubleQuote && ch == '\'')
            {
                inSingleQuote = true;
                i++;
                continue;
            }

            if (ch == '"')
            {
                inDoubleQuote = !inDoubleQuote;
                i++;
                continue;
            }

            if (ch == '$' && i + 1 < spanEnd && line[i + 1] == '(')
            {
                var innerStart = i + 2;
                var innerEnd = FindSubstitutionParenClose(line, innerStart, spanEnd);
                if (innerEnd < 0)
                    return;

                ScanSubstitutionInner(
                    line,
                    innerStart,
                    innerEnd,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    callableNames,
                    resolveContainerForCall);
                i = innerEnd + 1;
                continue;
            }

            if (!inSingleQuote && ch == '`')
            {
                var innerStart = i + 1;
                var innerEnd = FindBacktickClose(line, innerStart, spanEnd);
                if (innerEnd < 0)
                    return;

                ScanSubstitutionInner(
                    line,
                    innerStart,
                    innerEnd,
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    callableNames,
                    resolveContainerForCall);
                i = innerEnd + 1;
                continue;
            }

            i++;
        }
    }

    private static void ScanSubstitutionInner(
        string line,
        int innerStart,
        int innerEnd,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        HashSet<string> callableNames,
        Func<int, SymbolRecord?> resolveContainerForCall)
    {
        if (innerStart >= innerEnd)
            return;

        // CommandCallRegex anchors against statement-start tokens (`^`, `;`, `&&`, ...).
        // Prepend `;` so the first identifier inside the substitution is recognized as a
        // command position even though it is not literally at the start of a line.
        var inner = line.Substring(innerStart, innerEnd - innerStart);
        var virtualLine = ";" + inner;
        foreach (Match match in CommandCallRegex.Matches(virtualLine))
        {
            var name = match.Groups["name"].Value;
            if (!callableNames.Contains(name))
                continue;

            // -1 to undo the synthetic `;` prefix when mapping back to the real line.
            var callIndex = innerStart + match.Groups["name"].Index - 1;
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                name,
                callIndex,
                "call",
                context,
                lineNumber,
                resolveContainerForCall(callIndex));
        }

        EmitSubstitutionCalls(
            line,
            innerStart,
            innerEnd,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            callableNames,
            resolveContainerForCall);
    }

    private static int FindSubstitutionParenClose(string line, int start, int spanEnd)
    {
        var depth = 1;
        var i = start;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        while (i < spanEnd)
        {
            var ch = line[i];

            if (inSingleQuote)
            {
                if (ch == '\'')
                    inSingleQuote = false;
                i++;
                continue;
            }

            if (ch == '\\' && i + 1 < spanEnd)
            {
                i += 2;
                continue;
            }

            if (!inDoubleQuote && ch == '\'')
            {
                inSingleQuote = true;
                i++;
                continue;
            }

            if (ch == '"')
            {
                inDoubleQuote = !inDoubleQuote;
                i++;
                continue;
            }

            if (ch == '$' && i + 1 < spanEnd && line[i + 1] == '(')
            {
                depth++;
                i += 2;
                continue;
            }

            if (!inDoubleQuote && ch == '(')
            {
                depth++;
                i++;
                continue;
            }

            if (ch == ')')
            {
                depth--;
                if (depth == 0)
                    return i;
                i++;
                continue;
            }

            i++;
        }

        return -1;
    }

    private static int FindBacktickClose(string line, int start, int spanEnd)
    {
        var i = start;
        while (i < spanEnd)
        {
            var ch = line[i];
            if (ch == '\\' && i + 1 < spanEnd)
            {
                i += 2;
                continue;
            }

            if (ch == '`')
                return i;

            i++;
        }

        return -1;
    }

    private static string NormalizeSourceTargetToken(string token)
    {
        var trimmed = token.Trim();
        if (trimmed.Length >= 2)
        {
            var quote = trimmed[0];
            if ((quote == '\'' || quote == '"') && trimmed[^1] == quote)
                return trimmed[1..^1];
        }

        return trimmed;
    }

    private static string StripComment(string line)
    {
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var escapeNext = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (escapeNext)
            {
                escapeNext = false;
                continue;
            }

            if (inSingleQuote)
            {
                if (ch == '\'')
                    inSingleQuote = false;
                continue;
            }

            if (inDoubleQuote)
            {
                if (ch == '"')
                {
                    inDoubleQuote = false;
                    continue;
                }

                if (ch == '\\')
                    escapeNext = true;
                continue;
            }

            if (ch == '#')
                return line[..i];

            if (ch == '\'')
            {
                inSingleQuote = true;
                continue;
            }

            if (ch == '"')
            {
                inDoubleQuote = true;
                continue;
            }

            if (ch == '\\')
                escapeNext = true;
        }

        return line;
    }

    private static bool IsGlobalAliasReferenceBoundary(string text, int startIndex, int length)
    {
        if (startIndex < 0 || length <= 0 || startIndex + length > text.Length)
            return false;

        if (startIndex > 0 && !IsGlobalAliasBoundarySeparator(text[startIndex - 1]))
            return false;

        var endIndex = startIndex + length;
        return endIndex >= text.Length || IsGlobalAliasBoundarySeparator(text[endIndex]);
    }

    private static bool IsGlobalAliasBoundarySeparator(char ch) =>
        char.IsWhiteSpace(ch) || ch is '|' or ';' or '&' or '{' or '}' or '(' or ')' or '!';
}
