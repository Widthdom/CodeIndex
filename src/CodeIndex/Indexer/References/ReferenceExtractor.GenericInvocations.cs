using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static bool IsIgnoredCallName(string language, string name)
    {
        if (LanguageSpecificCallNameKeeps.TryGetValue(language, out var languageSpecificKeepNames)
            && languageSpecificKeepNames.Contains(name))
        {
            return false;
        }

        if (language == "php")
        {
            if (SharedIgnoredCallNamesCaseInsensitive.Contains(name))
                return true;
        }
        else if (SharedIgnoredCallNames.Contains(name))
        {
            return true;
        }

        return LanguageSpecificIgnoredCallNames.TryGetValue(language, out var languageSpecificIgnoredNames)
            && languageSpecificIgnoredNames.Contains(name);
    }

    private static bool IsConstructorCallName(string language, string preparedLine, int nameIndex)
    {
        var probe = nameIndex - 1;
        while (probe >= 0 && char.IsWhiteSpace(preparedLine[probe]))
            probe--;

        if (probe < 0)
            return false;

        while (probe >= 0)
        {
            char? separator = null;
            if (probe >= 1 && preparedLine[probe] == ':' && preparedLine[probe - 1] == ':')
            {
                separator = ':';
                probe -= 2;
            }
            else if (preparedLine[probe] is '.' or '\\')
            {
                separator = preparedLine[probe];
                probe--;
            }

            if (separator == null)
                break;

            if (separator != '\\')
            {
                while (probe >= 0 && char.IsWhiteSpace(preparedLine[probe]))
                    probe--;
            }

            var segmentEnd = probe;
            while (probe >= 0 && IsIdentifierChar(preparedLine[probe]))
                probe--;

            var consumedSegment = segmentEnd >= 0 && segmentEnd >= probe + 1;
            if (!consumedSegment && separator != '\\')
                return false;
        }

        while (probe >= 0 && char.IsWhiteSpace(preparedLine[probe]))
            probe--;

        if (probe < 0)
            return false;

        var tokenEnd = probe;
        while (probe >= 0 && IsIdentifierChar(preparedLine[probe]))
            probe--;

        var tokenStart = probe + 1;
        if (tokenStart > tokenEnd)
            return false;

        var token = preparedLine[tokenStart..(tokenEnd + 1)];
        return language == "php"
            ? string.Equals(token, "new", StringComparison.OrdinalIgnoreCase)
            : string.Equals(token, "new", StringComparison.Ordinal);
    }

    private static readonly HashSet<string> KotlinTypeProjectionModifierNames = new(StringComparer.Ordinal)
    {
        "in", "out",
    };

    private readonly record struct NestedGenericCallCandidate(string Name, int NameIndex);

    private static void EmitGenericInvocationTypeArgumentReferences(
        string language,
        string preparedLine,
        int nameIndex,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (language is not ("csharp" or "java" or "kotlin"))
            return;

        if (!TryGetPostNameGenericInvocationTypeArgumentSpan(preparedLine, nameIndex, out var argumentsStart, out var argumentsLength)
            && (language != "java"
                || !TryGetJavaExplicitGenericInvocationTypeArgumentSpan(preparedLine, nameIndex, out argumentsStart, out argumentsLength)))
        {
            return;
        }

        if (argumentsLength <= 0)
            return;

        var ignoredSegments = language == "kotlin"
            ? KotlinTypeProjectionModifierNames
            : null;
        var argumentsExpression = preparedLine.Substring(argumentsStart, argumentsLength);

        AddTypeExpressionSegments(
            references,
            seen,
            fileId,
            argumentsExpression,
            argumentsStart,
            context,
            lineNumber,
            container,
            language,
            ignoredSegments);
        AddGenericInvocationTypeArgumentSegments(
            references,
            seen,
            fileId,
            argumentsExpression,
            argumentsStart,
            context,
            lineNumber,
            container,
            language,
            ignoredSegments);
    }

    private static void AddGenericInvocationTypeArgumentSegments(
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string expression,
        int expressionStartInLine,
        string context,
        int lineNumber,
        SymbolRecord? container,
        string language,
        IReadOnlySet<string>? ignoredSegments)
    {
        if (language != "csharp")
            return;

        for (var i = 0; i < expression.Length; i++)
        {
            if (!IsTypeExpressionIdentifierStart(language, expression[i]))
                continue;

            var segmentStart = i;
            if (expression[i] == '@')
                i++;
            while (i < expression.Length && IsTypeExpressionIdentifierPart(language, expression[i]))
                i++;

            var segmentLength = i - segmentStart;
            var isEscapedCSharpIdentifier = segmentLength > 0 && expression[segmentStart] == '@';
            var segment = isEscapedCSharpIdentifier
                ? expression.Substring(segmentStart + 1, segmentLength - 1)
                : expression.Substring(segmentStart, segmentLength);
            if (i + 1 < expression.Length && expression[i] == ':' && expression[i + 1] == ':')
            {
                i++;
                continue;
            }

            AddTypeReferenceSegment(
                references,
                seen,
                fileId,
                segment,
                expressionStartInLine + segmentStart,
                context,
                lineNumber,
                container,
                language,
                isEscapedCSharpIdentifier,
                ignoredSegments,
                "generic_type_argument");
            i--;
        }
    }

    private static bool TryGetPostNameGenericInvocationTypeArgumentSpan(
        string preparedLine,
        int nameIndex,
        out int argumentsStart,
        out int argumentsLength)
    {
        argumentsStart = -1;
        argumentsLength = 0;

        if (nameIndex < 0 || nameIndex >= preparedLine.Length || !IsAtAwareAsciiIdentifierStart(preparedLine, nameIndex))
            return false;

        var scan = ConsumeAtAwareAsciiIdentifier(preparedLine, nameIndex);
        if (scan + 1 < preparedLine.Length
            && preparedLine[scan] == '?'
            && preparedLine[scan + 1] == '.')
        {
            scan += 2;
        }

        if (scan >= preparedLine.Length || preparedLine[scan] != '<')
            return false;

        var closeAngle = FindMatchingChar(preparedLine, scan, '<', '>');
        if (closeAngle <= scan)
            return false;

        var after = closeAngle + 1;
        while (after < preparedLine.Length && char.IsWhiteSpace(preparedLine[after]))
            after++;

        if (after >= preparedLine.Length || preparedLine[after] != '(')
            return false;

        argumentsStart = scan + 1;
        argumentsLength = closeAngle - scan - 1;
        return true;
    }

    private static bool TryGetJavaExplicitGenericInvocationTypeArgumentSpan(
        string preparedLine,
        int nameIndex,
        out int argumentsStart,
        out int argumentsLength)
    {
        argumentsStart = -1;
        argumentsLength = 0;

        var closeAngle = nameIndex - 1;
        while (closeAngle >= 0 && char.IsWhiteSpace(preparedLine[closeAngle]))
            closeAngle--;

        if (closeAngle < 0 || preparedLine[closeAngle] != '>')
            return false;

        var openAngle = FindMatchingOpenChar(preparedLine, closeAngle, '<', '>');
        if (openAngle < 0)
            return false;

        var beforeOpen = openAngle - 1;
        while (beforeOpen >= 0 && char.IsWhiteSpace(preparedLine[beforeOpen]))
            beforeOpen--;

        if (beforeOpen < 0 || preparedLine[beforeOpen] != '.')
            return false;

        argumentsStart = openAngle + 1;
        argumentsLength = closeAngle - openAngle - 1;
        return true;
    }

    private static int FindMatchingOpenChar(string text, int closeIndex, char openChar, char closeChar)
    {
        if (closeIndex < 0 || closeIndex >= text.Length || text[closeIndex] != closeChar)
            return -1;

        var depth = 0;
        for (var i = closeIndex; i >= 0; i--)
        {
            if (text[i] == closeChar)
            {
                depth++;
                continue;
            }

            if (text[i] != openChar)
                continue;

            depth--;
            if (depth == 0)
                return i;
        }

        return -1;
    }

    private static IEnumerable<NestedGenericCallCandidate> EnumerateNestedGenericCallCandidates(
        string preparedLine,
        HashSet<int> matchedCallIndices)
    {
        for (var i = 0; i < preparedLine.Length; i++)
        {
            if (!IsAtAwareAsciiIdentifierStart(preparedLine, i))
                continue;
            if (i > 0 && (IsIdentifierChar(preparedLine[i - 1]) || preparedLine[i - 1] == '$' || preparedLine[i - 1] == '@'))
                continue;

            var nameStart = i;
            i = ConsumeAtAwareAsciiIdentifier(preparedLine, i);

            if (matchedCallIndices.Contains(nameStart))
            {
                i--;
                continue;
            }

            var scan = i;
            if (scan + 1 < preparedLine.Length
                && preparedLine[scan] == '?'
                && preparedLine[scan + 1] == '.')
            {
                scan += 2;
            }

            if (scan >= preparedLine.Length || preparedLine[scan] != '<')
            {
                i--;
                continue;
            }

            if (!TrySkipBalancedGenericArgs(preparedLine, ref scan, out var sawNestedGeneric) || !sawNestedGeneric)
            {
                i--;
                continue;
            }

            while (scan < preparedLine.Length && char.IsWhiteSpace(preparedLine[scan]))
                scan++;

            if (scan < preparedLine.Length && preparedLine[scan] == '(')
                yield return new NestedGenericCallCandidate(preparedLine[nameStart..i], nameStart);

            i--;
        }
    }

    private static IEnumerable<NestedGenericCallCandidate> EnumerateNestedGenericInitializerCandidates(
        string preparedLine,
        HashSet<int> matchedInitializerIndices,
        bool requireOpeningBrace)
    {
        for (var i = 0; i < preparedLine.Length; i++)
        {
            if (!IsStandaloneNewKeyword(preparedLine, i))
                continue;

            var scan = i + 3;
            if (!TryReadQualifiedTypeName(preparedLine, ref scan, out var name, out var nameIndex))
            {
                i += 2;
                continue;
            }

            if (matchedInitializerIndices.Contains(nameIndex))
            {
                i = scan - 1;
                continue;
            }

            if (!TrySkipBalancedGenericArgs(preparedLine, ref scan, out var sawNestedGeneric) || !sawNestedGeneric)
            {
                i = scan - 1;
                continue;
            }

            if (!TrySkipArraySuffixes(preparedLine, ref scan))
            {
                i = scan - 1;
                continue;
            }

            while (scan < preparedLine.Length && char.IsWhiteSpace(preparedLine[scan]))
                scan++;

            if (requireOpeningBrace)
            {
                if (scan < preparedLine.Length && preparedLine[scan] == '{')
                    yield return new NestedGenericCallCandidate(name, nameIndex);
            }
            else if (scan == preparedLine.Length)
            {
                yield return new NestedGenericCallCandidate(name, nameIndex);
            }

            i = scan - 1;
        }
    }

    private static bool TryReadQualifiedTypeName(
        string preparedLine,
        ref int scan,
        out string name,
        out int nameIndex)
    {
        name = string.Empty;
        nameIndex = -1;

        while (true)
        {
            while (scan < preparedLine.Length && char.IsWhiteSpace(preparedLine[scan]))
                scan++;

            if (scan >= preparedLine.Length || !IsAtAwareAsciiIdentifierStart(preparedLine, scan))
                return false;

            var segmentStart = scan;
            scan = ConsumeAtAwareAsciiIdentifier(preparedLine, scan);

            name = preparedLine[segmentStart..scan];
            nameIndex = segmentStart;

            var separatorScan = scan;
            while (separatorScan < preparedLine.Length && char.IsWhiteSpace(preparedLine[separatorScan]))
                separatorScan++;

            if (separatorScan + 1 < preparedLine.Length
                && preparedLine[separatorScan] == ':'
                && preparedLine[separatorScan + 1] == ':')
            {
                scan = separatorScan + 2;
                continue;
            }

            if (separatorScan < preparedLine.Length && preparedLine[separatorScan] == '.')
            {
                scan = separatorScan + 1;
                continue;
            }

            scan = separatorScan;
            return true;
        }
    }

    private static bool TrySkipArraySuffixes(string preparedLine, ref int scan)
    {
        while (true)
        {
            while (scan < preparedLine.Length && char.IsWhiteSpace(preparedLine[scan]))
                scan++;

            if (scan >= preparedLine.Length || preparedLine[scan] != '[')
                return true;

            scan++;
            while (scan < preparedLine.Length && preparedLine[scan] != ']')
                scan++;

            if (scan >= preparedLine.Length || preparedLine[scan] != ']')
                return false;

            scan++;
        }
    }

    private static bool ShouldSkipInitializerName(string language, string name) =>
        (language == "csharp" && CSharpBuiltInTypeNames.Contains(name))
        || (language == "java" && JavaPrimitiveTypeNames.Contains(name))
        || IsIgnoredCallName(language, name);

    private static bool IsStandaloneNewKeyword(string preparedLine, int index)
    {
        if (index < 0 || index + 3 > preparedLine.Length)
            return false;
        if (preparedLine[index] != 'n'
            || preparedLine[index + 1] != 'e'
            || preparedLine[index + 2] != 'w')
        {
            return false;
        }

        if (index > 0 && IsIdentifierChar(preparedLine[index - 1]))
            return false;

        return index + 3 >= preparedLine.Length || !IsIdentifierChar(preparedLine[index + 3]);
    }

    private static bool TrySkipBalancedGenericArgs(string preparedLine, ref int scan, out bool sawNestedGeneric)
    {
        sawNestedGeneric = false;
        if (scan >= preparedLine.Length || preparedLine[scan] != '<')
            return false;

        var depth = 0;
        while (scan < preparedLine.Length)
        {
            var ch = preparedLine[scan++];
            if (ch == '<')
            {
                depth++;
                if (depth > 1)
                    sawNestedGeneric = true;
            }
            else if (ch == '>')
            {
                depth--;
                if (depth == 0)
                    return true;
                if (depth < 0)
                    return false;
            }
        }

        return false;
    }

    private static bool IsAsciiIdentifierStartChar(char ch) =>
        ch == '_' || (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z');

    private static bool IsAtAwareAsciiIdentifierStart(string text, int index)
    {
        if (index < 0 || index >= text.Length)
            return false;

        if (text[index] == '@')
            return index + 1 < text.Length && IsAsciiIdentifierStartChar(text[index + 1]);

        return IsAsciiIdentifierStartChar(text[index]);
    }

    private static int ConsumeAtAwareAsciiIdentifier(string text, int startIndex)
    {
        var index = startIndex;
        if (index < text.Length && text[index] == '@')
            index++;

        if (index >= text.Length || !IsAsciiIdentifierStartChar(text[index]))
            return startIndex;

        index++;
        while (index < text.Length && IsIdentifierChar(text[index]))
            index++;

        return index;
    }

    private static bool IsIdentifierChar(char ch) =>
        char.IsLetterOrDigit(ch) || ch == '_';

    /// <summary>
    /// Classify a call-looking identifier as an attribute/annotation when it appears inside
    /// a C# `[...]` attribute list or is preceded by a Java-family `@` marker. Returns null
    /// for ordinary method calls so the caller emits the default `call` reference kind.
    /// 呼び出しに見える識別子を、C# の `[...]` 属性リスト内や Java 系 `@` 付き注釈に該当する
    /// 場合に専用の reference kind へ分類する。通常の呼び出しは null を返して既定の `call` を維持する。
    /// </summary>
}
