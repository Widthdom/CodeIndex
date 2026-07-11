using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    internal static void AddTypeExpressionSegments(
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string expression,
        int expressionStartInLine,
        string context,
        int lineNumber,
        SymbolRecord? container,
        string language,
        IReadOnlySet<string>? ignoredSegments = null)
    {
        if (language == "typescript")
        {
            AddTypeScriptTypeExpressionSegments(
                references,
                seen,
                fileId,
                expression,
                expressionStartInLine,
                context,
                lineNumber,
                container,
                ignoredSegments);
            return;
        }

        for (int i = 0; i < expression.Length; i++)
        {
            char c = expression[i];
            if (language == "rust"
                && c == '\''
                && i + 1 < expression.Length
                && IsRustLifetimeStart(expression[i + 1]))
            {
                var lifetimeStart = i;
                i += 2;
                while (i < expression.Length && IsRustLifetimePart(expression[i]))
                    i++;

                var lifetime = expression.Substring(lifetimeStart, i - lifetimeStart);
                AddReference(references, seen, fileId, lifetime, expressionStartInLine + lifetimeStart, "lifetime_reference", context, lineNumber, container);
                i--;
                continue;
            }

            if (language == "rust"
                && c == 'r'
                && i + 2 < expression.Length
                && expression[i + 1] == '#'
                && IsJavaIdentifierStart(expression[i + 2]))
            {
                var rustSegmentStart = i;
                i += 2;
                var rawNameStart = i;
                i++;
                while (i < expression.Length && IsJavaIdentifierPart(expression[i]))
                    i++;

                var rustSegment = expression.Substring(rawNameStart, i - rawNameStart);
                if (i + 1 < expression.Length && expression[i] == ':' && expression[i + 1] == ':')
                {
                    i++;
                    continue;
                }

                AddReference(references, seen, fileId, rustSegment, expressionStartInLine + rustSegmentStart, "type_reference", context, lineNumber, container);
                i--;
                continue;
            }

            if (language is "java" or "kotlin" or "swift" && c == '@')
            {
                i = SkipJavaAnnotation(expression, i);
                continue;
            }

            if (language == "kotlin" && c == '`')
            {
                var closeIndex = expression.IndexOf('`', i + 1);
                if (closeIndex < 0)
                    continue;

                var backtickSegment = expression.Substring(i + 1, closeIndex - i - 1);
                AddTypeReferenceSegment(
                    references,
                    seen,
                    fileId,
                    backtickSegment,
                    expressionStartInLine + i,
                    context,
                    lineNumber,
                    container,
                    language,
                    ignoredSegments: ignoredSegments);
                i = closeIndex;
                continue;
            }

            if (language == "vb" && c == '[')
            {
                var closeIndex = expression.IndexOf(']', i + 1);
                if (closeIndex < 0)
                    continue;

                var escapedSegment = expression.Substring(i + 1, closeIndex - i - 1);
                AddTypeReferenceSegment(
                    references,
                    seen,
                    fileId,
                    escapedSegment,
                    expressionStartInLine + i,
                    context,
                    lineNumber,
                    container,
                    language,
                    ignoredSegments: ignoredSegments);
                i = closeIndex;
                continue;
            }

            if (!IsTypeExpressionIdentifierStart(language, c))
                continue;

            int segmentStart = i;
            if (language == "csharp" && expression[i] == '@')
                i++;
            while (i < expression.Length && IsTypeExpressionIdentifierPart(language, expression[i]))
                i++;

            if (language == "rust" && segmentStart > 0 && expression[segmentStart - 1] == '\'')
            {
                i--;
                continue;
            }

            var rawSegment = expression.Substring(segmentStart, i - segmentStart);
            var isEscapedCSharpIdentifier = language == "csharp" && rawSegment.Length > 0 && rawSegment[0] == '@';
            var segment = rawSegment;
            if (language == "csharp")
                segment = NormalizeCSharpIdentifier(rawSegment);

            if (language == "kotlin" && KotlinTypeProjectionModifierNames.Contains(segment))
            {
                i--;
                continue;
            }

            if (language == "swift" && IsSwiftTupleElementLabelSegment(expression, segmentStart, i))
            {
                i--;
                continue;
            }

            if (language == "swift" && IsSwiftMetatypeSuffixSegment(expression, segmentStart, segment))
            {
                i--;
                continue;
            }

            if (i + 1 < expression.Length && expression[i] == ':' && expression[i + 1] == ':')
            {
                i++;
                continue;
            }

            AddTypeReferenceSegment(references, seen, fileId, segment, expressionStartInLine + segmentStart, context, lineNumber, container, language, isEscapedCSharpIdentifier, ignoredSegments);
            i--;
        }
    }


}
