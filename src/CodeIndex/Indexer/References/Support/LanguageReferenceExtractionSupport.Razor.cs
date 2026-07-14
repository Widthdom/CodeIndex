using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class LanguageReferenceExtractionSupport
{
    public static void EmitRazorReferences(
        string originalLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlySet<string>? definitionNames,
        IReadOnlySet<string>? fileDefinitionNames,
        IReadOnlyList<string>? implementedTypeNames)
    {
        if (originalLine.IndexOf('<') >= 0)
        {
            foreach (Match match in RazorComponentTagRegex.Matches(originalLine))
            {
                var group = match.Groups["name"];
                var rawName = group.Value;
                var name = rawName;
                if (definitionNames?.Contains(name) == true)
                    continue;
                var nameIndex = group.Index;

                ReferenceExtractor.AddReference(
                    references,
                    seen,
                    fileId,
                    name,
                    nameIndex,
                    "call",
                    context,
                    lineNumber,
                    resolveContainerForColumn(nameIndex));
            }
        }

        if (originalLine.IndexOf('@') >= 0)
        {
            if (originalLine.IndexOf("inherits", StringComparison.Ordinal) >= 0
                || originalLine.IndexOf("implements", StringComparison.Ordinal) >= 0
                || originalLine.IndexOf("model", StringComparison.Ordinal) >= 0)
            {
                foreach (var match in EnumerateMatches(RazorDirectiveTypeRegex, originalLine))
                    EmitRazorTypeReference(match);
            }

            if (originalLine.IndexOf("@attribute", StringComparison.Ordinal) >= 0 && originalLine.IndexOf('[') >= 0)
            {
                foreach (var match in EnumerateMatches(RazorAttributeTypeRegex, originalLine))
                    EmitRazorTypeReference(match);
            }

            if (originalLine.IndexOf("@inject", StringComparison.Ordinal) >= 0)
            {
                foreach (var match in EnumerateMatches(RazorInjectRegex, originalLine))
                    EmitRazorTypeReference(match);
            }
        }

        if (originalLine.IndexOf("@on", StringComparison.Ordinal) >= 0 && originalLine.IndexOf('=') >= 0)
        {
            foreach (Match match in RazorEventHandlerRegex.Matches(originalLine))
            {
                var name = match.Groups["name"].Value;
                var nameIndex = match.Groups["name"].Index;
                var container = resolveContainerForColumn(nameIndex);
                var kind = "razor_event_binding";

                ReferenceExtractor.AddReference(
                    references,
                    seen,
                    fileId,
                    name,
                    nameIndex,
                    kind,
                    context,
                    lineNumber,
                    container);

                if (fileDefinitionNames?.Contains(name) == true || implementedTypeNames is not { Count: > 0 })
                    continue;

                foreach (var implementedTypeName in implementedTypeNames)
                {
                    ReferenceExtractor.AddReference(
                        references,
                        seen,
                        fileId,
                        name,
                        nameIndex,
                        "implicit_implementation",
                        context,
                        lineNumber,
                        new SymbolRecord
                        {
                            Kind = "interface",
                            Name = LastQualifiedSegment(implementedTypeName),
                            Line = lineNumber,
                            StartLine = lineNumber,
                            EndLine = lineNumber
                        });
                }
            }
        }

        void EmitRazorTypeReference(Match match)
        {
            var group = match.Groups["type"];
            ReferenceExtractor.AddTypeExpressionSegments(
                references,
                seen,
                fileId,
                group.Value,
                group.Index,
                context,
                lineNumber,
                resolveContainerForColumn(group.Index),
                "csharp");
        }
    }

    public static IReadOnlyList<string> ExtractRazorImplementedTypeNames(IReadOnlyList<string> originalLines)
    {
        if (!MayContainRazorImplementsDirective(originalLines))
            return Array.Empty<string>();

        List<string>? result = null;
        foreach (var line in originalLines)
        {
            var match = RazorDirectiveTypeRegex.Match(line);
            if (!match.Success || !line.TrimStart().StartsWith("@implements", StringComparison.Ordinal))
                continue;

            result ??= new List<string>(2);
            result.Add(match.Groups["type"].Value);
        }

        return result ?? (IReadOnlyList<string>)Array.Empty<string>();
    }

    private static bool MayContainRazorImplementsDirective(IReadOnlyList<string> originalLines)
    {
        foreach (var line in originalLines)
        {
            if (line.Contains("@implements", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    public static string[] MaskRazorCommentLines(IReadOnlyList<string> originalLines)
    {
        if (!MayContainRazorMaskingConstruct(originalLines))
            return ReuseOrCopyRazorLines(originalLines);

        var result = new string[originalLines.Count];
        var inRazorComment = false;
        var inHtmlComment = false;
        var inCodeBlock = false;
        var codeDepth = 0;
        var inCSharpBlockComment = false;
        var razorControlDepth = 0;
        var inRazorControlBlockComment = false;
        var pendingRazorControlBlock = false;

        for (var lineIndex = 0; lineIndex < originalLines.Count; lineIndex++)
        {
            var line = originalLines[lineIndex];
            var chars = line.ToCharArray();
            var cursor = 0;
            while (cursor < line.Length)
            {
                if (inRazorComment)
                {
                    var close = line.IndexOf("*@", cursor, StringComparison.Ordinal);
                    var end = close < 0 ? line.Length : close + 2;
                    MaskRange(chars, cursor, end);
                    inRazorComment = close < 0;
                    cursor = end;
                    continue;
                }

                if (inHtmlComment)
                {
                    var close = line.IndexOf("-->", cursor, StringComparison.Ordinal);
                    var end = close < 0 ? line.Length : close + 3;
                    MaskRange(chars, cursor, end);
                    inHtmlComment = close < 0;
                    cursor = end;
                    continue;
                }

                if (line.AsSpan(cursor).StartsWith("@*", StringComparison.Ordinal))
                {
                    inRazorComment = true;
                    continue;
                }

                if (line.AsSpan(cursor).StartsWith("<!--", StringComparison.Ordinal))
                {
                    inHtmlComment = true;
                    continue;
                }

                cursor++;
            }

            var codeScanStart = 0;
            if (!inCodeBlock)
            {
                codeScanStart = IndexOfRazorCodeDirective(chars);
                if (codeScanStart >= 0)
                    inCodeBlock = true;
                else
                {
                    codeScanStart = IndexOfRazorExplicitCodeBlock(chars);
                    if (codeScanStart >= 0)
                        inCodeBlock = true;
                }
            }

            if (inCodeBlock)
                MaskCSharpStringsAndCommentsInRazorCode(chars, Math.Max(0, codeScanStart), ref codeDepth, ref inCodeBlock, ref inCSharpBlockComment);
            else
            {
                var controlStart = IndexOfRazorControlDirective(chars);
                if (controlStart >= 0)
                {
                    var delta = MaskRazorControlCodeLine(chars, controlStart, ref inRazorControlBlockComment);
                    razorControlDepth = Math.Max(0, razorControlDepth + delta);
                    pendingRazorControlBlock = delta <= 0;
                }
                else if (IndexOfRazorBareControlContinuation(chars) is var continuationStart && continuationStart >= 0)
                {
                    var delta = MaskRazorControlCodeLine(chars, continuationStart, ref inRazorControlBlockComment);
                    razorControlDepth = Math.Max(0, razorControlDepth + delta);
                    pendingRazorControlBlock = delta <= 0;
                }
                else if (pendingRazorControlBlock && IsRazorCodeLineInsideControl(chars))
                {
                    var delta = MaskRazorControlCodeLine(chars, FirstNonWhitespaceIndex(chars), ref inRazorControlBlockComment);
                    razorControlDepth = Math.Max(0, razorControlDepth + delta);
                    if (delta != 0)
                        pendingRazorControlBlock = false;
                }
                else if (razorControlDepth > 0 && IsRazorCodeLineInsideControl(chars))
                {
                    razorControlDepth = Math.Max(
                        0,
                        razorControlDepth + MaskRazorControlCodeLine(chars, FirstNonWhitespaceIndex(chars), ref inRazorControlBlockComment));
                }
            }

            result[lineIndex] = new string(chars);
        }

        return result;
    }

    private static bool MayContainRazorMaskingConstruct(IReadOnlyList<string> originalLines)
    {
        foreach (var line in originalLines)
        {
            if (line.Contains('@') || line.Contains("<!--", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string[] ReuseOrCopyRazorLines(IReadOnlyList<string> originalLines)
    {
        if (originalLines is string[] lineArray)
            return lineArray;

        if (originalLines.Count == 0)
            return [];

        var result = new string[originalLines.Count];
        for (var i = 0; i < originalLines.Count; i++)
            result[i] = originalLines[i];

        return result;
    }

    private static int IndexOfRazorCodeDirective(char[] chars)
    {
        var line = new string(chars);
        foreach (var directive in RazorCodeDirectives)
        {
            var index = line.IndexOf(directive, StringComparison.Ordinal);
            if (index < 0)
                continue;
            var beforeOk = index == 0 || char.IsWhiteSpace(line[index - 1]);
            var afterIndex = index + directive.Length;
            var afterOk = afterIndex == line.Length || char.IsWhiteSpace(line[afterIndex]) || line[afterIndex] == '{';
            if (beforeOk && afterOk)
                return index;
        }

        return -1;
    }

    private static int IndexOfRazorBareControlContinuation(char[] chars)
    {
        var index = FirstNonWhitespaceIndex(chars);
        if (index < 0)
            return -1;

        var line = new string(chars);
        foreach (var keyword in RazorBareControlContinuationKeywords)
        {
            if (line.AsSpan(index).StartsWith(keyword, StringComparison.Ordinal)
                && (index + keyword.Length == line.Length || !IsSimpleIdentifierPart(line[index + keyword.Length])))
            {
                return index;
            }
        }

        return -1;
    }

    private static int IndexOfRazorExplicitCodeBlock(char[] chars)
    {
        var line = new string(chars);
        var index = line.IndexOf("@{", StringComparison.Ordinal);
        return index >= 0 ? index : -1;
    }

    private static int IndexOfRazorControlDirective(char[] chars)
    {
        for (var index = 0; index < chars.Length; index++)
        {
            if (chars[index] != '@')
                continue;

            var beforeOk = index == 0 || char.IsWhiteSpace(chars[index - 1]) || chars[index - 1] == '}';
            if (!beforeOk)
                continue;

            foreach (var directive in RazorControlDirectives)
            {
                if (!CharsStartWith(chars, index, directive))
                    continue;

                var afterIndex = index + directive.Length;
                var afterOk = afterIndex == chars.Length || !IsSimpleIdentifierPart(chars[afterIndex]);
                if (beforeOk && afterOk)
                    return index;
            }
        }

        return -1;
    }

    private static bool CharsStartWith(char[] chars, int index, string value)
    {
        if (index + value.Length > chars.Length)
            return false;

        for (var i = 0; i < value.Length; i++)
        {
            if (chars[index + i] != value[i])
                return false;
        }

        return true;
    }

    private static bool IsRazorCodeLineInsideControl(char[] chars)
    {
        var index = FirstNonWhitespaceIndex(chars);
        if (index < 0)
            return false;

        return !LooksLikeRazorMarkupStart(new string(chars), index);
    }

    private static int FirstNonWhitespaceIndex(char[] chars)
    {
        for (var i = 0; i < chars.Length; i++)
        {
            if (!char.IsWhiteSpace(chars[i]))
                return i;
        }

        return -1;
    }

    private static bool LooksLikeRazorMarkupStart(string line, int index)
    {
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;
        if (index >= line.Length)
            return false;
        if (line[index] == '<')
            return true;

        return line[index] == '@'
            && index + 1 < line.Length
            && line[index + 1] is ':' or '<';
    }

    private static int MaskRazorControlCodeLine(char[] chars, int start, ref bool inBlockComment)
    {
        var line = new string(chars);
        var firstOpenBrace = -1;
        var delta = CountCSharpBraceDelta(line, start, ref inBlockComment, ref firstOpenBrace);
        if (firstOpenBrace >= 0 && LooksLikeRazorMarkupStart(line, firstOpenBrace + 1))
        {
            MaskRange(chars, start, firstOpenBrace + 1);
            return delta;
        }

        MaskRange(chars, start, chars.Length);
        return delta;
    }

    private static int CountCSharpBraceDelta(string line, int start, ref bool inBlockComment, ref int firstOpenBrace)
    {
        var delta = 0;
        for (var cursor = Math.Max(0, start); cursor < line.Length; cursor++)
        {
            if (inBlockComment)
            {
                if (line[cursor] == '*' && cursor + 1 < line.Length && line[cursor + 1] == '/')
                {
                    inBlockComment = false;
                    cursor++;
                }

                continue;
            }

            if (line[cursor] == '/' && cursor + 1 < line.Length && line[cursor + 1] == '/')
                break;

            if (line[cursor] == '/' && cursor + 1 < line.Length && line[cursor + 1] == '*')
            {
                inBlockComment = true;
                cursor++;
                continue;
            }

            if (line[cursor] is '"' or '\'')
            {
                var quote = line[cursor++];
                while (cursor < line.Length)
                {
                    if (line[cursor] == '\\' && cursor + 1 < line.Length)
                    {
                        cursor += 2;
                        continue;
                    }

                    if (line[cursor] == quote)
                        break;
                    cursor++;
                }

                continue;
            }

            if (line[cursor] == '{')
            {
                if (firstOpenBrace < 0)
                    firstOpenBrace = cursor;
                delta++;
            }
            else if (line[cursor] == '}')
            {
                delta--;
            }
        }

        return delta;
    }

    private static void MaskCSharpStringsAndCommentsInRazorCode(
        char[] chars,
        int start,
        ref int codeDepth,
        ref bool inCodeBlock,
        ref bool inBlockComment)
    {
        for (var cursor = start; cursor < chars.Length; cursor++)
        {
            if (inBlockComment)
            {
                if (chars[cursor] == '*' && cursor + 1 < chars.Length && chars[cursor + 1] == '/')
                {
                    chars[cursor++] = ' ';
                    chars[cursor] = ' ';
                    inBlockComment = false;
                    continue;
                }

                chars[cursor] = ' ';
                continue;
            }

            if (chars[cursor] == '/' && cursor + 1 < chars.Length && chars[cursor + 1] == '/')
            {
                MaskRange(chars, cursor, chars.Length);
                break;
            }

            if (chars[cursor] == '/' && cursor + 1 < chars.Length && chars[cursor + 1] == '*')
            {
                chars[cursor++] = ' ';
                chars[cursor] = ' ';
                inBlockComment = true;
                continue;
            }

            if (chars[cursor] is '"' or '\'')
            {
                var quote = chars[cursor];
                chars[cursor++] = ' ';
                while (cursor < chars.Length)
                {
                    if (chars[cursor] == '\\' && cursor + 1 < chars.Length)
                    {
                        chars[cursor++] = ' ';
                        chars[cursor] = ' ';
                        cursor++;
                        continue;
                    }

                    var closes = chars[cursor] == quote;
                    chars[cursor++] = ' ';
                    if (closes)
                        break;
                }
                cursor--;
                continue;
            }

            if (chars[cursor] == '{')
            {
                codeDepth++;
                chars[cursor] = ' ';
                continue;
            }

            if (chars[cursor] == '}' && codeDepth > 0)
            {
                codeDepth--;
                chars[cursor] = ' ';
                if (codeDepth == 0)
                    inCodeBlock = false;
                continue;
            }

            chars[cursor] = ' ';
        }
    }

}
