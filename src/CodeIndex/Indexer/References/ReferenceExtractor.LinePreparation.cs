using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private readonly record struct ReferenceLinePrepareOptions(
        bool UseCSharpTriggerFastPath,
        bool MaskRustLifetimes,
        bool MaskStringLiterals,
        bool PreserveStringLiteralWidth,
        bool MaskNimRawStrings,
        bool IncludeBacktickStringDelimiter,
        bool PreserveStringLiteralLength,
        bool PreservePostfixSingleQuotes,
        bool UseMatlabStringRules,
        bool ScientificStringUsesBackslashEscapes,
        bool UsesHashComments,
        bool UsesRHashComments,
        bool UsesSlashComments,
        bool UsesDashDashComments,
        bool UsesPercentComments,
        bool UsesFortranBangComments,
        bool UsesPascalBlockComments,
        bool UsesVisualBasicComments);

    private static ReferenceLinePrepareOptions CreateReferenceLinePrepareOptions(string lang)
        => new(
            UseCSharpTriggerFastPath: lang == "csharp",
            MaskRustLifetimes: lang == "rust",
            MaskStringLiterals: lang != "cobol",
            PreserveStringLiteralWidth: lang is "crystal" or "groovy" or "prolog" or "ambiguous_pl",
            MaskNimRawStrings: lang == "nim",
            IncludeBacktickStringDelimiter: lang is not ("kotlin" or "r"),
            PreserveStringLiteralLength: ScientificNativeReferenceExtractor.Supports(lang),
            PreservePostfixSingleQuotes: lang is "ada" or "julia" or "matlab",
            UseMatlabStringRules: lang == "matlab",
            ScientificStringUsesBackslashEscapes: lang is "cython" or "d" or "julia" or "nim" or "objc",
            UsesHashComments: UsesHashComments(lang),
            UsesRHashComments: lang == "r",
            UsesSlashComments: UsesSlashComments(lang),
            UsesDashDashComments: UsesDashDashComments(lang),
            UsesPercentComments: lang == "matlab",
            UsesFortranBangComments: lang == "fortran",
            UsesPascalBlockComments: lang == "pascal",
            UsesVisualBasicComments: lang == "vb");

    private static string PrepareLine(string lang, string line)
        => PrepareLine(line, CreateReferenceLinePrepareOptions(lang));

    private static string PrepareLine(string line, ReferenceLinePrepareOptions options)
    {
        if (line.Length == 0)
            return line;

        if (options.UseCSharpTriggerFastPath && line.IndexOfAny(CSharpReferenceLinePreparationTriggerChars) < 0)
            return line;

        var result = line;
        if (options.MaskRustLifetimes)
            result = MaskRustLifetimeTokens(result);
        if (options.MaskNimRawStrings)
            result = ScientificNativeCommentMasker.MaskNimRawStringLiterals(result);
        if (options.MaskStringLiterals && MayContainStringLiteralDelimiter(result, options.IncludeBacktickStringDelimiter))
        {
            if (options.PreserveStringLiteralLength)
            {
                result = ScientificNativeCommentMasker.MaskLineStringLiteralsPreservingPostfixSingleQuotes(
                    result,
                    options.UseMatlabStringRules,
                    options.ScientificStringUsesBackslashEscapes,
                    options.PreservePostfixSingleQuotes);
            }
            else
            {
                var stringLiteralRegex = !options.IncludeBacktickStringDelimiter
                    ? NonBacktickStringLiteralRegex
                    : StringLiteralRegex;
                result = options.PreserveStringLiteralWidth
                    ? stringLiteralRegex.Replace(result, static match => new string(' ', match.Length))
                    : stringLiteralRegex.Replace(result, "\"\"");
            }
        }
        if (result.Contains("/*", StringComparison.Ordinal))
            result = InlineBlockCommentRegex.Replace(result, " ");

        if (options.UsesHashComments)
        {
            var hashIndex = options.UsesRHashComments
                ? FindRHashCommentStart(result)
                : result.IndexOf('#');
            if (hashIndex >= 0)
                result = result[..hashIndex];
        }

        if (options.UsesSlashComments)
        {
            var slashIndex = result.IndexOf("//", StringComparison.Ordinal);
            if (slashIndex >= 0)
                result = result[..slashIndex];
        }

        // Lua, SQL, Haskell use -- for line comments / Lua、SQL、Haskell は -- を行コメントに使う
        if (options.UsesDashDashComments)
        {
            var dashCommentIndex = result.IndexOf("--", StringComparison.Ordinal);
            if (dashCommentIndex >= 0)
                result = result[..dashCommentIndex];
        }

        if (options.UsesPercentComments)
        {
            // Outside strings, MATLAB treats `...` and the rest of the physical line as a
            // continuation comment. MATLAB では文字列外の `...` 以降は継続コメントになる。
            var continuationIndex = result.IndexOf("...", StringComparison.Ordinal);
            if (continuationIndex >= 0)
                result = result[..continuationIndex];

            var percentCommentIndex = result.IndexOf('%');
            if (percentCommentIndex >= 0)
                result = result[..percentCommentIndex];
        }

        if (options.UsesFortranBangComments)
        {
            var bangCommentIndex = result.IndexOf('!');
            if (bangCommentIndex >= 0)
                result = result[..bangCommentIndex];
        }

        if (options.UsesPascalBlockComments)
        {
            result = PascalBraceCommentRegex.Replace(result, " ");
            result = PascalParenStarCommentRegex.Replace(result, " ");
        }

        // VB.NET uses Rem and ' for line comments / VB.NET は Rem と ' を行コメントに使う
        if (options.UsesVisualBasicComments)
        {
            var remCommentMatch = VisualBasicRemCommentRegex.Match(result);
            if (remCommentMatch.Success)
                result = result[..remCommentMatch.Index];

            var vbCommentIndex = result.IndexOf('\'');
            if (vbCommentIndex >= 0)
                result = result[..vbCommentIndex];
        }

        return result;
    }

    private static bool MayContainStringLiteralDelimiter(string line, bool includeBacktick)
        => includeBacktick
            ? line.AsSpan().IndexOfAny('"', '\'', '`') >= 0
            : line.AsSpan().IndexOfAny('"', '\'') >= 0;

    private static int FindRHashCommentStart(string line)
    {
        var inBacktickIdentifier = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inBacktickIdentifier && ch == '\\' && i + 1 < line.Length)
            {
                i++;
                continue;
            }

            if (ch == '`')
            {
                inBacktickIdentifier = !inBacktickIdentifier;
                continue;
            }

            if (ch == '#' && !inBacktickIdentifier)
                return i;
        }

        return -1;
    }

    private static string MaskRustLifetimeTokens(string line)
    {
        var quoteIndex = line.IndexOf('\'');
        if (quoteIndex < 0)
            return line;

        char[]? chars = null;
        for (var index = quoteIndex; index + 1 < line.Length; index++)
        {
            if (line[index] != '\'')
                continue;

            var next = line[index + 1];
            if (next != '_' && !char.IsLetter(next))
                continue;

            var end = index + 2;
            while (end < line.Length && IsJavaIdentifierPart(line[end]))
                end++;

            if (end == index + 2 && end < line.Length && line[end] == '\'')
                continue;

            chars ??= line.ToCharArray();
            for (var maskIndex = index; maskIndex < end; maskIndex++)
                chars[maskIndex] = ' ';

            index = end - 1;
        }

        return chars is null ? line : new string(chars);
    }

    private static string[] MaskPascalBlockCommentLines(IReadOnlyList<string> lines)
    {
        if (lines is string[] lineArray && !MayContainPascalBlockComment(lines))
            return lineArray;

        var result = new string[lines.Count];
        var inBraceComment = false;
        var inParenStarComment = false;

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            char[]? chars = null;

            void MaskAt(int index) =>
                (chars ??= line.ToCharArray())[index] = ' ';

            var cursor = 0;

            while (cursor < line.Length)
            {
                if (inBraceComment)
                {
                    var closes = line[cursor] == '}';
                    MaskAt(cursor++);
                    if (closes)
                        inBraceComment = false;
                    continue;
                }

                if (inParenStarComment)
                {
                    if (line[cursor] == '*' && cursor + 1 < line.Length && line[cursor + 1] == ')')
                    {
                        MaskAt(cursor++);
                        MaskAt(cursor++);
                        inParenStarComment = false;
                        continue;
                    }

                    MaskAt(cursor++);
                    continue;
                }

                if (line[cursor] == '\'')
                {
                    cursor++;
                    while (cursor < line.Length)
                    {
                        if (line[cursor] == '\'')
                        {
                            cursor++;
                            if (cursor < line.Length && line[cursor] == '\'')
                            {
                                cursor++;
                                continue;
                            }
                            break;
                        }

                        cursor++;
                    }
                    continue;
                }

                if (line[cursor] == '{')
                {
                    MaskAt(cursor++);
                    inBraceComment = true;
                    continue;
                }

                if (line[cursor] == '(' && cursor + 1 < line.Length && line[cursor + 1] == '*')
                {
                    MaskAt(cursor++);
                    MaskAt(cursor++);
                    inParenStarComment = true;
                    continue;
                }

                cursor++;
            }

            result[lineIndex] = chars is null ? line : new string(chars);
        }

        return result;
    }

    private static bool MayContainPascalBlockComment(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            if (line.Contains('{') || line.Contains("(*", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool UsesCStyleBlockComments(string language) =>
        language is "c"
            or "cpp"
            or "cuda"
            or "glsl"
            or "hlsl"
            or "metal"
            or "wgsl"
            or "go"
            or "objc"
            or "dart";

    private static string[] MaskCStyleBlockCommentLines(string language, IReadOnlyList<string> lines)
    {
        if (lines is string[] lineArray && !MayContainCStyleMaskingTrigger(language, lines))
            return lineArray;

        var result = new string[lines.Count];
        var blockCommentDepth = 0;
        var inGoRawString = false;
        char dartTripleQuote = '\0';
        string? cppRawStringTerminator = null;

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            char[]? chars = null;

            void MaskAt(int index) =>
                (chars ??= line.ToCharArray())[index] = ' ';

            void MaskRange(int start, int endExclusive)
            {
                var masked = chars ??= line.ToCharArray();
                for (var index = start; index < endExclusive; index++)
                    masked[index] = ' ';
            }

            var cursor = 0;
            while (cursor < line.Length)
            {
                if (blockCommentDepth > 0)
                {
                    MaskAt(cursor);
                    if (language == "wgsl"
                        && line[cursor] == '/'
                        && cursor + 1 < line.Length
                        && line[cursor + 1] == '*')
                    {
                        MaskAt(cursor + 1);
                        blockCommentDepth++;
                        cursor += 2;
                        continue;
                    }

                    if (line[cursor] == '*' && cursor + 1 < line.Length && line[cursor + 1] == '/')
                    {
                        MaskAt(cursor + 1);
                        blockCommentDepth--;
                        cursor += 2;
                        continue;
                    }

                    cursor++;
                    continue;
                }

                if (inGoRawString)
                {
                    MaskAt(cursor);
                    if (line[cursor] == '`')
                        inGoRawString = false;
                    cursor++;
                    continue;
                }

                if (dartTripleQuote != '\0')
                {
                    if (IsTripleQuoteAt(line, cursor, dartTripleQuote))
                    {
                        MaskRange(cursor, cursor + 3);
                        dartTripleQuote = '\0';
                        cursor += 3;
                        continue;
                    }

                    MaskAt(cursor);
                    cursor++;
                    continue;
                }

                if (cppRawStringTerminator != null)
                {
                    var closeIndex = line.IndexOf(cppRawStringTerminator, cursor, StringComparison.Ordinal);
                    if (closeIndex < 0)
                    {
                        MaskRange(cursor, line.Length);
                        break;
                    }

                    MaskRange(cursor, closeIndex + cppRawStringTerminator.Length);
                    cursor = closeIndex + cppRawStringTerminator.Length;
                    cppRawStringTerminator = null;
                    continue;
                }

                if (line[cursor] == '/' && cursor + 1 < line.Length && line[cursor + 1] == '/')
                    break;

                if (language == "go" && line[cursor] == '`')
                {
                    MaskAt(cursor);
                    inGoRawString = true;
                    cursor++;
                    continue;
                }

                if (language == "dart" && TryGetDartTripleStringStart(line, cursor, out var dartQuote, out var dartOpeningLength))
                {
                    var closeIndex = IndexOfTripleQuote(line, cursor + dartOpeningLength, dartQuote);
                    if (closeIndex < 0)
                    {
                        MaskRange(cursor, line.Length);
                        dartTripleQuote = dartQuote;
                        break;
                    }

                    MaskRange(cursor, closeIndex + 3);
                    cursor = closeIndex + 3;
                    continue;
                }

                if (language == "cpp" && TryGetCppRawStringTerminator(line, cursor, out var rawTerminator, out var rawOpeningLength))
                {
                    var closeIndex = line.IndexOf(rawTerminator, cursor + rawOpeningLength, StringComparison.Ordinal);
                    if (closeIndex < 0)
                    {
                        MaskRange(cursor, line.Length);
                        cppRawStringTerminator = rawTerminator;
                        break;
                    }

                    MaskRange(cursor, closeIndex + rawTerminator.Length);
                    cursor = closeIndex + rawTerminator.Length;
                    continue;
                }

                if (line[cursor] is '"' or '\'' or '`')
                {
                    cursor = SkipCStyleQuotedLiteral(line, cursor) + 1;
                    continue;
                }

                if (line[cursor] == '/' && cursor + 1 < line.Length && line[cursor + 1] == '*')
                {
                    MaskAt(cursor);
                    cursor++;
                    MaskAt(cursor);
                    blockCommentDepth = 1;
                    cursor++;
                    continue;
                }

                cursor++;
            }

            result[lineIndex] = chars is null ? line : new string(chars);
        }

        return result;
    }

    private static bool MayContainCStyleMaskingTrigger(string language, IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            if (line.Contains('/'))
                return true;
            if (language == "go" && line.Contains('`'))
                return true;
            if (language == "dart" &&
                (line.Contains("\"\"\"", StringComparison.Ordinal) ||
                 line.Contains("'''", StringComparison.Ordinal)))
            {
                return true;
            }

            if (language == "cpp" && line.Contains("R\"", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool TryGetDartTripleStringStart(string line, int start, out char quote, out int openingLength)
    {
        quote = '\0';
        openingLength = 0;
        var quoteIndex = start;

        if (line[start] is 'r' or 'R')
        {
            if (start > 0 && IsIdentifierChar(line[start - 1]))
                return false;
            quoteIndex = start + 1;
        }

        if (quoteIndex + 2 >= line.Length)
            return false;

        quote = line[quoteIndex];
        if (quote is not ('"' or '\'') || !IsTripleQuoteAt(line, quoteIndex, quote))
            return false;

        openingLength = quoteIndex - start + 3;
        return true;
    }

    private static bool IsTripleQuoteAt(string line, int start, char quote) =>
        start + 2 < line.Length
        && line[start] == quote
        && line[start + 1] == quote
        && line[start + 2] == quote;

    private static int IndexOfTripleQuote(string line, int start, char quote)
    {
        for (var i = start; i + 2 < line.Length; i++)
        {
            if (IsTripleQuoteAt(line, i, quote))
                return i;
        }

        return -1;
    }

    private static bool TryGetCppRawStringTerminator(string line, int start, out string terminator, out int openingLength)
    {
        terminator = string.Empty;
        openingLength = 0;
        if (line[start] != 'R' || start + 2 >= line.Length || line[start + 1] != '"')
            return false;

        var delimiterStart = start + 2;
        var parenIndex = line.IndexOf('(', delimiterStart);
        if (parenIndex < 0)
            return false;

        for (var i = delimiterStart; i < parenIndex; i++)
        {
            if (char.IsWhiteSpace(line[i]) || line[i] is '(' or ')' or '\\')
                return false;
        }

        terminator = ")" + line[delimiterStart..parenIndex] + "\"";
        openingLength = parenIndex - start + 1;
        return true;
    }

    private static string[] MaskHaskellBlockCommentLines(IReadOnlyList<string> lines)
    {
        if (lines is string[] lineArray && !MayContainHaskellBlockComment(lines))
            return lineArray;

        var result = new string[lines.Count];
        var blockDepth = 0;

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            char[]? chars = null;

            void MaskAt(int index) =>
                (chars ??= line.ToCharArray())[index] = ' ';

            var cursor = 0;

            while (cursor < line.Length)
            {
                if (blockDepth > 0)
                {
                    if (line[cursor] == '{' && cursor + 1 < line.Length && line[cursor + 1] == '-')
                    {
                        MaskAt(cursor);
                        MaskAt(cursor + 1);
                        blockDepth++;
                        cursor += 2;
                        continue;
                    }

                    if (line[cursor] == '-' && cursor + 1 < line.Length && line[cursor + 1] == '}')
                    {
                        MaskAt(cursor);
                        MaskAt(cursor + 1);
                        blockDepth--;
                        cursor += 2;
                        continue;
                    }

                    MaskAt(cursor++);
                    continue;
                }

                if (line[cursor] == '"')
                {
                    cursor = SkipCStyleQuotedLiteral(line, cursor) + 1;
                    continue;
                }

                if (line[cursor] == '-' && cursor + 1 < line.Length && line[cursor + 1] == '-')
                    break;

                if (line[cursor] == '{' && cursor + 1 < line.Length && line[cursor + 1] == '-')
                {
                    MaskAt(cursor);
                    MaskAt(cursor + 1);
                    blockDepth = 1;
                    cursor += 2;
                    continue;
                }

                cursor++;
            }

            result[lineIndex] = chars is null ? line : new string(chars);
        }

        return result;
    }

    private static bool MayContainHaskellBlockComment(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            if (line.Contains("{-", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static int SkipCStyleQuotedLiteral(string line, int start)
    {
        var quote = line[start];
        var cursor = start + 1;
        while (cursor < line.Length)
        {
            if (quote != '`' && line[cursor] == '\\' && cursor + 1 < line.Length)
            {
                cursor += 2;
                continue;
            }

            if (line[cursor] == quote)
                return cursor;
            cursor++;
        }

        return line.Length;
    }

    private static readonly Regex VisualBasicRemCommentRegex = new(
        @"(?:^|:)\s*Rem\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PascalBraceCommentRegex = new(@"\{[^}\r\n]*\}", RegexOptions.Compiled);
    private static readonly Regex PascalParenStarCommentRegex = new(@"\(\*.*?\*\)", RegexOptions.Compiled);


}
