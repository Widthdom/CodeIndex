using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class CssReferenceExtractor
{
    private static bool ShouldSkipScssVariableReference(string preparedLine, int variableIndex)
    {
        var firstNonWhitespace = 0;
        while (firstNonWhitespace < preparedLine.Length && char.IsWhiteSpace(preparedLine[firstNonWhitespace]))
            firstNonWhitespace++;

        var lineTail = preparedLine.AsSpan(firstNonWhitespace);
        if (lineTail.StartsWith("$", StringComparison.Ordinal))
        {
            var declarationColonIndex = preparedLine.IndexOf(':', variableIndex);
            if (declarationColonIndex >= 0)
                return true;
        }

        if (lineTail.StartsWith("@mixin", StringComparison.Ordinal)
            || lineTail.StartsWith("@function", StringComparison.Ordinal))
        {
            var braceIndex = preparedLine.IndexOf('{');
            if (braceIndex < 0)
                return true;
            if (variableIndex < braceIndex)
                return true;
        }

        return false;
    }

    private static bool ShouldSkipSassIndentedDeclarationReferences(string preparedLine)
    {
        var firstNonWhitespace = 0;
        while (firstNonWhitespace < preparedLine.Length && char.IsWhiteSpace(preparedLine[firstNonWhitespace]))
            firstNonWhitespace++;

        return firstNonWhitespace < preparedLine.Length && preparedLine[firstNonWhitespace] == '=';
    }

    private static bool ShouldSkipSassBareFunctionReference(string preparedLine, int functionIndex)
    {
        var firstNonWhitespace = 0;
        while (firstNonWhitespace < preparedLine.Length && char.IsWhiteSpace(preparedLine[firstNonWhitespace]))
            firstNonWhitespace++;

        var lineTail = preparedLine.AsSpan(firstNonWhitespace);
        if (!lineTail.StartsWith("@function", StringComparison.Ordinal)
            && !lineTail.StartsWith("@mixin", StringComparison.Ordinal))
        {
            return false;
        }

        return functionIndex >= firstNonWhitespace;
    }

    private static string PrepareSassStylusReferenceLine(string originalLine)
    {
        char[]? chars = null;

        void MaskAt(int index) =>
            (chars ??= originalLine.ToCharArray())[index] = ' ';

        void MaskRange(int start, int endExclusive)
        {
            var masked = chars ??= originalLine.ToCharArray();
            for (var index = start; index < endExclusive; index++)
                masked[index] = ' ';
        }

        char quote = '\0';
        var parenDepth = 0;
        for (var i = 0; i < originalLine.Length; i++)
        {
            var ch = originalLine[i];
            if (quote != '\0')
            {
                MaskAt(i);
                if (ch == quote && (i == 0 || originalLine[i - 1] != '\\'))
                    quote = '\0';
                continue;
            }

            if (ch is '\'' or '"')
            {
                MaskAt(i);
                quote = ch;
                continue;
            }

            if (ch == '/' && i + 1 < originalLine.Length && originalLine[i + 1] == '*')
            {
                var commentEnd = originalLine.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var stop = commentEnd >= 0 ? commentEnd + 2 : originalLine.Length;
                MaskRange(i, stop);
                i = stop - 1;
                continue;
            }

            if (ch == '(')
            {
                parenDepth++;
                continue;
            }

            if (ch == ')' && parenDepth > 0)
            {
                parenDepth--;
                continue;
            }

            if (parenDepth == 0 && ch == '/' && i + 1 < originalLine.Length && originalLine[i + 1] == '/')
            {
                MaskRange(i, originalLine.Length);
                break;
            }
        }

        return chars is null ? originalLine : new string(chars);
    }

    private static bool ShouldSkipStylusVariableReference(string preparedLine, int variableIndex)
    {
        var firstNonWhitespace = 0;
        while (firstNonWhitespace < preparedLine.Length && char.IsWhiteSpace(preparedLine[firstNonWhitespace]))
            firstNonWhitespace++;

        var dollarIndex = variableIndex - 1;
        if (dollarIndex != firstNonWhitespace || dollarIndex < 0 || preparedLine[dollarIndex] != '$')
            return false;

        var cursor = variableIndex;
        while (cursor < preparedLine.Length && (char.IsLetterOrDigit(preparedLine[cursor]) || preparedLine[cursor] is '_' or '-'))
            cursor++;
        while (cursor < preparedLine.Length && char.IsWhiteSpace(preparedLine[cursor]))
            cursor++;

        return cursor < preparedLine.Length
            && (preparedLine[cursor] == '='
                || (preparedLine[cursor] == ':' && cursor + 1 < preparedLine.Length && preparedLine[cursor + 1] == '='));
    }

    private static bool ShouldSkipStylusBareVariableReference(string preparedLine, int variableIndex)
    {
        var firstNonWhitespace = 0;
        while (firstNonWhitespace < preparedLine.Length && char.IsWhiteSpace(preparedLine[firstNonWhitespace]))
            firstNonWhitespace++;
        if (variableIndex == firstNonWhitespace)
            return true;

        var cursor = variableIndex;
        while (cursor < preparedLine.Length && (char.IsLetterOrDigit(preparedLine[cursor]) || preparedLine[cursor] is '_' or '-'))
            cursor++;
        while (cursor < preparedLine.Length && char.IsWhiteSpace(preparedLine[cursor]))
            cursor++;

        if (cursor < preparedLine.Length && preparedLine[cursor] == '(')
            return true;
        return cursor < preparedLine.Length
            && (preparedLine[cursor] == '='
                || (preparedLine[cursor] == ':' && cursor + 1 < preparedLine.Length && preparedLine[cursor + 1] == '='));
    }
}
