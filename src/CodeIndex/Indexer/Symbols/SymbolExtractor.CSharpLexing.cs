using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static CSharpLexedLine LexCSharpLine(string line, CSharpLexState state)
    {
        if (state.Mode == CSharpLexMode.Code
            && state.InterpolationReturnMode == CSharpLexMode.Code
            && state.InterpolationBraceDepth == 0
            && line.AsSpan().IndexOfAny('/', '"', '\'') < 0)
        {
            return new CSharpLexedLine(line, state);
        }

        var sanitized = new char[line.Length];
        var i = 0;

        while (i < line.Length)
        {
            var ch = line[i];
            var next = i + 1 < line.Length ? line[i + 1] : '\0';

            if (state.Mode == CSharpLexMode.BlockComment)
            {
                sanitized[i] = ' ';
                if (ch == '*' && next == '/')
                {
                    sanitized[i + 1] = ' ';
                    state = state with { Mode = CSharpLexMode.Code };
                    i += 2;
                    continue;
                }

                i++;
                continue;
            }

            if (state.Mode == CSharpLexMode.String)
            {
                sanitized[i] = ch is '"' or '\\' ? ch : ' ';

                if (state.EscapeNext)
                {
                    state = state with { EscapeNext = false };
                    i++;
                    continue;
                }

                if (ch == '\\')
                {
                    state = state with { EscapeNext = true };
                    i++;
                    continue;
                }

                if (state.IsInterpolated && ch == '{')
                {
                    if (next == '{')
                    {
                        sanitized[i + 1] = ' ';
                        i += 2;
                        continue;
                    }

                    sanitized[i] = ' ';
                    state = state with
                    {
                        Mode = CSharpLexMode.Code,
                        InterpolationReturnMode = CSharpLexMode.String,
                        InterpolationReturnRawDelimiterLength = 0,
                        InterpolationReturnDollarCount = state.InterpolationDollarCount,
                        InterpolationBraceDepth = 1,
                        IsInterpolated = false,
                        InterpolationDollarCount = 0,
                    };
                    i++;
                    continue;
                }

                if (state.IsInterpolated && ch == '}' && next == '}')
                {
                    sanitized[i + 1] = ' ';
                    i += 2;
                    continue;
                }

                if (ch == '"')
                    state = state with { Mode = CSharpLexMode.Code };

                i++;
                continue;
            }

            if (state.Mode == CSharpLexMode.Char)
            {
                sanitized[i] = ch is '\'' or '\\' ? ch : ' ';

                if (state.EscapeNext)
                {
                    state = state with { EscapeNext = false };
                    i++;
                    continue;
                }

                if (ch == '\\')
                {
                    state = state with { EscapeNext = true };
                    i++;
                    continue;
                }

                if (ch == '\'')
                    state = state with { Mode = CSharpLexMode.Code };

                i++;
                continue;
            }

            if (state.Mode == CSharpLexMode.VerbatimString)
            {
                sanitized[i] = ch == '"' ? '"' : ' ';

                // Interpolation hole handling for $@"..." / @$"...".
                // { opens a hole (unless {{, which is a literal {). Entering a hole
                // switches to Code mode so inner strings / brackets are parsed normally;
                // Return* fields preserve the outer verbatim-interp context.
                // 補間 verbatim 文字列（$@"..." / @$"..."）のホール処理。
                // { 単独でホール開始（{{ は literal {）。ホール進入時は Code モードへ切替。
                if (state.IsInterpolated && ch == '{')
                {
                    if (next == '{')
                    {
                        sanitized[i + 1] = ' ';
                        i += 2;
                        continue;
                    }

                    sanitized[i] = ' ';
                    state = state with
                    {
                        Mode = CSharpLexMode.Code,
                        InterpolationReturnMode = CSharpLexMode.VerbatimString,
                        InterpolationReturnRawDelimiterLength = 0,
                        InterpolationReturnDollarCount = state.InterpolationDollarCount,
                        InterpolationBraceDepth = 1,
                        IsInterpolated = false,
                        InterpolationDollarCount = 0,
                    };
                    i++;
                    continue;
                }

                if (state.IsInterpolated && ch == '}' && next == '}')
                {
                    sanitized[i + 1] = ' ';
                    i += 2;
                    continue;
                }

                if (ch == '"' && next == '"')
                {
                    sanitized[i + 1] = '"';
                    i += 2;
                    continue;
                }

                if (ch == '"')
                {
                    state = state with { Mode = CSharpLexMode.Code };
                    if (state.InterpolationReturnMode == CSharpLexMode.Code || state.InterpolationBraceDepth == 0)
                    {
                        state = state with
                        {
                            IsInterpolated = false,
                            InterpolationDollarCount = 0,
                        };
                    }
                }

                i++;
                continue;
            }

            if (state.Mode == CSharpLexMode.RawString)
            {
                sanitized[i] = ' ';

                // Interpolation hole handling for $"""..."""  (and multi-$ forms).
                // A run of N consecutive `{` where N = InterpolationDollarCount opens
                // a hole; fewer are literal string content. Closing mirrors this but
                // is handled in the Code-mode hole tracking below.
                // 補間 raw 文字列（$"""..."""  と $$"""..."""  など）のホール処理。
                // `{` 連続数 N が InterpolationDollarCount と一致したらホール開始。
                if (state.IsInterpolated && ch == '{')
                {
                    var openRun = 0;
                    while (i + openRun < line.Length && line[i + openRun] == '{')
                        openRun++;

                    var dollarCount = state.InterpolationDollarCount;
                    if (openRun >= dollarCount)
                    {
                        for (var j = 0; j < dollarCount && i + j < line.Length; j++)
                            sanitized[i + j] = ' ';

                        state = state with
                        {
                            Mode = CSharpLexMode.Code,
                            InterpolationReturnMode = CSharpLexMode.RawString,
                            InterpolationReturnRawDelimiterLength = state.RawDelimiterLength,
                            InterpolationReturnDollarCount = dollarCount,
                            InterpolationBraceDepth = 1,
                            IsInterpolated = false,
                            InterpolationDollarCount = 0,
                            RawDelimiterLength = 0,
                        };
                        i += dollarCount;
                        continue;
                    }

                    for (var j = 0; j < openRun && i + j < line.Length; j++)
                        sanitized[i + j] = ' ';
                    i += openRun;
                    continue;
                }

                if (ch == '"')
                {
                    var quoteRunLength = GetCSharpQuoteRunLength(line, i);
                    if (quoteRunLength != state.RawDelimiterLength)
                    {
                        for (var j = 0; j < quoteRunLength && i + j < line.Length; j++)
                            sanitized[i + j] = ' ';

                        i += quoteRunLength;
                        continue;
                    }

                    for (var j = 0; j < quoteRunLength && i + j < line.Length; j++)
                        sanitized[i + j] = ' ';

                    state = state with { Mode = CSharpLexMode.Code };
                    if (state.InterpolationReturnMode == CSharpLexMode.Code || state.InterpolationBraceDepth == 0)
                    {
                        state = state with
                        {
                            RawDelimiterLength = 0,
                            IsInterpolated = false,
                            InterpolationDollarCount = 0,
                        };
                    }
                    i += quoteRunLength;
                    continue;
                }

                i++;
                continue;
            }

            // Interpolation hole tracking. Only active when we are inside a hole of
            // an outer interpolated string (Mode = Code, InterpolationReturnMode set).
            // { increments depth; } decrements, and at depth 1 tries to close the hole
            // using the outer string's dollar count.
            // ホール内の括弧追跡。外側補間文字列のホール内（Mode=Code かつ Return* セット時）
            // のみ有効。{ で深さ++、} で --。深さ 1 で外側 dollar count を満たせば閉じる。
            if (state.Mode == CSharpLexMode.Code
                && state.InterpolationReturnMode != CSharpLexMode.Code
                && state.InterpolationBraceDepth > 0)
            {
                if (ch == '{')
                {
                    sanitized[i] = ch;
                    state = state with { InterpolationBraceDepth = state.InterpolationBraceDepth + 1 };
                    i++;
                    continue;
                }

                if (ch == '}')
                {
                    if (state.InterpolationBraceDepth > 1)
                    {
                        sanitized[i] = ch;
                        state = state with { InterpolationBraceDepth = state.InterpolationBraceDepth - 1 };
                        i++;
                        continue;
                    }

                    if (state.InterpolationReturnMode == CSharpLexMode.String)
                    {
                        sanitized[i] = ' ';
                        state = state with
                        {
                            Mode = CSharpLexMode.String,
                            IsInterpolated = true,
                            InterpolationDollarCount = state.InterpolationReturnDollarCount,
                            InterpolationBraceDepth = 0,
                            InterpolationReturnMode = CSharpLexMode.Code,
                            InterpolationReturnRawDelimiterLength = 0,
                            InterpolationReturnDollarCount = 0,
                        };
                        i++;
                        continue;
                    }

                    if (state.InterpolationReturnMode == CSharpLexMode.VerbatimString)
                    {
                        sanitized[i] = ' ';
                        state = state with
                        {
                            Mode = CSharpLexMode.VerbatimString,
                            IsInterpolated = true,
                            InterpolationDollarCount = state.InterpolationReturnDollarCount,
                            InterpolationBraceDepth = 0,
                            InterpolationReturnMode = CSharpLexMode.Code,
                            InterpolationReturnRawDelimiterLength = 0,
                            InterpolationReturnDollarCount = 0,
                        };
                        i++;
                        continue;
                    }

                    if (state.InterpolationReturnMode == CSharpLexMode.RawString)
                    {
                        var closeRun = 0;
                        while (i + closeRun < line.Length && line[i + closeRun] == '}')
                            closeRun++;

                        var dollarCount = state.InterpolationReturnDollarCount;
                        if (closeRun >= dollarCount)
                        {
                            for (var j = 0; j < dollarCount && i + j < line.Length; j++)
                                sanitized[i + j] = ' ';

                            state = state with
                            {
                                Mode = CSharpLexMode.RawString,
                                RawDelimiterLength = state.InterpolationReturnRawDelimiterLength,
                                IsInterpolated = true,
                                InterpolationDollarCount = dollarCount,
                                InterpolationBraceDepth = 0,
                                InterpolationReturnMode = CSharpLexMode.Code,
                                InterpolationReturnRawDelimiterLength = 0,
                                InterpolationReturnDollarCount = 0,
                            };
                            i += dollarCount;
                            continue;
                        }

                        // Not enough } — fall through to normal code handling.
                        // dollar count に満たない } — 通常の Code ハンドリングへ。
                    }
                }
            }

            if (ch == '/' && next == '/')
            {
                while (i < line.Length)
                {
                    sanitized[i] = ' ';
                    i++;
                }

                break;
            }

            if (ch == '/' && next == '*')
            {
                sanitized[i] = ' ';
                sanitized[i + 1] = ' ';
                state = state with { Mode = CSharpLexMode.BlockComment };
                i += 2;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                sanitized[i] = ch;
                i++;
                continue;
            }

            if (TryReadCSharpRawStringStart(line, i, out var rawPrefixLength, out var rawDelimiterLength))
            {
                for (var j = 0; j < rawPrefixLength + rawDelimiterLength && i + j < line.Length; j++)
                    sanitized[i + j] = ' ';

                state = state with
                {
                    Mode = CSharpLexMode.RawString,
                    RawDelimiterLength = rawDelimiterLength,
                    IsInterpolated = rawPrefixLength > 0,
                    InterpolationDollarCount = rawPrefixLength,
                };
                i += rawPrefixLength + rawDelimiterLength;
                continue;
            }

            if (ch == '@' && next == '"')
            {
                sanitized[i] = ' ';
                sanitized[i + 1] = '"';
                state = state with
                {
                    Mode = CSharpLexMode.VerbatimString,
                    IsInterpolated = false,
                    InterpolationDollarCount = 0,
                };
                i += 2;
                continue;
            }

            if (ch == '$' && next == '@' && i + 2 < line.Length && line[i + 2] == '"')
            {
                sanitized[i] = ' ';
                sanitized[i + 1] = ' ';
                sanitized[i + 2] = '"';
                state = state with
                {
                    Mode = CSharpLexMode.VerbatimString,
                    IsInterpolated = true,
                    InterpolationDollarCount = 1,
                };
                i += 3;
                continue;
            }

            if (ch == '@' && next == '$' && i + 2 < line.Length && line[i + 2] == '"')
            {
                sanitized[i] = ' ';
                sanitized[i + 1] = ' ';
                sanitized[i + 2] = '"';
                state = state with
                {
                    Mode = CSharpLexMode.VerbatimString,
                    IsInterpolated = true,
                    InterpolationDollarCount = 1,
                };
                i += 3;
                continue;
            }

            if (ch == '$' && next == '"')
            {
                sanitized[i] = ' ';
                sanitized[i + 1] = '"';
                state = state with
                {
                    Mode = CSharpLexMode.String,
                    IsInterpolated = true,
                    InterpolationBraceDepth = 0,
                    InterpolationDollarCount = 1,
                    InterpolationReturnMode = CSharpLexMode.Code,
                    InterpolationReturnRawDelimiterLength = 0,
                    InterpolationReturnDollarCount = 0
                };
                i += 2;
                continue;
            }

            if (ch == '"')
            {
                sanitized[i] = '"';
                state = state with { Mode = CSharpLexMode.String };
                i++;
                continue;
            }

            if (ch == '\'')
            {
                sanitized[i] = '\'';
                state = state with { Mode = CSharpLexMode.Char };
                i++;
                continue;
            }

            sanitized[i] = ch;
            i++;
        }

        return new CSharpLexedLine(new string(sanitized), state);
    }

    private static bool TryReadCSharpRawStringStart(string line, int index, out int prefixLength, out int delimiterLength)
    {
        prefixLength = 0;
        delimiterLength = 0;
        var probe = index;

        while (probe < line.Length && line[probe] == '$')
        {
            prefixLength++;
            probe++;
        }

        delimiterLength = GetCSharpQuoteRunLength(line, probe);
        return delimiterLength >= 3;
    }

    private static int GetCSharpQuoteRunLength(string line, int index)
    {
        var length = 0;
        while (index + length < line.Length && line[index + length] == '"')
            length++;

        return length;
    }


}
