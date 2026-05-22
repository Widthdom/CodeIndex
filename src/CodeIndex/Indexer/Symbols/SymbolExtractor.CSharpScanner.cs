using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    // THREAD-SAFETY: The C# scanner keeps all scan state in parameters, locals, or caller-owned
    // collections. It may read shared Regex fields from SymbolExtractor, but must not add static
    // mutable scanner state.
    private static void ExtractCSharpEnumMembers(long fileId, string[] rawLines, string[] enumScannerLines, string[] csharpMatchLines, List<SymbolRecord> symbols)
    {
        var enumDeclarations = symbols
            .Where(s => s.Kind == "enum" && s.BodyStartLine != null && s.BodyEndLine != null)
            .OrderBy(s => s.StartLine)
            .ThenByDescending(s => s.EndLine)
            .ToList();

        foreach (var enumSymbol in enumDeclarations)
        {
            if (!TryFindCSharpEnumBodyBounds(rawLines, csharpMatchLines, enumSymbol, out var bodyStartLineIndex, out var bodyStartColumn, out var bodyEndLineIndex, out var bodyEndColumnExclusive))
                continue;

            ExtractCSharpEnumMembersFromBody(
                fileId,
                enumSymbol,
                rawLines,
                enumScannerLines,
                bodyStartLineIndex,
                bodyStartColumn,
                bodyEndLineIndex,
                bodyEndColumnExclusive,
                symbols);
        }
    }

    private static bool TryFindCSharpEnumBodyBounds(
        string[] rawLines,
        string[] csharpMatchLines,
        SymbolRecord enumSymbol,
        out int bodyStartLineIndex,
        out int bodyStartColumn,
        out int bodyEndLineIndex,
        out int bodyEndColumnExclusive)
    {
        bodyStartLineIndex = 0;
        bodyStartColumn = 0;
        bodyEndLineIndex = 0;
        bodyEndColumnExclusive = 0;

        var declarationLineIndex = enumSymbol.StartLine - 1;
        if (declarationLineIndex < 0 || declarationLineIndex >= csharpMatchLines.Length)
            return false;

        var declarationLine = csharpMatchLines[declarationLineIndex];
        var declarationStartColumn = FindCSharpDeclarationStartColumn(rawLines[declarationLineIndex], enumSymbol.Signature);
        if (declarationStartColumn < 0 || declarationStartColumn >= declarationLine.Length)
            declarationStartColumn = 0;

        var declarationMatch = CSharpEnumDeclarationRegex.Match(declarationLine[declarationStartColumn..]);
        if (!declarationMatch.Success)
            return false;

        var depth = 0;
        var opened = false;
        var scanEndLineIndex = Math.Min(enumSymbol.EndLine, csharpMatchLines.Length) - 1;
        for (int lineIndex = declarationLineIndex; lineIndex <= scanEndLineIndex; lineIndex++)
        {
            var line = csharpMatchLines[lineIndex];
            var scanStartColumn = lineIndex == declarationLineIndex
                ? declarationStartColumn + declarationMatch.Index
                : 0;

            for (int column = scanStartColumn; column < line.Length; column++)
            {
                var ch = line[column];
                if (ch == '{')
                {
                    depth++;
                    if (!opened)
                    {
                        opened = true;
                        bodyStartLineIndex = lineIndex;
                        bodyStartColumn = column + 1;
                    }
                }
                else if (ch == '}' && opened)
                {
                    depth--;
                    if (depth == 0)
                    {
                        bodyEndLineIndex = lineIndex;
                        bodyEndColumnExclusive = column;
                        return true;
                    }
                }
            }
        }

        if (!opened)
            return false;

        bodyEndLineIndex = scanEndLineIndex;
        bodyEndColumnExclusive = csharpMatchLines[scanEndLineIndex].Length;
        return true;
    }

    private static void ExtractCSharpEnumMembersFromBody(
        long fileId,
        SymbolRecord enumSymbol,
        string[] rawLines,
        string[] enumScannerLines,
        int bodyStartLineIndex,
        int bodyStartColumn,
        int bodyEndLineIndex,
        int bodyEndColumnExclusive,
        List<SymbolRecord> symbols)
    {
        (int LineIndex, int Column)? currentStart = null;
        var parenDepth = 0;
        var bracketDepth = 0;
        var lineIndex = bodyStartLineIndex;
        var column = bodyStartColumn;

        while (lineIndex <= bodyEndLineIndex)
        {
            var maskedLine = enumScannerLines[lineIndex];
            var lineScanStartColumn = lineIndex == bodyStartLineIndex
                ? Math.Min(bodyStartColumn, maskedLine.Length)
                : 0;
            var scanEndColumnExclusive = lineIndex == bodyEndLineIndex
                ? Math.Min(bodyEndColumnExclusive, maskedLine.Length)
                : maskedLine.Length;

            if (column >= scanEndColumnExclusive)
            {
                lineIndex++;
                column = 0;
                continue;
            }

            if (TryGetFirstNonWhitespaceColumn(maskedLine, lineScanStartColumn, scanEndColumnExclusive, out var firstNonWhitespaceColumn)
                && column == firstNonWhitespaceColumn
                && (maskedLine[column] == '#'
                    || IsCSharpXmlDocCommentLine(rawLines[lineIndex], firstNonWhitespaceColumn)))
            {
                currentStart = null;
                parenDepth = 0;
                bracketDepth = 0;
                lineIndex++;
                column = 0;
                continue;
            }

            var ch = maskedLine[column];
            if (currentStart == null)
            {
                if (char.IsWhiteSpace(ch) || ch == ',')
                {
                    column++;
                    continue;
                }

                if (ch == '['
                    && TrySkipLeadingCSharpAttributeListsInEnumBody(
                        enumScannerLines,
                        lineIndex,
                        column,
                        bodyEndLineIndex,
                        bodyEndColumnExclusive,
                        out var nextPosition))
                {
                    lineIndex = nextPosition.LineIndex;
                    column = nextPosition.Column;
                    continue;
                }

                if (ch == '['
                    && TryRecoverBrokenCSharpEnumAttributeInBody(
                        enumScannerLines,
                        lineIndex,
                        bodyEndLineIndex,
                        bodyEndColumnExclusive,
                        out var recoveredPosition))
                {
                    lineIndex = recoveredPosition.LineIndex;
                    column = recoveredPosition.Column;
                    continue;
                }

                currentStart = (lineIndex, column);
            }

            if (ch == '(')
            {
                parenDepth++;
            }
            else if (ch == ')' && parenDepth > 0)
            {
                parenDepth--;
            }
            else if (ch == '[')
            {
                bracketDepth++;
            }
            else if (ch == ']' && bracketDepth > 0)
            {
                bracketDepth--;
            }
            else if (ch == ',' && parenDepth == 0 && bracketDepth == 0 && currentStart != null)
            {
                TryAddCSharpEnumMemberFromSpan(fileId, enumSymbol, rawLines, enumScannerLines, currentStart.Value, (lineIndex, column + 1), symbols);
                currentStart = null;
            }

            column++;
        }

        if (currentStart != null)
            TryAddCSharpEnumMemberFromSpan(fileId, enumSymbol, rawLines, enumScannerLines, currentStart.Value, (bodyEndLineIndex, bodyEndColumnExclusive), symbols);
    }

    private static bool IsCSharpXmlDocCommentLine(string rawLine, int firstNonWhitespaceColumn)
    {
        return firstNonWhitespaceColumn + 2 < rawLine.Length
            && rawLine[firstNonWhitespaceColumn] == '/'
            && rawLine[firstNonWhitespaceColumn + 1] == '/'
            && rawLine[firstNonWhitespaceColumn + 2] == '/';
    }

    internal static string[] SanitizeCSharpLinesForCrossLineScan(string[] lines)
    {
        if (lines.Length == 0)
            return lines;

        var result = new string[lines.Length];
        var state = new CSharpLexState();
        for (var i = 0; i < lines.Length; i++)
        {
            var lexed = LexCSharpLine(lines[i], state);
            var chars = lexed.SanitizedLine.ToCharArray();
            for (var k = 0; k < chars.Length; k++)
            {
                var ch = chars[k];
                if (ch == '"' || ch == '\'' || ch == '\\')
                    chars[k] = ' ';
            }
            result[i] = new string(chars);
            state = lexed.EndState;
        }
        return result;
    }

    private static CSharpLexedLine LexCSharpLine(string line, CSharpLexState state)
    {
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

    private static bool HasCSharpQuoteRun(string line, int index, int requiredLength)
    {
        if (requiredLength <= 0)
            return false;

        return GetCSharpQuoteRunLength(line, index) >= requiredLength;
    }

    private static (int EndLine, int? BodyStartLine, int? BodyEndLine) FindCSharpBraceRange(string[] lines, int startIndex, int startColumn = 0)
    {
        int depth = 0;
        bool opened = false;
        int? bodyStartLine = null;
        // Expression-bodied member (`=> expr;`) detection. Tracks paren/bracket depth only
        // until '=>' is observed at top level, so default-value lambdas in params
        // (e.g. `Action a = () => ...`) don't trigger expression-body mode.
        // 式本体メンバー (`=> expr;`) の検出。param のデフォルト値に出てくるラムダ
        // (`Action a = () => ...` 等) を誤検出しないよう、paren/bracket の深さを追う。
        bool expressionBody = false;
        int parenDepth = 0;
        int bracketDepth = 0;
        // `;` early-return safety check. Required so the new top-level `;` clamp
        // only fires once we have observed a parameter list (`(`), which body-less
        // function-like declarations (`void M();`, ctor signatures) always carry.
        // Without this guard, the column-space mismatch where `absoluteStartColumn`
        // arrives in collapsed-generic / attribute-stripped match-line space and is
        // sliced into raw `structuralLines` can place an unrelated leading `;` (the
        // sibling member that came before this declaration, e.g. `event ... E;`
        // ahead of a same-line `class Wrapped<T>`) at scan position 0 and trick the
        // clamp into ending the body range before the real declaration even
        // begins. Function/delegate signatures by definition pass through `(` first.
        // Closes #515 review follow-up.
        // `;` 早期 return 用の安全ガード。新たに追加する top-level `;` クランプは
        // パラメータリスト `(` を一度でも見たあとでのみ発火するようにする。これは
        // body-less な関数系宣言 (`void M();`、コンストラクタ等) は必ず `(` を通る
        // ため安全であり、逆に column-space mismatch (collapsed-generic 列や
        // attribute-strip された match-line 列を raw な `structuralLines` に
        // スライスするケース) で scan 位置 0 が直前 sibling の `;` (例: same-line
        // `class Wrapped<T>` 直前の `event ... E;`) に重なってしまった場合に、
        // 宣言が始まる前にクランプが暴発するのを防ぐ。Closes #515 review follow-up.
        bool sawOpenParen = false;
        var lexState = new CSharpLexState();

        for (int i = startIndex; i < lines.Length; i++)
        {
            var lexedLine = LexCSharpLine(lines[i], lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;
            var scanLine = i == startIndex && startColumn > 0 && startColumn < sanitizedLine.Length
                ? sanitizedLine[startColumn..]
                : i == startIndex && startColumn >= sanitizedLine.Length
                    ? string.Empty
                    : sanitizedLine;

            for (int j = 0; j < scanLine.Length; j++)
            {
                var c = scanLine[j];

                if (expressionBody)
                {
                    // In expression-body mode: track nested (), [], {} and stop at ';' at top level.
                    // 式本体モード: ()/[]/{} の深さを追い、トップレベルの ';' で終端する。
                    if (c == '(') parenDepth++;
                    else if (c == ')' && parenDepth > 0) parenDepth--;
                    else if (c == '[') bracketDepth++;
                    else if (c == ']' && bracketDepth > 0) bracketDepth--;
                    else if (c == '{') depth++;
                    else if (c == '}' && depth > 0) depth--;
                    else if (c == ';' && parenDepth == 0 && bracketDepth == 0 && depth == 0)
                        return (i + 1, bodyStartLine, i + 1);
                    continue;
                }

                if (c == '(') { parenDepth++; sawOpenParen = true; continue; }
                if (c == ')' && parenDepth > 0) { parenDepth--; continue; }
                if (c == '[') { bracketDepth++; continue; }
                if (c == ']' && bracketDepth > 0) { bracketDepth--; continue; }

                if (c == '{' && parenDepth == 0 && bracketDepth == 0)
                {
                    depth++;
                    if (!opened)
                    {
                        opened = true;
                        bodyStartLine = i + 1;
                    }
                    continue;
                }

                if (c == '}' && opened && parenDepth == 0 && bracketDepth == 0)
                {
                    depth--;
                    if (depth == 0)
                    {
                        var trailingSiblingOffset = FindNextSameLineNonClosingBraceStatementStart(scanLine, j + 1, "csharp");
                        var bodyEndLine = trailingSiblingOffset >= 0
                            && bodyStartLine.HasValue
                            && bodyStartLine.Value < i + 1
                            ? i
                            : i + 1;
                        return (i + 1, bodyStartLine, bodyEndLine);
                    }
                    continue;
                }

                // Top-level `;` after a parameter list and before any block body opened
                // terminates a body-less function-like declaration (`void M();`,
                // ctor signatures, etc.). Without this in-loop guard, recovery for
                // same-line siblings whose own line ends with the enclosing type's `}`
                // (`{ int P { get; } void M(); }`) falls through the trailing
                // `EndsWith(';')` fallback because the physical line ends with `}`,
                // then the scanner bleeds into later lines and attributes their brace
                // ranges to this body-less symbol. The `sawOpenParen` guard keeps the
                // clamp inert when `absoluteStartColumn` arrived in collapsed-generic
                // / attribute-stripped column space and slid the scan onto an earlier
                // sibling's `;`, since real function-like signatures always pass
                // through `(` before `;`. Closes #515.
                // ブロック本体が開く前に、かつパラメータリスト `(` を通過したあとで
                // 出現した top-level `;` は、本体を持たない関数系宣言
                // (`void M();`、コンストラクタ等) の終端としてその場で確定する。
                // これがないと、`{ int P { get; } void M(); }` のように物理行末が
                // `}` で閉じる same-line sibling 復元では末尾の `EndsWith(';')`
                // フォールバックが効かず、次行以降の brace を本体と誤認して
                // body-less symbol に取り込んでしまう。`sawOpenParen` ガードに
                // よって、`absoluteStartColumn` が collapsed-generic / attribute-strip
                // 後の列で渡って raw `structuralLines` 上の直前 sibling の `;` に
                // 落ち込むケースでもクランプが暴発しない (関数系シグネチャは必ず
                // `(` を通過するため)。Closes #515.
                if (c == ';' && sawOpenParen && !opened && parenDepth == 0 && bracketDepth == 0)
                    return (i + 1, null, null);

                // Detect '=>' at top level (outside any (), [], {}) before any block body opened.
                // This marks an expression-bodied member; body spans the declaration line
                // through the line holding the terminating ';'.
                // () / [] / {} の外側で、かつブロック本体がまだ開いていない状態で '=>' を検出すると
                // 式本体メンバー開始。本体は宣言行から終端 ';' の行までとする。
                if (c == '=' && j + 1 < scanLine.Length && scanLine[j + 1] == '>'
                    && !opened && parenDepth == 0 && bracketDepth == 0)
                {
                    expressionBody = true;
                    bodyStartLine = startIndex + 1;
                    j++; // consume '>' / '>' を消費
                    continue;
                }
            }

            if (!opened && !expressionBody && scanLine.TrimEnd().EndsWith(';'))
                return (startIndex + 1, null, null);
        }

        if (expressionBody)
            return (lines.Length, bodyStartLine, lines.Length);

        return opened
            ? (lines.Length, bodyStartLine, lines.Length)
            : (startIndex + 1, null, null);
    }

    private static string StripLeadingCSharpAttributeLists(
        string line,
        ref bool inLeadingAttributeBlock,
        ref int attributeBracketDepth,
        ref int attributeParenDepth,
        bool insideEnumBody)
    {
        var index = 0;
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;

        if (index >= line.Length)
            return line;

        if (!inLeadingAttributeBlock && line[index] != '[')
            return line;

        if (inLeadingAttributeBlock && ShouldRecoverFromIncompleteLeadingCSharpAttribute(line, index, insideEnumBody, attributeParenDepth))
        {
            inLeadingAttributeBlock = false;
            attributeBracketDepth = 0;
            attributeParenDepth = 0;
            return line;
        }

        var cursor = index;
        var blankUntil = index;
        while (cursor < line.Length)
        {
            if (!inLeadingAttributeBlock)
            {
                if (line[cursor] != '[')
                    break;

                inLeadingAttributeBlock = true;
                attributeBracketDepth = 0;
                attributeParenDepth = 0;
            }

            while (cursor < line.Length)
            {
                var ch = line[cursor++];
                if (ch == '[')
                {
                    attributeBracketDepth++;
                }
                else if (ch == '(')
                {
                    attributeParenDepth++;
                }
                else if (ch == ')' && attributeParenDepth > 0)
                {
                    attributeParenDepth--;
                }
                else if (ch == ']')
                {
                    attributeBracketDepth--;
                    if (attributeBracketDepth == 0)
                    {
                        inLeadingAttributeBlock = false;
                        attributeParenDepth = 0;
                        break;
                    }
                }
            }

            if (inLeadingAttributeBlock)
                return line[..index] + new string(' ', line.Length - index);

            while (cursor < line.Length && char.IsWhiteSpace(line[cursor]))
                cursor++;

            blankUntil = cursor;
            if (cursor >= line.Length || line[cursor] != '[')
                break;
        }

        return blankUntil < line.Length
            ? line[..index] + new string(' ', blankUntil - index) + line[blankUntil..]
            : line[..index] + new string(' ', blankUntil - index);
    }

    private static bool ShouldRecoverFromIncompleteLeadingCSharpAttribute(
        string line,
        int firstNonWhitespaceIndex,
        bool insideEnumBody,
        int attributeParenDepth)
    {
        if (firstNonWhitespaceIndex >= line.Length || line[firstNonWhitespaceIndex] == '[')
            return false;

        return TryMatchAnyRecoverableCSharpPattern(line, insideEnumBody, attributeParenDepth);
    }

    private static bool TryMatchAnyRecoverableCSharpPattern(string line, bool insideEnumBody, int attributeParenDepth)
    {
        if (PatternCache.TryGetValue("csharp", out var patterns))
        {
            foreach (var pattern in patterns)
            {
                if (ReferenceEquals(pattern.Regex, CSharpEnumMemberRegex))
                    continue;

                if (pattern.Regex.IsMatch(line))
                    return true;
            }
        }

        return insideEnumBody
            && attributeParenDepth == 0
            && CSharpEnumMemberNameRegex.IsMatch(line);
    }

    /// <summary>
    /// Return true when a batch (.bat / .cmd) line is a comment, i.e. `::` / `:::` / `rem` /
    /// `@rem` (with optional leading whitespace and case-insensitive `rem`). Comment lines
    /// must not contribute `set` property symbols even when they contain the boundary tokens
    /// (`&`, `(`, `else`, `do`) that the new inline-set-capture regex accepts.
    /// batch (.bat / .cmd) のコメント行 (`::` / `:::` / `rem` / `@rem`、先頭空白可、`rem` は大小文字不問) のときに
    /// true を返す。新しい inline `set` 捕捉正規表現が受け付ける境界トークン (`&` / `(` / `else` / `do`) を
    /// 含んでいても、コメント行からは `set` property を拾わない。
    /// </summary>
    private static bool IsBatchCommentLine(string line)
    {
        var i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
            i++;

        if (i >= line.Length)
            return false;

        // `::` (and therefore also `:::`, `::: ...`) opens a batch comment that consumes the
        // rest of the line. The label regex does not match these because it requires a name
        // char after the first `:`, but the property regex could match their inline tokens.
        // `::` 以降はコメント (`:::`、`::: ...` も同様)。ラベル正規表現は `:` の後ろに名前文字を
        // 要求するため影響を受けないが、property 正規表現は inline トークンを拾ってしまう。
        if (line[i] == ':' && i + 1 < line.Length && line[i + 1] == ':')
            return true;

        // `@rem` (echo-suppression prefix + rem). Accept optional whitespace between `@` and
        // `rem` to mirror how the property regex tolerates `@ set`.
        // `@rem` (echo 抑止プレフィクス + rem) 。property 正規表現が `@ set` を許すのに合わせて
        // `@` と `rem` の間の空白も許容する。
        if (line[i] == '@')
        {
            var j = i + 1;
            while (j < line.Length && (line[j] == ' ' || line[j] == '\t'))
                j++;
            return IsBatchRemKeyword(line, j);
        }

        return IsBatchRemKeyword(line, i);
    }

    private static bool IsBatchRemKeyword(string line, int start)
    {
        // A bare `rem` or `rem` followed by whitespace / end-of-line is a comment.
        // Case-insensitive: `REM`, `rem`, `Rem`, `rEM`, etc. are all comments.
        // 単独の `rem` または `rem` の直後が空白か行末ならコメント扱い。
        // 大小文字不問 — `REM` / `rem` / `Rem` / `rEM` などすべてコメント。
        if (start + 3 > line.Length)
            return false;
        if ((line[start] | 0x20) != 'r')
            return false;
        if ((line[start + 1] | 0x20) != 'e')
            return false;
        if ((line[start + 2] | 0x20) != 'm')
            return false;
        if (start + 3 == line.Length)
            return true;
        var next = line[start + 3];
        return next == ' ' || next == '\t' || next == '\r' || next == '\n';
    }

    private static bool CanContinueScanningSameLineBraceBody(
        string? lang,
        string kind,
        BodyStyle bodyStyle,
        int? endLine,
        int startLine,
        int sameLineEndColumn,
        int absoluteStartColumn)
    {
        if (bodyStyle != BodyStyle.Brace || endLine != startLine || sameLineEndColumn < absoluteStartColumn)
            return false;

        return lang is "javascript" or "typescript" or "css" or "java"
            || (lang == "csharp" && CanContinueScanningSameLineCSharpBraceBody(kind));
    }

    private static bool CanStepIntoSameLineTypeBody(string? lang, string kind)
    {
        if (kind is not ("class" or "struct" or "interface" or "enum" or "namespace"))
            return false;

        return lang is "csharp" or "java" or "cpp";
    }

    private static bool IsCSharpFieldLikeFunctionPattern(SymbolPattern pattern)
        => pattern.Kind == "function"
            && pattern.BodyStyle == BodyStyle.None
            && pattern.ReturnTypeGroup != null;

    private static string? NormalizeCSharpImplicitPartialMethodReturnType(
        string? lang,
        SymbolPattern pattern,
        Match match,
        string? returnType)
    {
        if (lang == "csharp"
            && pattern.Kind == "function"
            && pattern.ReturnTypeGroup != null
            && returnType == "partial")
        {
            return "void";
        }

        return returnType;
    }

    private static void NormalizeCSharpImplicitPartialConstructorReturnTypes(List<SymbolRecord> symbols)
    {
        foreach (var symbol in symbols)
        {
            var signature = symbol.Signature?.TrimStart();
            if (symbol.Kind == "function"
                && symbol.ReturnType == "void"
                && string.Equals(symbol.Name, symbol.ContainerName, StringComparison.Ordinal)
                && signature != null
                && CSharpPartialFunctionDeclarationSignatureRegex.IsMatch(signature))
            {
                symbol.ReturnType = null;
            }
        }
    }

    private static bool HasInvalidCSharpReturnTypeSuffix(string? returnType)
    {
        if (string.IsNullOrWhiteSpace(returnType))
            return true;

        var trimmed = returnType.TrimEnd();
        if (trimmed.Length == 0)
            return true;

        var lastChar = trimmed[^1];
        if (lastChar is '<' or '=' or ':' or '+' or '-' or '/' or '%' or '!' or '&' or '|' or '^' or '~' or '.')
            return true;

        var tokenStart = trimmed.Length - 1;
        while (tokenStart > 0
            && (char.IsLetterOrDigit(trimmed[tokenStart - 1]) || trimmed[tokenStart - 1] == '_'))
        {
            tokenStart--;
        }

        if (tokenStart > 0
            && trimmed[tokenStart - 1] == '@'
            && CSharpSymbolNameNormalizer.IsVerbatimIdentifierPrefix(trimmed, tokenStart - 1))
        {
            return false;
        }

        var lastToken = trimmed[tokenStart..];
        return lastToken is "as" or "is" or "return" or "throw" or "new";
    }

    private static bool IsInsidePreviouslyEmittedCSharpMemberBody(
        string[] lines,
        List<SymbolRecord> symbols,
        int candidateLine,
        int candidateColumn)
    {
        foreach (var symbol in symbols)
        {
            if (symbol.Kind is not "function" and not "property" and not "event")
                continue;
            if (!symbol.BodyStartLine.HasValue || !symbol.BodyEndLine.HasValue)
                continue;
            if (candidateLine <= symbol.StartLine)
                continue;
            if (candidateLine < symbol.BodyStartLine.Value || candidateLine > symbol.BodyEndLine.Value)
                continue;
            if (candidateLine == symbol.BodyEndLine.Value
                && TryFindCSharpSemicolonTerminatedSignatureExtent(
                    lines,
                    Math.Max(0, symbol.StartLine - 1),
                    symbol.StartColumn ?? 0,
                    out var signatureLastLineIndex,
                    out var signatureLastLineExclusiveEndColumn)
                && signatureLastLineIndex + 1 == candidateLine
                && signatureLastLineExclusiveEndColumn.HasValue
                && candidateColumn >= signatureLastLineExclusiveEndColumn.Value)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static int FindNextSameLineBraceStatementStart(string matchLine, int startIndex, string? lang)
    {
        return lang is "javascript" or "typescript"
            ? FindNextJavaScriptTypeScriptStatementStart(matchLine, startIndex)
            : FindNextBraceStatementStart(matchLine, startIndex);
    }

    // C# same-line restarts can legitimately hit a container-closing `}`, an empty
    // statement `;`, or a carried verbatim-string closing `"` before the next real sibling
    // declaration (`... P { get; } } public int Q { get; }`, or a carried multiline string
    // continuation like `"; public class Child { }`). Keep advancing until we reach a
    // non-`}` / non-`;` / non-`"` statement start so the later real declaration stays visible.
    // C# の同一行再開は、次の実 sibling 宣言の前に container を閉じる `}`、空文の `;`、
    // あるいは継続 verbatim string の閉じ `"` に当たりうる（`... P { get; } } public int Q { get; }`
    // や、`"; public class Child { }` のような継続文字列の閉じ直後）。後続の実宣言を落とさないよう、
    // 非 `}` / 非 `;` / 非 `"` の statement start に当たるまで再開位置を進める。
    private static int FindNextSameLineNonClosingBraceStatementStart(string matchLine, int startIndex, string? lang)
    {
        var nextOffset = FindNextSameLineBraceStatementStart(matchLine, startIndex, lang);
        while (lang == "csharp"
               && nextOffset >= 0
               && nextOffset < matchLine.Length
               && matchLine[nextOffset] is '}' or ';' or '"')
        {
            nextOffset = FindNextSameLineBraceStatementStart(matchLine, nextOffset + 1, lang);
        }

        return nextOffset;
    }

    private static int FindNextBraceStatementStart(string line, int startIndex)
    {
        var index = Math.Max(0, startIndex);
        while (index < line.Length)
        {
            while (index < line.Length && char.IsWhiteSpace(line[index]))
                index++;

            if (index >= line.Length)
                return -1;

            var previous = index - 1;
            while (previous >= 0 && char.IsWhiteSpace(line[previous]))
                previous--;

            if (previous < 0 || line[previous] is ';' or '{' or '}')
                return index;

            index++;
        }

        return -1;
    }

    // For C# plain fields (kind `property`, BodyStyle.None), find the end of the
    // field's declaration statement on the same (merged) match line so the
    // signature can be clamped to the full declaration text and the same-line
    // pattern scanner can resume after the terminating `;`. Walks with paren /
    // bracket / brace depth tracking so `{` / `}` inside an initializer
    // (collection or object initializer, lambda body) does not short-circuit
    // the scan; when an unbalanced `}` is encountered (the closing brace of
    // the enclosing type body) the position of that `}` is returned instead
    // so signature and advance both stop before the wrapper terminator. Input
    // is expected to be the structurally-masked match line so string-literal
    // `{` / `;` cannot poison the depth tracker.
    // C# 通常フィールド（kind `property`、BodyStyle.None）向けに、結合済みマッチ行での
    // 宣言文の終端位置を返す。signature を `;` まで含む完全な宣言文字列に揃え、かつ
    // 同一行のパターンスキャンを `;` の次から再開できるようにするために使う。paren /
    // bracket / brace の深さを追うので、初期化子（コレクション / オブジェクト初期化子や
    // ラムダ本体）内の `{` / `}` で判定が途切れない。深さ 0 で出現する `}`（囲む型本体の
    // 閉じ括弧）は、その位置をそのまま返すため signature と advance の両方がラッパー
    // 終端の手前で止まる。入力は構造的にマスク済みのマッチ行を想定し、文字列リテラル内の
    // `{` / `;` が深さトラッカを誤認させないようにしている。
    private static int FindCSharpSameLineStatementEnd(string maskedLine, int startIndex)
    {
        int parenDepth = 0;
        int bracketDepth = 0;
        int braceDepth = 0;
        var index = Math.Max(0, startIndex);
        while (index < maskedLine.Length)
        {
            var ch = maskedLine[index];
            if (ch == '(')
            {
                parenDepth++;
            }
            else if (ch == ')')
            {
                if (parenDepth > 0) parenDepth--;
            }
            else if (ch == '[')
            {
                bracketDepth++;
            }
            else if (ch == ']')
            {
                if (bracketDepth > 0) bracketDepth--;
            }
            else if (ch == '{')
            {
                braceDepth++;
            }
            else if (ch == '}')
            {
                if (braceDepth > 0)
                {
                    braceDepth--;
                }
                else
                {
                    return index;
                }
            }
            else if (ch == ';' && parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
            {
                return index + 1;
            }

            index++;
        }

        return maskedLine.Length;
    }

    // Reuse the same top-level `;` scan as plain fields for other compact same-line C#
    // members (`event E;`, interface/abstract methods like `void M();`, delegates, etc.).
    // Returns the inclusive `;` column when one exists on the same physical line and -1
    // when the declaration instead runs into the enclosing `}` or simply has no same-line
    // semicolon terminator. Closes #473.
    // 通常フィールドと同じ top-level `;` 探索を、他のコンパクトな同一行 C# member
    // (`event E;`、`void M();` 形の interface/abstract method、delegate など) にも
    // 再利用する。同一物理行に `;` があればその包含列を返し、囲み `}` にぶつかる、
    // あるいは同一行終端 `;` 自体が無い場合は -1 を返す。Closes #473.
    private static int FindCSharpSameLineSemicolonEndColumn(string maskedLine, int startIndex)
    {
        var statementEnd = FindCSharpSameLineStatementEnd(maskedLine, startIndex);
        var semicolonIndex = statementEnd - 1;
        return semicolonIndex >= startIndex
            && semicolonIndex < maskedLine.Length
            && maskedLine[semicolonIndex] == ';'
            ? semicolonIndex
            : -1;
    }

    private static int FindCSharpSameLineEnumMemberEndColumn(string maskedLine, int startIndex)
    {
        int parenDepth = 0;
        int bracketDepth = 0;
        int braceDepth = 0;
        for (var index = Math.Max(0, startIndex); index < maskedLine.Length; index++)
        {
            var ch = maskedLine[index];
            if (ch == '(')
            {
                parenDepth++;
            }
            else if (ch == ')')
            {
                if (parenDepth > 0)
                    parenDepth--;
            }
            else if (ch == '[')
            {
                bracketDepth++;
            }
            else if (ch == ']')
            {
                if (bracketDepth > 0)
                    bracketDepth--;
            }
            else if (ch == '{')
            {
                braceDepth++;
            }
            else if (ch == '}')
            {
                if (braceDepth > 0)
                {
                    braceDepth--;
                }
                else
                {
                    return index;
                }
            }
            else if (ch == ',' && parenDepth == 0 && bracketDepth == 0 && braceDepth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindSameLineBraceEndColumn(string line, int startColumn, string? lang, string kind)
    {
        return lang switch
        {
            "javascript" or "typescript" => FindJavaScriptTypeScriptSameLineBraceEndColumn(line, startColumn, lang),
            "css" => FindCssSameLineBraceEndColumn(line, startColumn),
            "csharp" => FindCSharpSameLineBraceEndColumn(line, startColumn),
            "java" => FindJavaSameLineBraceEndColumn(line, startColumn),
            _ => -1,
        };
    }

    private static bool CanContinueScanningSameLineCSharpBraceBody(string kind)
    {
        return kind is "namespace" or "class" or "struct" or "interface" or "enum" or "property";
    }

    private static bool CanUseCSharpSameLineSemicolonEndColumn(string kind)
    {
        return kind is "function" or "event" or "delegate";
    }

    private static bool CanRestartCSharpSameLineSiblingScan(string kind)
    {
        return kind is "function" or "property" or "event" or "delegate" or "enum";
    }

    private static int FindCSharpSameLineBraceEndColumn(string line, int startColumn)
    {
        return FindCSharpSameLineBraceEndColumnFromSanitized(
            LexCSharpLine(line, new CSharpLexState()).SanitizedLine,
            startColumn);
    }

    private static int FindCSharpSameLineBraceEndColumnFromSanitized(string sanitizedLine, int startColumn)
    {
        var depth = 0;
        var opened = false;
        var expressionBody = false;
        var parenDepth = 0;
        var bracketDepth = 0;

        for (var index = Math.Max(0, startColumn); index < sanitizedLine.Length; index++)
        {
            var ch = sanitizedLine[index];

            if (expressionBody)
            {
                if (ch == '(')
                    parenDepth++;
                else if (ch == ')' && parenDepth > 0)
                    parenDepth--;
                else if (ch == '[')
                    bracketDepth++;
                else if (ch == ']' && bracketDepth > 0)
                    bracketDepth--;
                else if (ch == '{')
                    depth++;
                else if (ch == '}' && depth > 0)
                    depth--;
                else if (ch == ';' && parenDepth == 0 && bracketDepth == 0 && depth == 0)
                    return index;

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

            if (ch == '[')
            {
                bracketDepth++;
                continue;
            }

            if (ch == ']' && bracketDepth > 0)
            {
                bracketDepth--;
                continue;
            }

            if (ch == '{' && parenDepth == 0 && bracketDepth == 0)
            {
                depth++;
                opened = true;
                continue;
            }

            if (ch == '}' && opened && parenDepth == 0 && bracketDepth == 0)
            {
                depth--;
                if (depth == 0)
                    return index;

                continue;
            }

            // Expression-bodied members (`=> expr;`) have no surrounding `{}` to anchor the
            // same-line end column. Detect the top-level `=>` so later sibling declarations
            // on the same physical line are not swallowed into the current signature / body
            // extent. Closes #470 review follow-up.
            // 式本体 member (`=> expr;`) には `{}` が無いため、same-line 終端列を
            // top-level の `=>` から `;` までで判定する。これにより、同じ物理行の後続
            // sibling 宣言を現在の signature / body 範囲へ飲み込まないようにする。
            // Closes #470 review follow-up.
            if (ch == '='
                && index + 1 < sanitizedLine.Length
                && sanitizedLine[index + 1] == '>'
                && !opened
                && parenDepth == 0
                && bracketDepth == 0)
            {
                expressionBody = true;
                index++;
            }
        }

        return -1;
    }

    // Body-less Java members (`void a();`, `String[] value();`, `int[] v() default {1};`) need a
    // same-line statement-end scanner so later siblings on the same physical line stay reachable.
    // Track comments / strings / text blocks with the Java lexer and balance `()`, `[]`, and
    // annotation/default-value braces. If the enclosing `}` arrives before a top-level `;`, return
    // that `}` position so callers can stop without absorbing the wrapper close.
    // Java の body-less member（`void a();` / `String[] value();` / `int[] v() default {1};`）向けの
    // same-line statement-end scanner。Java lexer で comment / 文字列 / text block を避けつつ、
    // `()` / `[]` / annotation / default 値の `{}` を釣り合わせる。top-level `;` より先に
    // 囲み `}` が来た場合はその位置を返し、呼び出し側が wrapper close を飲み込まず止まれるようにする。
    private static int FindJavaSameLineStatementEnd(string line, int startColumn)
    {
        var mode = JavaScanMode.Normal;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var column = Math.Max(0, startColumn);
        while (column < line.Length)
        {
            if (TryConsumeJavaNonCode(line, ref column, ref mode))
                continue;

            var ch = line[column];
            if (ch == '(')
            {
                parenDepth++;
            }
            else if (ch == ')' && parenDepth > 0)
            {
                parenDepth--;
            }
            else if (ch == '[')
            {
                bracketDepth++;
            }
            else if (ch == ']' && bracketDepth > 0)
            {
                bracketDepth--;
            }
            else if (ch == '{')
            {
                braceDepth++;
            }
            else if (ch == '}')
            {
                if (braceDepth > 0)
                {
                    braceDepth--;
                }
                else
                {
                    return column;
                }
            }
            else if (ch == ';'
                && parenDepth == 0
                && bracketDepth == 0
                && braceDepth == 0)
            {
                return column + 1;
            }

            column++;
        }

        return line.Length;
    }

    // Walk upward from the identifier line looking for a contiguous run of modifier-only
    // physical lines, skipping blank lines and attribute-stripped whitespace. Returns the
    // concatenated modifier prefix (in declaration order) so callers can prepend it to the
    // identifier line for regex matching. Returns null when no modifier-only predecessor
    // exists. Used to recover wrapped C# constructors whose leading `static` / `public` /
    // etc. sits on its own physical line. Closes #348.
    // 識別子行から上に遡り、空行や属性ストリップで空白化された行をスキップしつつ、
    // モディファイアのみの物理行を連続して連結する。宣言順に連結したプレフィックスを返し、
    // 呼び出し元は識別子行の先頭に付けて regex マッチに使える。先頭モディファイア行が
    // 見つからなければ null を返す。C# の `static` / `public` などが単独行に書かれた
    // ラップ型コンストラクタを拾うために使う。Closes #348.
    private static CSharpWrappedHeaderModifierInfo? TryFindCSharpWrappedHeaderModifier(
        string[] csharpMatchLines,
        int nameLineIndex)
    {
        if (nameLineIndex <= 0)
            return null;

        string? prefix = null;
        for (int index = nameLineIndex - 1; index >= 0; index--)
        {
            var structural = csharpMatchLines[index];
            if (string.IsNullOrWhiteSpace(structural))
                continue;

            if (!CSharpWrappedHeaderModifierLineRegex.IsMatch(structural))
                break;

            var structuralTrimmed = structural.Trim();
            prefix = prefix == null
                ? structuralTrimmed
                : structuralTrimmed + " " + prefix;
        }

        if (prefix == null)
            return null;

        return new CSharpWrappedHeaderModifierInfo(prefix);
    }

    // Enumerate candidate prefixes to retry against the C# function-kind regexes when the
    // full wrapped-modifier prefix fails. Multi-modifier shapes like
    // `public\nstatic\nP1()` synthesize `public static P1()` which neither the ctor regex
    // (accepts only unsafe/extern between visibility and name) nor the static-ctor regex
    // (requires static first) will match. Falling back to `static`-only and
    // visibility-only variants lets the respective regex still fire so the wrapped ctor
    // is not silently dropped. Closes #348.
    // ラップされた先頭モディファイア prefix で C# function 系パターンに失敗した場合に
    // 試す候補 prefix を列挙する。`public\nstatic\nP1()` のような複数モディファイア形は
    // `public static P1()` と合成されるが、ctor regex は visibility と name の間に
    // `unsafe` / `extern` しか許さず、静的 ctor regex は `static` 先頭を要求するため、
    // このままではどちらもマッチしない。`static` 単独や visibility 単独の variant に
    // フォールバックして、適合する regex を拾えるようにする。Closes #348.
    private static IEnumerable<string> EnumerateCSharpWrappedModifierCandidates(string prefix)
    {
        yield return prefix;

        var tokens = prefix.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length <= 1)
            yield break;

        var hasStatic = false;
        string? visibility = null;
        foreach (var token in tokens)
        {
            if (token == "static")
                hasStatic = true;
            else if (visibility == null
                && token is "public" or "private" or "protected" or "internal" or "file")
                visibility = token;
        }

        if (hasStatic)
            yield return "static";
        if (visibility != null)
            yield return visibility;
    }

    /// <summary>
    /// Track multi-line C# `[...]` bracket sections across lines and blank out any text that
    /// sits inside those sections, so downstream symbol regexes do not treat interior identifiers
    /// as declarations. Activates whenever a `[` opens without a matching `]` on the same line,
    /// regardless of whether the `[` sits at the start of the line (leading attribute) or deeper
    /// inside the line (parameter attribute like `void M([\n Attr\n] T x)`, type-parameter
    /// attribute like `class C<[\n Attr\n] T>`, delegate/lambda parameter attributes, etc.).
    /// Single-line attribute lists continue to be handled by `StripLeadingCSharpAttributeLists`.
    /// 複数行にまたがる C# `[...]` セクションを跨行で追跡し、内部の文字列を空白化することで
    /// 下流のシンボル regex が内部の識別子を宣言として誤解釈しないようにする。`[` が行頭
    /// （空白の後）にある場合だけでなく、`void M([\n Attr\n] T x)` のようなパラメータ属性、
    /// `class C<[\n Attr\n] T>` のような型パラメータ属性、delegate / lambda のパラメータ属性など、
    /// 行の途中で開いて同一行で閉じない `[` でも作動する。同一行で完結する属性リストは
    /// `StripLeadingCSharpAttributeLists` が引き続き担当する。
    /// </summary>
    private static string StripMultiLineCSharpAttributeInterior(string line, ref int depth)
    {
        if (depth == 0)
        {
            // Scan the line for a `[` that is NOT closed on the same line. Everything before
            // that `[` is real code (method header text like `void M(`, generic opener like
            // `class C<`, etc.) and must be preserved so downstream declaration regexes can
            // still recognize the surrounding construct. Everything from the unclosed `[`
            // onward is blanked, and subsequent lines are blanked until the matching `]`.
            // Only attribute-position `[` should trigger blanking — a multi-line indexer
            // declaration such as `public int this[\n    int i\n] => _items[i];` opens `[`
            // immediately after the identifier `this`, which is NOT an attribute and must
            // not be stripped (otherwise the indexer regex sees only `public int this` and
            // the indexer silently disappears from symbols / definition / outline). Treat
            // `[` as an attribute opener only when the immediately preceding non-whitespace
            // character is not a word character (`[_A-Za-z0-9]`) and not `)` / `]` (which
            // indicate indexer / array access on an expression result or chained indexer).
            // 行内を走査し、同一行で閉じない `[` を探す。その `[` より前は通常のコード
            // （`void M(` のようなメソッドヘッダ、`class C<` のようなジェネリック開口など）
            // であり、下流の宣言 regex が外側の構文を認識できるように残す必要がある。
            // 閉じない `[` 以降は空白化し、対応する `]` が現れるまで後続行も空白化する。
            // `[` が属性位置にあるときだけ空白化する — `public int this[\n    int i\n]`
            // のような複数行インデクサ宣言では `this` 直後の `[` が属性でないため、
            // ここを削ってしまうとインデクサがシンボルから消える。直前の非空白文字が
            // 語文字（`[_A-Za-z0-9]`）でも `)` / `]` でもない場合にのみ属性開口と判定する。
            int openIndex = -1;
            int localDepth = 0;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '[')
                {
                    if (localDepth == 0)
                    {
                        // Look back past whitespace for the character that introduces the `[`.
                        // 先行する非空白文字を探して `[` の導入子を判定する。
                        int p = i - 1;
                        while (p >= 0 && (line[p] == ' ' || line[p] == '\t'))
                            p--;
                        if (p >= 0)
                        {
                            char prev = line[p];
                            if (prev == '_' || (prev >= 'A' && prev <= 'Z') || (prev >= 'a' && prev <= 'z') || (prev >= '0' && prev <= '9') || prev == ')' || prev == ']')
                            {
                                // Not an attribute opener (e.g. `this[`, `arr[`, `(expr)[`, `arr[i][`).
                                // Treat this `[` as opaque — do not track depth, do not blank.
                                // 属性開口ではない（`this[`・`arr[`・`(expr)[`・`arr[i][` など）。
                                // この `[` は追跡も空白化もしない。
                                continue;
                            }
                        }
                        openIndex = i;
                    }
                    localDepth++;
                }
                else if (line[i] == ']')
                {
                    if (localDepth > 0)
                    {
                        localDepth--;
                        if (localDepth == 0)
                            openIndex = -1;
                    }
                }
            }

            if (openIndex < 0 || localDepth <= 0)
                return line;

            depth = localDepth;
            return line.Substring(0, openIndex);
        }

        // We are inside a multi-line attribute section. Walk the line, closing brackets when we
        // see `]`. Once depth returns to zero, the remainder of the line is real code.
        int index = 0;
        while (index < line.Length && depth > 0)
        {
            if (line[index] == '[') depth++;
            else if (line[index] == ']') depth--;
            index++;
        }
        if (depth > 0)
            return string.Empty;
        return line[index..];
    }

    private static CSharpPropertyMatchCandidate BuildCSharpPropertyMatchLine(string[] lines, string[] csharpMatchLines, int startLineIndex)
    {
        var matchLine = csharpMatchLines[startLineIndex];
        if (IsCSharpNonMemberHeaderLine(matchLine))
            return new CSharpPropertyMatchCandidate(matchLine, startLineIndex, startLineIndex);

        var isPropertyHeaderPrefix = CSharpPropertyHeaderPrefixRegex.IsMatch(matchLine);
        var isMethodHeaderPrefix = CSharpMethodHeaderPrefixRegex.IsMatch(matchLine);
        if (string.IsNullOrWhiteSpace(matchLine)
            || (!isPropertyHeaderPrefix && !isMethodHeaderPrefix)
            || HasCSharpPropertyAccessorStart(matchLine)
            || CSharpWrappedHeaderModifierLineRegex.IsMatch(matchLine))
        {
            // Modifier-only lines (`static`, `public`, etc. on their own physical line)
            // are handled by the name-line wrapped-modifier recovery in the extraction
            // loop. If the field-header merger runs here, it joins the next line and
            // produces a phantom emission at the modifier line with a truncated
            // signature. Returning the raw matchLine lets the pattern match fail at the
            // modifier line so only the name line emits a symbol. Closes #348.
            // 単独行に書かれたモディファイア（`static`、`public` 等）は、抽出ループ側の
            // 名前行ラップド救済が処理する。フィールドヘッダ結合がここで動くと、次の行を
            // 結合してモディファイア行に signature の切れた幻のエミットを残してしまう。
            // 生の matchLine を返してモディファイア行ではパターンに失敗させ、名前行のみが
            // シンボルを emit するようにする。Closes #348.
            return new CSharpPropertyMatchCandidate(matchLine, startLineIndex, startLineIndex);
        }

        if (TryFindCSharpExpressionArrow(lines, startLineIndex, startLineIndex, out var sameLineArrowLineIndex, out var sameLineArrowColumn))
        {
            var expressionEndLineIndex = FindCSharpExpressionBodyEndLine(lines, sameLineArrowLineIndex, sameLineArrowColumn);
            return new CSharpPropertyMatchCandidate(matchLine, startLineIndex, startLineIndex, null, expressionEndLineIndex);
        }

        var builder = new StringBuilder(matchLine.TrimEnd());

        // Detect `{` on the sanitized line so braces inside string literals or comments
        // don't flip a plain field into property-body handling.
        // サニタイズ済みの行で `{` を検出し、文字列やコメント内の `{` で通常フィールドが
        // property 本体扱いに切り替わらないようにする。
        var openBraceLineIndex = csharpMatchLines[startLineIndex].IndexOf('{') >= 0
            ? startLineIndex
            : -1;
        var openBraceExclusiveEndColumn = openBraceLineIndex == startLineIndex
            ? ResolveCSharpBraceColumn(lines[startLineIndex], csharpMatchLines[startLineIndex]) + 1
            : (int?)null;

        if (HasCSharpTopLevelFieldInitializer(matchLine)
            || openBraceLineIndex >= 0 && IsCSharpConfirmedMemberOrMethodPrefix(matchLine))
        {
            return ContinueConfirmedCSharpPropertyMatch(
                lines,
                csharpMatchLines,
                builder,
                startLineIndex,
                startLineIndex,
                matchLine,
                openBraceLineIndex,
                openBraceExclusiveEndColumn);
        }

        var lookaheadLimitExclusive = Math.Min(csharpMatchLines.Length, startLineIndex + CSharpPropertyMatchLookaheadLineLimit + 1);
        for (int i = startLineIndex + 1; i < lookaheadLimitExclusive; i++)
        {
            var nextLine = csharpMatchLines[i].Trim();
            if (nextLine.Length == 0)
                continue;

            if (builder.Length + 1 + nextLine.Length > CSharpPropertyMatchLookaheadCharLimit)
                break;

            builder.Append(' ').Append(nextLine);
            var normalizedCombined = CollapseCSharpGenericTypeWhitespace(builder.ToString());

            if (openBraceLineIndex < 0 && csharpMatchLines[i].IndexOf('{') >= 0)
            {
                openBraceLineIndex = i;
                openBraceExclusiveEndColumn = ResolveCSharpBraceColumn(lines[i], csharpMatchLines[i]) + 1;
            }

            if (HasCSharpPropertyAccessorStart(normalizedCombined))
            {
                return new CSharpPropertyMatchCandidate(
                    normalizedCombined,
                    i,
                    openBraceLineIndex >= 0 ? openBraceLineIndex : i,
                    openBraceLineIndex >= 0 ? openBraceExclusiveEndColumn : null);
            }

            if (TryFindCSharpExpressionArrow(lines, startLineIndex, i, out var arrowLineIndex, out var arrowColumn))
            {
                var expressionEndLineIndex = FindCSharpExpressionBodyEndLine(lines, arrowLineIndex, arrowColumn);
                return new CSharpPropertyMatchCandidate(normalizedCombined, i, i, null, expressionEndLineIndex);
            }

            // Plain-field multi-line declaration: continuation reaches a top-level `;`.
            // Object / collection initializers (`= new() { ... };`) balance their own braces,
            // so `HasCSharpTopLevelSemicolon` fires only at the real terminator — regardless
            // of whether an earlier line contained `{`. `HasCSharpPropertyAccessorStart` above
            // already claimed true property bodies, so reaching this point means the `{`
            // belongs to an initializer, not an accessor block.
            // 複数行にまたがる通常フィールド宣言: 継続行でトップレベルの `;` に到達する形に対応する。
            // `= new() { ... };` のようなオブジェクト/コレクション初期化子は自身の brace を閉じるため、
            // `HasCSharpTopLevelSemicolon` は真の終端 `;` のみで発火し、先行行に `{` があっても
            // 問題ない。上の `HasCSharpPropertyAccessorStart` が真の property 本体を先に拾うため、
            // ここに到達する `{` は初期化子側のものと確定している。
            if (HasCSharpTopLevelSemicolon(normalizedCombined))
            {
                return new CSharpPropertyMatchCandidate(normalizedCombined, i, i);
            }

            if (HasCSharpTopLevelFieldInitializer(normalizedCombined)
                || openBraceLineIndex >= 0 && IsCSharpConfirmedMemberOrMethodPrefix(normalizedCombined))
            {
                return ContinueConfirmedCSharpPropertyMatch(
                    lines,
                    csharpMatchLines,
                    builder,
                    startLineIndex,
                    i,
                    normalizedCombined,
                    openBraceLineIndex,
                    openBraceExclusiveEndColumn);
            }

            if (nextLine.StartsWith(";", StringComparison.Ordinal))
                break;
        }

        return new CSharpPropertyMatchCandidate(matchLine, startLineIndex, startLineIndex);
    }

    private static bool IsCSharpNonMemberHeaderLine(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("namespace ", StringComparison.Ordinal)
            || trimmed.StartsWith("using ", StringComparison.Ordinal)
            || trimmed.StartsWith("global using ", StringComparison.Ordinal)
            || trimmed.StartsWith("extern alias ", StringComparison.Ordinal)
            || trimmed.StartsWith("//", StringComparison.Ordinal);
    }

    private static CSharpPropertyMatchCandidate ContinueConfirmedCSharpPropertyMatch(
        string[] lines,
        string[] csharpMatchLines,
        StringBuilder builder,
        int startLineIndex,
        int currentLineIndex,
        string normalizedCombined,
        int openBraceLineIndex,
        int? openBraceExclusiveEndColumn)
    {
        var semicolonTracker = new CSharpTopLevelSemicolonTracker();
        semicolonTracker.Scan(normalizedCombined);

        StringBuilder? accessorProbeBuilder = null;
        var accessorProbeStatus = CSharpAccessorProbeStatus.Rejected;
        if (openBraceLineIndex >= 0 && openBraceExclusiveEndColumn.HasValue)
        {
            accessorProbeBuilder = BuildCSharpAccessorProbeBuilder(
                csharpMatchLines,
                openBraceLineIndex,
                openBraceExclusiveEndColumn.Value,
                currentLineIndex);
            accessorProbeStatus = ClassifyCSharpAccessorProbe(accessorProbeBuilder.ToString());
            if (accessorProbeStatus == CSharpAccessorProbeStatus.Found)
            {
                return new CSharpPropertyMatchCandidate(
                    normalizedCombined,
                    currentLineIndex,
                    openBraceLineIndex,
                    openBraceExclusiveEndColumn);
            }
        }

        for (int i = currentLineIndex + 1; i < csharpMatchLines.Length; i++)
        {
            var nextLine = csharpMatchLines[i].Trim();
            if (nextLine.Length == 0)
                continue;

            builder.Append(' ').Append(nextLine);
            semicolonTracker.Scan(nextLine);

            if (openBraceLineIndex < 0 && csharpMatchLines[i].IndexOf('{') >= 0)
            {
                openBraceLineIndex = i;
                openBraceExclusiveEndColumn = ResolveCSharpBraceColumn(lines[i], csharpMatchLines[i]) + 1;
                accessorProbeBuilder = BuildCSharpAccessorProbeBuilder(
                    csharpMatchLines,
                    openBraceLineIndex,
                    openBraceExclusiveEndColumn.Value,
                    i);
                accessorProbeStatus = ClassifyCSharpAccessorProbe(accessorProbeBuilder.ToString());
            }
            else if (accessorProbeBuilder != null
                && accessorProbeStatus == CSharpAccessorProbeStatus.Pending)
            {
                AppendCSharpAccessorProbeLine(accessorProbeBuilder, csharpMatchLines[i], null);
                accessorProbeStatus = ClassifyCSharpAccessorProbe(accessorProbeBuilder.ToString());
            }

            if (accessorProbeStatus == CSharpAccessorProbeStatus.Found)
            {
                return new CSharpPropertyMatchCandidate(
                    CollapseCSharpGenericTypeWhitespace(builder.ToString()),
                    i,
                    openBraceLineIndex,
                    openBraceExclusiveEndColumn);
            }

            if (semicolonTracker.HasTopLevelSemicolon)
            {
                return new CSharpPropertyMatchCandidate(
                    CollapseCSharpGenericTypeWhitespace(builder.ToString()),
                    i,
                    i);
            }
        }

        return new CSharpPropertyMatchCandidate(normalizedCombined, currentLineIndex, currentLineIndex);
    }

    private static bool IsCSharpConfirmedMemberOrMethodPrefix(string line)
        => CSharpConfirmedMemberPrefixRegex.IsMatch(line) || CSharpConfirmedMethodPrefixRegex.IsMatch(line);

    // Prefer the raw line's `{` column (to preserve original positioning for body slicing),
    // falling back to the sanitized line only when the raw line hides the brace in a string
    // literal — in that case the sanitized position is the only safe signal we have.
    // 本体抽出で元の位置を保つため raw 行の `{` 列を優先し、raw 側で文字列リテラル内に隠れて
    // いる場合のみサニタイズ済み行の位置にフォールバックする。
    private static int ResolveCSharpBraceColumn(string rawLine, string sanitizedLine)
    {
        var rawColumn = rawLine.IndexOf('{');
        if (rawColumn >= 0)
            return rawColumn;

        return sanitizedLine.IndexOf('{');
    }

    private static bool HasCSharpPropertyAccessorStart(string text)
    {
        var braceIndex = text.IndexOf('{');
        if (braceIndex < 0)
            return false;

        var cursor = SkipWhitespace(text, braceIndex + 1);
        while (TrySkipCSharpAttributeList(text, ref cursor))
            cursor = SkipWhitespace(text, cursor);

        cursor = SkipWhitespace(text, cursor);
        if (TrySkipCSharpAccessorAccessibility(text, ref cursor))
            cursor = SkipWhitespace(text, cursor);
        while (TrySkipCSharpAccessorModifier(text, ref cursor))
            cursor = SkipWhitespace(text, cursor);

        return StartsWithCSharpAccessorKeyword(text, cursor);
    }

    private static bool HasCSharpEventAccessorStart(string text)
    {
        var braceIndex = text.IndexOf('{');
        if (braceIndex < 0)
            return false;

        var cursor = SkipWhitespace(text, braceIndex + 1);
        while (TrySkipCSharpAttributeList(text, ref cursor))
            cursor = SkipWhitespace(text, cursor);

        return StartsWithCSharpEventAccessorKeyword(text, cursor);
    }

    private static bool ShouldDeferCSharpFunctionSameLineAdvance(string matchLine, int startColumn)
    {
        if (startColumn < 0 || startColumn >= matchLine.Length)
            return false;

        var remaining = matchLine[startColumn..];
        return !CSharpTypeBodyDeclarationMarker.IsMatch(remaining)
            && (CSharpSameLinePropertyStatementStartRegex.IsMatch(remaining)
                || CSharpSameLineEventOrDelegateStatementStartRegex.IsMatch(remaining));
    }

    private static bool HasCSharpTokenBeforeIndex(string text, string token, int exclusiveEnd)
    {
        if (string.IsNullOrEmpty(token) || exclusiveEnd <= 0)
            return false;

        if (exclusiveEnd > text.Length)
            exclusiveEnd = text.Length;

        var searchStart = 0;
        while (searchStart < exclusiveEnd)
        {
            var remaining = exclusiveEnd - searchStart;
            if (remaining <= 0)
                return false;

            var tokenIndex = text.IndexOf(token, searchStart, remaining, StringComparison.Ordinal);
            if (tokenIndex < 0)
                return false;

            var tokenEnd = tokenIndex + token.Length;
            if ((tokenIndex == 0 || !char.IsLetterOrDigit(text[tokenIndex - 1]) && text[tokenIndex - 1] != '_')
                && (tokenEnd >= text.Length || !char.IsLetterOrDigit(text[tokenEnd]) && text[tokenEnd] != '_'))
            {
                return true;
            }

            searchStart = tokenIndex + 1;
        }

        return false;
    }

    private static bool ShouldDeferCSharpBracePropertySameLineAdvance(string matchLine, int startColumn)
    {
        if (startColumn < 0 || startColumn >= matchLine.Length)
            return false;

        var remaining = matchLine[startColumn..];
        return !CSharpTypeBodyDeclarationMarker.IsMatch(remaining)
            && !HasCSharpPropertyAccessorStart(remaining)
            && CSharpSameLinePropertyStatementStartRegex.IsMatch(remaining);
    }

    private static bool ShouldDeferCSharpEventOrDelegateSameLineAdvance(string matchLine, int startColumn, string kind)
    {
        if (startColumn < 0 || startColumn >= matchLine.Length)
            return false;

        var remaining = matchLine[startColumn..];
        if (CSharpTypeBodyDeclarationMarker.IsMatch(remaining))
            return false;

        return kind switch
        {
            "event" => CSharpSameLineDelegateStatementStartRegex.IsMatch(remaining),
            "delegate" => CSharpSameLineEventStatementStartRegex.IsMatch(remaining),
            _ => false,
        };
    }

    private static bool TryGetCSharpSameLineSemicolonSiblingOffset(string matchLine, int startColumn, out int nextSameLineOffset)
    {
        nextSameLineOffset = -1;
        if (startColumn < 0 || startColumn >= matchLine.Length)
            return false;

        var statementEnd = FindCSharpSameLineStatementEnd(matchLine, startColumn);
        if (statementEnd <= startColumn)
            return false;

        var nextOffset = FindNextSameLineNonClosingBraceStatementStart(matchLine, statementEnd, "csharp");
        if (nextOffset <= statementEnd
            || nextOffset >= matchLine.Length)
        {
            return false;
        }

        nextSameLineOffset = nextOffset;
        return true;
    }

    private static bool TryGetCSharpSameLineEventSiblingOffset(string matchLine, int startColumn, out int nextSameLineOffset)
    {
        nextSameLineOffset = -1;
        if (startColumn < 0 || startColumn >= matchLine.Length)
            return false;

        var remaining = matchLine[startColumn..];
        if (!CSharpSameLineEventStatementStartRegex.IsMatch(remaining)
            || !HasCSharpEventAccessorStart(remaining))
            return false;

        var bodyEnd = FindCSharpSameLineBraceEndColumnFromSanitized(matchLine, startColumn);
        if (bodyEnd < startColumn)
            return false;

        var nextOffset = FindNextSameLineNonClosingBraceStatementStart(matchLine, bodyEnd + 1, "csharp");
        if (nextOffset <= bodyEnd
            || nextOffset >= matchLine.Length)
        {
            return false;
        }

        nextSameLineOffset = nextOffset;
        return true;
    }

    private static bool StartsWithCSharpEventAccessorKeyword(string text, int start)
    {
        return StartsWithCSharpEventAccessorKeyword(text, start, "add")
            || StartsWithCSharpEventAccessorKeyword(text, start, "remove");
    }

    private static bool StartsWithCSharpEventAccessorKeyword(string text, int start, string keyword)
    {
        if (start < 0)
            return false;
        if (start + keyword.Length > text.Length)
            return false;
        if (!text.AsSpan(start, keyword.Length).SequenceEqual(keyword))
            return false;

        var end = start + keyword.Length;
        return end >= text.Length || !char.IsLetterOrDigit(text[end]) && text[end] != '_';
    }

    private static bool HasCSharpTopLevelFieldInitializer(string text)
    {
        int paren = 0, bracket = 0, brace = 0;
        for (int i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            switch (ch)
            {
                case '(':
                    paren++;
                    continue;
                case ')' when paren > 0:
                    paren--;
                    continue;
                case '[':
                    bracket++;
                    continue;
                case ']' when bracket > 0:
                    bracket--;
                    continue;
                case '{':
                    brace++;
                    continue;
                case '}' when brace > 0:
                    brace--;
                    continue;
                case '=' when paren == 0 && bracket == 0 && brace == 0:
                    var previous = i > 0 ? text[i - 1] : '\0';
                    var next = i + 1 < text.Length ? text[i + 1] : '\0';
                    if (previous is not ('=' or '!' or '<' or '>')
                        && next is not ('=' or '>'))
                    {
                        return true;
                    }

                    continue;
            }
        }

        return false;
    }

    private static int SkipWhitespace(string text, int cursor)
    {
        while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
            cursor++;

        return cursor;
    }

    private static bool TrySkipCSharpAttributeList(string text, ref int cursor)
    {
        var start = SkipWhitespace(text, cursor);
        if (start >= text.Length || text[start] != '[')
            return false;

        var depth = 0;
        var current = start;
        while (current < text.Length)
        {
            var ch = text[current++];
            if (ch == '[')
            {
                depth++;
            }
            else if (ch == ']')
            {
                depth--;
                if (depth == 0)
                {
                    cursor = current;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TrySkipCSharpAccessorAccessibility(string text, ref int cursor)
    {
        foreach (var modifier in new[] { "protected internal", "private protected", "protected", "internal", "private", "public" })
        {
            if (StartsWithWord(text, cursor, modifier))
            {
                cursor += modifier.Length;
                return true;
            }
        }

        return false;
    }

    private static bool TrySkipCSharpAccessorModifier(string text, ref int cursor)
    {
        if (!StartsWithWord(text, cursor, "readonly"))
            return false;

        cursor += "readonly".Length;
        return true;
    }

    private static bool StartsWithCSharpAccessorKeyword(string text, int cursor) =>
        StartsWithWord(text, cursor, "get")
        || StartsWithWord(text, cursor, "set")
        || StartsWithWord(text, cursor, "init");

    private static bool StartsWithWord(string text, int cursor, string word)
    {
        if (cursor < 0 || cursor + word.Length > text.Length)
            return false;

        if (!text.AsSpan(cursor, word.Length).SequenceEqual(word.AsSpan()))
            return false;

        var end = cursor + word.Length;
        return end >= text.Length || !char.IsLetterOrDigit(text[end]) && text[end] != '_';
    }

    private static StringBuilder BuildCSharpAccessorProbeBuilder(
        string[] csharpMatchLines,
        int openBraceLineIndex,
        int openBraceExclusiveEndColumn,
        int endLineIndex)
    {
        var builder = new StringBuilder();
        var openBraceColumn = Math.Max(0, openBraceExclusiveEndColumn - 1);
        for (int i = openBraceLineIndex; i <= endLineIndex && i < csharpMatchLines.Length; i++)
        {
            AppendCSharpAccessorProbeLine(
                builder,
                csharpMatchLines[i],
                i == openBraceLineIndex ? openBraceColumn : null);
        }

        return builder;
    }

    private static void AppendCSharpAccessorProbeLine(StringBuilder builder, string sanitizedLine, int? startColumn)
    {
        var start = Math.Clamp(startColumn ?? 0, 0, sanitizedLine.Length);
        var trimmed = sanitizedLine[start..].Trim();
        if (trimmed.Length == 0)
            return;

        if (builder.Length > 0)
            builder.Append(' ');
        builder.Append(trimmed);
    }

    private static CSharpAccessorProbeStatus ClassifyCSharpAccessorProbe(string text)
    {
        var braceIndex = text.IndexOf('{');
        if (braceIndex < 0)
            return CSharpAccessorProbeStatus.Rejected;

        var cursor = SkipWhitespace(text, braceIndex + 1);
        while (true)
        {
            while (TrySkipCSharpAttributeList(text, ref cursor))
                cursor = SkipWhitespace(text, cursor);

            if (cursor >= text.Length)
                return CSharpAccessorProbeStatus.Pending;

            if (TrySkipCSharpAccessorAccessibility(text, ref cursor))
            {
                cursor = SkipWhitespace(text, cursor);
                if (cursor >= text.Length)
                    return CSharpAccessorProbeStatus.Pending;
            }
            while (TrySkipCSharpAccessorModifier(text, ref cursor))
            {
                cursor = SkipWhitespace(text, cursor);
                if (cursor >= text.Length)
                    return CSharpAccessorProbeStatus.Pending;
            }

            break;
        }

        return StartsWithCSharpAccessorKeyword(text, cursor)
            ? CSharpAccessorProbeStatus.Found
            : CSharpAccessorProbeStatus.Rejected;
    }

    private static bool IsStandaloneCSharpAccessorCandidate(string text) =>
        CSharpStandaloneAccessorRegex.IsMatch(text);

    private struct CSharpTopLevelSemicolonTracker
    {
        private int _parenDepth;
        private int _bracketDepth;
        private int _braceDepth;

        public bool HasTopLevelSemicolon { get; private set; }

        public void Scan(string text)
        {
            if (HasTopLevelSemicolon)
                return;

            for (int i = 0; i < text.Length; i++)
            {
                switch (text[i])
                {
                    case '(':
                        _parenDepth++;
                        break;
                    case ')' when _parenDepth > 0:
                        _parenDepth--;
                        break;
                    case '[':
                        _bracketDepth++;
                        break;
                    case ']' when _bracketDepth > 0:
                        _bracketDepth--;
                        break;
                    case '{':
                        _braceDepth++;
                        break;
                    case '}' when _braceDepth > 0:
                        _braceDepth--;
                        break;
                    case ';' when _parenDepth == 0 && _bracketDepth == 0 && _braceDepth == 0:
                        HasTopLevelSemicolon = true;
                        return;
                }
            }
        }
    }

    private static bool TryFindCSharpExpressionArrow(string[] lines, int startLineIndex, int endLineIndex, out int arrowLineIndex, out int arrowColumn)
    {
        var lexState = new CSharpLexState();
        for (int i = startLineIndex; i <= endLineIndex && i < lines.Length; i++)
        {
            var lexedLine = LexCSharpLine(lines[i], lexState);
            lexState = lexedLine.EndState;
            var column = lexedLine.SanitizedLine.IndexOf("=>", StringComparison.Ordinal);
            if (column >= 0)
            {
                arrowLineIndex = i;
                arrowColumn = column;
                return true;
            }
        }

        arrowLineIndex = -1;
        arrowColumn = -1;
        return false;
    }

    private static bool IsCSharpMultilineExpressionBodiedMember(string[] lines, int startLineIndex, int startColumn)
    {
        var lexState = new CSharpLexState();
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;

        for (int i = startLineIndex; i < lines.Length; i++)
        {
            var lexedLine = LexCSharpLine(lines[i], lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;
            var fromColumn = i == startLineIndex
                ? Math.Min(Math.Max(0, startColumn), sanitizedLine.Length)
                : 0;

            for (int column = fromColumn; column < sanitizedLine.Length; column++)
            {
                var ch = sanitizedLine[column];
                switch (ch)
                {
                    case '(':
                        parenDepth++;
                        break;
                    case ')' when parenDepth > 0:
                        parenDepth--;
                        break;
                    case '[':
                        bracketDepth++;
                        break;
                    case ']' when bracketDepth > 0:
                        bracketDepth--;
                        break;
                    case '{' when parenDepth == 0 && bracketDepth == 0 && braceDepth == 0:
                        return false;
                    case '{':
                        braceDepth++;
                        break;
                    case '}' when braceDepth > 0:
                        braceDepth--;
                        break;
                    case ';' when parenDepth == 0 && bracketDepth == 0 && braceDepth == 0:
                        return false;
                    case '=' when parenDepth == 0
                        && bracketDepth == 0
                        && braceDepth == 0
                        && column + 1 < sanitizedLine.Length
                        && sanitizedLine[column + 1] == '>':
                        return true;
                }
            }
        }

        return false;
    }

    private static int FindCSharpExpressionBodyEndLine(string[] lines, int arrowLineIndex, int arrowColumn)
    {
        var lexState = new CSharpLexState();
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;

        for (int i = arrowLineIndex; i < lines.Length; i++)
        {
            var lexedLine = LexCSharpLine(lines[i], lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;
            var startColumn = i == arrowLineIndex
                ? Math.Min(sanitizedLine.Length, arrowColumn + 2)
                : 0;

            for (int column = startColumn; column < sanitizedLine.Length; column++)
            {
                var ch = sanitizedLine[column];
                switch (ch)
                {
                    case '(':
                        parenDepth++;
                        break;
                    case ')' when parenDepth > 0:
                        parenDepth--;
                        break;
                    case '[':
                        bracketDepth++;
                        break;
                    case ']' when bracketDepth > 0:
                        bracketDepth--;
                        break;
                    case '{':
                        braceDepth++;
                        break;
                    case '}' when braceDepth > 0:
                        braceDepth--;
                        break;
                    case ';' when parenDepth == 0 && bracketDepth == 0 && braceDepth == 0:
                        return i;
                }
            }
        }

        return arrowLineIndex;
    }

    private static string BuildCSharpMultilineSignature(
        string[] lines,
        int startLineIndex,
        int startColumn,
        int signatureLastLineIndex,
        int? signatureLastLineExclusiveEndColumn = null)
    {
        var builder = new StringBuilder(lines[startLineIndex].Length);
        builder.Append(lines[startLineIndex][startColumn..].TrimEnd());

        for (int i = startLineIndex + 1; i <= signatureLastLineIndex && i < lines.Length; i++)
        {
            var slice = i == signatureLastLineIndex && signatureLastLineExclusiveEndColumn.HasValue
                ? lines[i][..Math.Min(signatureLastLineExclusiveEndColumn.Value, lines[i].Length)]
                : lines[i];
            var trimmed = slice.Trim();
            if (trimmed.Length == 0)
                continue;

            if (builder.Length > 0)
                builder.Append(' ');
            builder.Append(trimmed);
        }

        return builder.ToString().Trim();
    }

    private static bool TryFindCSharpSemicolonTerminatedSignatureExtent(
        string[] lines,
        int startLineIndex,
        int startColumn,
        out int lastLineIndex,
        out int? lastLineExclusiveEndColumn)
    {
        var lexState = new CSharpLexState();
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;

        for (int i = startLineIndex; i < lines.Length; i++)
        {
            var lexedLine = LexCSharpLine(lines[i], lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;
            var fromColumn = i == startLineIndex
                ? Math.Min(Math.Max(0, startColumn), sanitizedLine.Length)
                : 0;

            for (int column = fromColumn; column < sanitizedLine.Length; column++)
            {
                switch (sanitizedLine[column])
                {
                    case '(':
                        parenDepth++;
                        break;
                    case ')' when parenDepth > 0:
                        parenDepth--;
                        break;
                    case '[':
                        bracketDepth++;
                        break;
                    case ']' when bracketDepth > 0:
                        bracketDepth--;
                        break;
                    case '{':
                        braceDepth++;
                        break;
                    case '}' when braceDepth > 0:
                        braceDepth--;
                        break;
                    case '}' when parenDepth == 0 && bracketDepth == 0 && braceDepth == 0:
                        lastLineIndex = i;
                        lastLineExclusiveEndColumn = column;
                        return true;
                    case ';' when parenDepth == 0 && bracketDepth == 0 && braceDepth == 0:
                        lastLineIndex = i;
                        lastLineExclusiveEndColumn = column + 1;
                        return true;
                }
            }
        }

        lastLineIndex = startLineIndex;
        lastLineExclusiveEndColumn = null;
        return false;
    }

    // Scan forward from a C# type declaration header (`class` / `struct` / `interface` /
    // `enum`) and find where the header ends — either at the body-opening `{` or the
    // primary-constructor / forward-declaration terminator `;`. Returns the line index
    // and column of that terminator so the signature builder can concatenate the header
    // lines up to (but not including) the terminator. Respects paren depth for primary
    // ctors, bracket depth for attributes on type / generic parameters, and uses the
    // same lexer as the rest of the C# path so that `{` / `;` inside string literals,
    // comments, or verbatim / raw strings do not short-circuit the scan. A line cap
    // prevents runaway scans on unterminated input. Closes #382.
    //
    // C# 型宣言ヘッダ（`class` / `struct` / `interface` / `enum`）の終端位置を探す。
    // 本体開きの `{` か、primary ctor / 前方宣言の `;` を終端とする。終端行と列を
    // 返すので、シグネチャ組立側はその直前までを連結できる。primary ctor 用の
    // 括弧深度、型 / ジェネリック引数へのアトリビュート用の角括弧深度を追跡し、
    // 文字列リテラル、コメント、verbatim / raw 文字列の中の `{` / `;` を
    // 誤検出しないよう、他の C# 経路と同じ lexer を共有する。未終端入力に対する
    // 暴走防止に行数上限を設ける。Closes #382.
    private const int CSharpTypeHeaderLookaheadLineLimit = 64;

    private static bool TryFindCSharpTypeHeaderExtent(
        string[] lines,
        int startLineIndex,
        int startColumn,
        out int lastLineIndex,
        out int? lastLineExclusiveEndColumn)
    {
        var parenDepth = 0;
        var bracketDepth = 0;

        var limit = Math.Min(lines.Length, startLineIndex + CSharpTypeHeaderLookaheadLineLimit);
        for (int i = startLineIndex; i < limit; i++)
        {
            var sanitizedLine = lines[i];
            var fromColumn = i == startLineIndex ? startColumn : 0;

            for (int column = fromColumn; column < sanitizedLine.Length; column++)
            {
                var ch = sanitizedLine[column];
                if (ch == '/' && column + 1 < sanitizedLine.Length && sanitizedLine[column + 1] == '/')
                    break;

                switch (ch)
                {
                    case '(':
                        parenDepth++;
                        break;
                    case ')' when parenDepth > 0:
                        parenDepth--;
                        break;
                    case '[':
                        bracketDepth++;
                        break;
                    case ']' when bracketDepth > 0:
                        bracketDepth--;
                        break;
                    case '{' when parenDepth == 0 && bracketDepth == 0:
                        lastLineIndex = i;
                        lastLineExclusiveEndColumn = column;
                        return true;
                    case ';' when parenDepth == 0 && bracketDepth == 0:
                        lastLineIndex = i;
                        lastLineExclusiveEndColumn = column;
                        return true;
                }
            }
        }

        lastLineIndex = -1;
        lastLineExclusiveEndColumn = null;
        return false;
    }

    // Build a multi-line C# type-header signature like BuildCSharpMultilineSignature, but
    // strip inline `//` and `/* ... */` comments that would otherwise leak into the stored
    // `symbols.signature` when a base list or `where` clause has a trailing or interleaved
    // comment. Uses the shared C# lexer so comment boundaries cannot be confused by `//`
    // or `/*` characters inside string, char, verbatim, or raw string literals. String
    // content is preserved (primary constructor default arguments, etc.). Closes #382.
    //
    // BuildCSharpMultilineSignature と同じく折り返された C# 型ヘッダを連結する。ただし
    // base リストや `where` 句に混ざる `//` / `/* ... */` コメントを除去し、保存される
    // `symbols.signature` に漏れないようにする。`//` / `/*` が文字列リテラル・char・
    // verbatim・raw 文字列内にある場合を誤検出しないよう共有 C# lexer を使う。文字列の
    // 中身（primary constructor のデフォルト引数など）はそのまま保持する。Closes #382.
    private static string BuildCSharpTypeHeaderSignature(
        string[] lines,
        int startLineIndex,
        int startColumn,
        int lastLineIndex,
        int? lastLineExclusiveEndColumn)
    {
        // Assemble the raw slice preserving '\n' between physical lines so multi-line raw
        // and verbatim string literals keep their newlines and leading indentation. The
        // dedicated sanitizer handles lex mode (Code / String / Verbatim / Raw / Char /
        // LineComment / BlockComment) and interpolation holes in one pass.
        // 物理行を跨ぐとき `\n` をそのまま入れてスライスを組み立て、multi-line raw や
        // verbatim 文字列リテラルの改行と行頭インデントを保持する。専用サニタイザが
        // lex モード（Code / String / Verbatim / Raw / Char / LineComment / BlockComment）
        // と補間ホールを 1 パスで処理する。
        var rawSlice = new StringBuilder();
        for (int i = startLineIndex; i <= lastLineIndex && i < lines.Length; i++)
        {
            var line = lines[i];
            // Content was split on '\n', so CRLF lines carry a trailing '\r'. Trim it so the
            // inter-line separator stays '\n' regardless of source-file line endings.
            // content は '\n' で分割しているため、CRLF 行は末尾に '\r' が残る。行間を
            // 必ず '\n' に揃えるため末尾の '\r' を落とす。
            int length = line.Length;
            if (length > 0 && line[length - 1] == '\r')
                length--;
            int from = i == startLineIndex ? Math.Clamp(startColumn, 0, length) : 0;
            int to = i == lastLineIndex && lastLineExclusiveEndColumn.HasValue
                ? Math.Clamp(lastLineExclusiveEndColumn.Value, 0, length)
                : length;
            if (to < from) to = from;
            if (i > startLineIndex)
                rawSlice.Append('\n');
            if (from < to)
                rawSlice.Append(line, from, to - from);
        }

        var sanitized = SanitizeCSharpTypeHeaderSlice(rawSlice.ToString()).Trim();
        return NormalizeCSharpConstraintGenericWhitespace(sanitized);
    }

    private static string NormalizeCSharpConstraintGenericWhitespace(string signature)
    {
        var whereIndex = FindCSharpWhereConstraintToken(signature);
        if (whereIndex < 0)
            return signature;

        var prefix = signature[..whereIndex];
        var constraints = CollapseCSharpGenericTypeWhitespace(signature[whereIndex..]);
        return prefix + constraints;
    }

    private static int FindCSharpWhereConstraintToken(string signature)
    {
        var lexState = new CSharpLexState();
        var lineStart = 0;

        while (lineStart <= signature.Length)
        {
            var lineEnd = signature.IndexOf('\n', lineStart);
            if (lineEnd < 0)
                lineEnd = signature.Length;

            var line = signature[lineStart..lineEnd];
            var lexedLine = LexCSharpLine(line, lexState);
            lexState = lexedLine.EndState;
            var sanitizedLine = lexedLine.SanitizedLine;

            for (int i = 0; i < sanitizedLine.Length; i++)
            {
                if (!sanitizedLine.AsSpan(i).StartsWith("where".AsSpan(), StringComparison.Ordinal))
                    continue;

                var absoluteIndex = lineStart + i;
                var before = absoluteIndex == 0 ? '\0' : signature[absoluteIndex - 1];
                var afterIndex = absoluteIndex + "where".Length;
                var after = afterIndex < signature.Length ? signature[afterIndex] : '\0';
                if (!IsCSharpWhereTokenBoundary(before) || !IsCSharpWhereTokenBoundary(after))
                    continue;
                if (!LooksLikeCSharpConstraintClause(signature, absoluteIndex))
                    continue;

                return absoluteIndex;
            }

            if (lineEnd == signature.Length)
                break;

            lineStart = lineEnd + 1;
        }

        return -1;
    }

    private static bool IsCSharpWhereTokenBoundary(char ch)
        => ch == '\0' || !(char.IsLetterOrDigit(ch) || ch == '_' || ch == '@');

    private static bool LooksLikeCSharpConstraintClause(string signature, int whereIndex)
    {
        var i = whereIndex + "where".Length;
        while (i < signature.Length && char.IsWhiteSpace(signature[i]))
            i++;

        if (i >= signature.Length)
            return false;

        if (signature[i] == '@')
            i++;

        if (i >= signature.Length || !IsCSharpConstraintIdentifierStart(signature[i]))
            return false;

        i++;
        while (i < signature.Length && IsCSharpConstraintIdentifierPart(signature[i]))
            i++;

        while (i < signature.Length && char.IsWhiteSpace(signature[i]))
            i++;

        return i < signature.Length && signature[i] == ':';
    }

    private static bool IsCSharpConstraintIdentifierStart(char ch)
        => char.IsLetter(ch) || ch == '_';

    private static bool IsCSharpConstraintIdentifierPart(char ch)
        => char.IsLetterOrDigit(ch) || ch == '_';

    private enum CSharpHeaderFrameKind
    {
        Code,
        LineComment,
        BlockComment,
        String,
        Verbatim,
        Raw,
        Char,
    }

    private struct CSharpHeaderFrame
    {
        public CSharpHeaderFrameKind Kind;
        public bool Interpolated;   // String / Verbatim / Raw: true if $-prefixed.
        public int DollarCount;     // Raw: number of '$' prefixes; also the '{' count needed to open a hole.
        public int QuoteCount;      // Raw: number of '"' in the opening delimiter; same run closes the string.
        public int HoleBraceDepth;  // Code frame inside an interpolation hole: counts nested '{' depth. 0 means next unmatched '}' exits the hole.
        public bool EscapeNext;     // String / Char: true if a preceding backslash awaits its escaped char.
    }

    // Sanitize a C# type header slice: strip `//` line comments and `/* ... */` block
    // comments, collapse runs of Code-mode whitespace (including '\n' between lines) to a
    // single space, preserve all String / Verbatim / Raw / Char literal contents verbatim
    // (including literal whitespace runs, line breaks inside raw / verbatim strings, and
    // escape sequences), and keep interpolation holes (`$"{expr}"`, `$@"{expr}"`, raw
    // `$"""{expr}"""` / `$$"""{{expr}}"""`) correctly classified as Code-mode content so
    // whitespace inside holes is collapsed while literal content outside holes is not.
    // Closes #382.
    //
    // C# 型ヘッダスライスのサニタイザ: `//` 行コメントと `/* ... */` ブロックコメントを
    // 除去し、Code モードの空白列（行間の `\n` も含む）を 1 つのスペースに畳み、String /
    // Verbatim / Raw / Char リテラルの中身（リテラル内の空白、raw / verbatim の行末改行、
    // エスケープ列）は verbatim に残し、補間ホール（`$"{expr}"`、`$@"{expr}"`、raw
    // `$"""{expr}"""` / `$$"""{{expr}}"""`）内部は Code モードとして分類してホール内の
    // 空白だけを畳み、ホール外のリテラル内容は畳まないようにする。Closes #382.
    private static string SanitizeCSharpTypeHeaderSlice(string input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        var output = new StringBuilder(input.Length);
        var stack = new Stack<CSharpHeaderFrame>();
        stack.Push(new CSharpHeaderFrame { Kind = CSharpHeaderFrameKind.Code });
        bool prevWasCodeSpace = false;
        int i = 0;

        while (i < input.Length)
        {
            var frame = stack.Peek();
            var ch = input[i];
            var next = i + 1 < input.Length ? input[i + 1] : '\0';

            if (frame.Kind == CSharpHeaderFrameKind.LineComment)
            {
                // `//` swallows everything to the next '\n', which then flows through
                // Code-mode whitespace handling as a single space. `//` は次の '\n' まで
                // 食いつぶし、'\n' は Code モードで 1 スペースに畳まれる。
                if (ch == '\n')
                {
                    stack.Pop();
                    continue;
                }
                i++;
                continue;
            }

            if (frame.Kind == CSharpHeaderFrameKind.BlockComment)
            {
                if (ch == '*' && next == '/')
                {
                    stack.Pop();
                    i += 2;
                    continue;
                }
                i++;
                continue;
            }

            if (frame.Kind == CSharpHeaderFrameKind.String)
            {
                output.Append(ch);
                prevWasCodeSpace = false;

                if (frame.EscapeNext)
                {
                    frame.EscapeNext = false;
                    stack.Pop();
                    stack.Push(frame);
                    i++;
                    continue;
                }
                if (ch == '\\')
                {
                    frame.EscapeNext = true;
                    stack.Pop();
                    stack.Push(frame);
                    i++;
                    continue;
                }
                if (ch == '{' && frame.Interpolated)
                {
                    if (next == '{')
                    {
                        // `{{` is a literal brace inside an interpolated string.
                        // `{{` は補間文字列内のリテラル波括弧エスケープ。
                        output.Append(next);
                        i += 2;
                        continue;
                    }
                    stack.Push(new CSharpHeaderFrame { Kind = CSharpHeaderFrameKind.Code });
                    i++;
                    continue;
                }
                if (ch == '}' && frame.Interpolated && next == '}')
                {
                    output.Append(next);
                    i += 2;
                    continue;
                }
                if (ch == '"')
                {
                    stack.Pop();
                    i++;
                    continue;
                }
                i++;
                continue;
            }

            if (frame.Kind == CSharpHeaderFrameKind.Verbatim)
            {
                output.Append(ch);
                prevWasCodeSpace = false;

                if (ch == '"' && next == '"')
                {
                    // `""` is a literal '"' escape inside a verbatim string.
                    // verbatim 文字列内の `""` はリテラル '"' エスケープ。
                    output.Append(next);
                    i += 2;
                    continue;
                }
                if (ch == '{' && frame.Interpolated)
                {
                    if (next == '{')
                    {
                        output.Append(next);
                        i += 2;
                        continue;
                    }
                    stack.Push(new CSharpHeaderFrame { Kind = CSharpHeaderFrameKind.Code });
                    i++;
                    continue;
                }
                if (ch == '}' && frame.Interpolated && next == '}')
                {
                    output.Append(next);
                    i += 2;
                    continue;
                }
                if (ch == '"')
                {
                    stack.Pop();
                    i++;
                    continue;
                }
                i++;
                continue;
            }

            if (frame.Kind == CSharpHeaderFrameKind.Raw)
            {
                // In a raw string, a hole opens on a run of '{' at least DollarCount long
                // (for $$-prefixed raw strings, {{ is literal, only {{{ opens), and the
                // string closes on a run of '"' at least QuoteCount long.
                // raw 文字列では、`{` が DollarCount 個以上並んでいればホール開始、
                // 不足するならリテラルの波括弧。`"` が QuoteCount 個以上並べば文字列終端。
                if (ch == '{' && frame.Interpolated && frame.DollarCount > 0)
                {
                    int runLen = 0;
                    while (i + runLen < input.Length && input[i + runLen] == '{')
                        runLen++;
                    if (runLen >= frame.DollarCount)
                    {
                        for (int k = 0; k < frame.DollarCount; k++)
                            output.Append('{');
                        i += frame.DollarCount;
                        stack.Push(new CSharpHeaderFrame { Kind = CSharpHeaderFrameKind.Code });
                        prevWasCodeSpace = false;
                        continue;
                    }
                    for (int k = 0; k < runLen; k++)
                        output.Append('{');
                    i += runLen;
                    prevWasCodeSpace = false;
                    continue;
                }
                if (ch == '"')
                {
                    int runLen = 0;
                    while (i + runLen < input.Length && input[i + runLen] == '"')
                        runLen++;
                    if (runLen >= frame.QuoteCount)
                    {
                        for (int k = 0; k < frame.QuoteCount; k++)
                            output.Append('"');
                        i += frame.QuoteCount;
                        stack.Pop();
                        prevWasCodeSpace = false;
                        continue;
                    }
                    for (int k = 0; k < runLen; k++)
                        output.Append('"');
                    i += runLen;
                    prevWasCodeSpace = false;
                    continue;
                }
                output.Append(ch);
                prevWasCodeSpace = false;
                i++;
                continue;
            }

            if (frame.Kind == CSharpHeaderFrameKind.Char)
            {
                output.Append(ch);
                prevWasCodeSpace = false;

                if (frame.EscapeNext)
                {
                    frame.EscapeNext = false;
                    stack.Pop();
                    stack.Push(frame);
                    i++;
                    continue;
                }
                if (ch == '\\')
                {
                    frame.EscapeNext = true;
                    stack.Pop();
                    stack.Push(frame);
                    i++;
                    continue;
                }
                if (ch == '\'')
                {
                    stack.Pop();
                    i++;
                    continue;
                }
                i++;
                continue;
            }

            // Code mode (root code or an open interpolation hole).
            // Code モード（ルート コード または 開いている補間ホール）。
            if (ch == '}' && stack.Count > 1 && frame.HoleBraceDepth == 0)
            {
                stack.Pop();
                output.Append(ch);
                prevWasCodeSpace = false;
                i++;
                continue;
            }
            if (ch == '{' && stack.Count > 1)
            {
                frame.HoleBraceDepth++;
                stack.Pop();
                stack.Push(frame);
                output.Append(ch);
                prevWasCodeSpace = false;
                i++;
                continue;
            }
            if (ch == '}' && stack.Count > 1)
            {
                frame.HoleBraceDepth--;
                stack.Pop();
                stack.Push(frame);
                output.Append(ch);
                prevWasCodeSpace = false;
                i++;
                continue;
            }

            if (ch == '/' && next == '/')
            {
                stack.Push(new CSharpHeaderFrame { Kind = CSharpHeaderFrameKind.LineComment });
                if (!prevWasCodeSpace)
                {
                    output.Append(' ');
                    prevWasCodeSpace = true;
                }
                i += 2;
                continue;
            }
            if (ch == '/' && next == '*')
            {
                stack.Push(new CSharpHeaderFrame { Kind = CSharpHeaderFrameKind.BlockComment });
                if (!prevWasCodeSpace)
                {
                    output.Append(' ');
                    prevWasCodeSpace = true;
                }
                i += 2;
                continue;
            }

            if (TryReadCSharpRawStringStart(input, i, out var rawPrefixLength, out var rawDelimiterLength))
            {
                int total = rawPrefixLength + rawDelimiterLength;
                for (int k = 0; k < total && i + k < input.Length; k++)
                    output.Append(input[i + k]);
                stack.Push(new CSharpHeaderFrame
                {
                    Kind = CSharpHeaderFrameKind.Raw,
                    Interpolated = rawPrefixLength > 0,
                    DollarCount = rawPrefixLength,
                    QuoteCount = rawDelimiterLength,
                });
                i += total;
                prevWasCodeSpace = false;
                continue;
            }

            if (ch == '$' && next == '@' && i + 2 < input.Length && input[i + 2] == '"')
            {
                output.Append(ch);
                output.Append(next);
                output.Append(input[i + 2]);
                stack.Push(new CSharpHeaderFrame { Kind = CSharpHeaderFrameKind.Verbatim, Interpolated = true });
                i += 3;
                prevWasCodeSpace = false;
                continue;
            }
            if (ch == '@' && next == '$' && i + 2 < input.Length && input[i + 2] == '"')
            {
                output.Append(ch);
                output.Append(next);
                output.Append(input[i + 2]);
                stack.Push(new CSharpHeaderFrame { Kind = CSharpHeaderFrameKind.Verbatim, Interpolated = true });
                i += 3;
                prevWasCodeSpace = false;
                continue;
            }
            if (ch == '@' && next == '"')
            {
                output.Append(ch);
                output.Append(next);
                stack.Push(new CSharpHeaderFrame { Kind = CSharpHeaderFrameKind.Verbatim, Interpolated = false });
                i += 2;
                prevWasCodeSpace = false;
                continue;
            }
            if (ch == '$' && next == '"')
            {
                output.Append(ch);
                output.Append(next);
                stack.Push(new CSharpHeaderFrame { Kind = CSharpHeaderFrameKind.String, Interpolated = true });
                i += 2;
                prevWasCodeSpace = false;
                continue;
            }
            if (ch == '"')
            {
                output.Append(ch);
                stack.Push(new CSharpHeaderFrame { Kind = CSharpHeaderFrameKind.String, Interpolated = false });
                i++;
                prevWasCodeSpace = false;
                continue;
            }
            if (ch == '\'')
            {
                output.Append(ch);
                stack.Push(new CSharpHeaderFrame { Kind = CSharpHeaderFrameKind.Char });
                i++;
                prevWasCodeSpace = false;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (!prevWasCodeSpace)
                {
                    output.Append(' ');
                    prevWasCodeSpace = true;
                }
                i++;
                continue;
            }

            output.Append(ch);
            prevWasCodeSpace = false;
            i++;
        }

        return output.ToString();
    }

    private static string CollapseCSharpGenericTypeWhitespace(string line)
        => CollapseCSharpGenericTypeWhitespace(line, out _);

    // Collapse only the whitespace that sits between generic type-argument angle brackets
    // so patterns like `Dictionary<string, int>` normalize to `Dictionary<string,int>`.
    // Preserve the separator space between a tuple element type and its element name so
    // `Dictionary<string, (int x, int y)>` still normalizes to a readable tuple shape
    // instead of merging the tokens into `intx` / `inty`.
    // tuple 要素の型と要素名の間にある区切り空白だけは残し、
    // `Dictionary<string, (int x, int y)>` が `intx` / `inty` に潰れないようにする。
    // Also emits a column-mapping array so callers can translate a column in the collapsed
    // string back to the corresponding column in the raw source. `collapsedToRaw[c]` is
    // the raw index of the character at collapsed column `c`; the final element
    // (`collapsedToRaw[collapsed.Length]`) is the sentinel `raw.Length`, which lets
    // translation use exclusive-end indices safely. When nothing collapses (early return
    // path), the map is emitted as `null` to signal identity — callers fall back to the
    // original collapsed column in that case. Closes #400.
    // ジェネリック型引数の `<...>` 内部の空白だけを取り除き、`Dictionary<string, int>` の
    // ような型を `Dictionary<string,int>` に正規化する。併せて column map を出力する。
    // `collapsedToRaw[c]` は collapsed 列 `c` に対応する raw 列で、末尾 sentinel には
    // `raw.Length` を入れているため、排他終端インデックスの変換にもそのまま使える。
    // 折り畳みが発生しない early return 経路では `null` を返し、呼び出し元は識別写像を
    // 用いる運用にしている。Closes #400.
    private static string CollapseCSharpGenericTypeWhitespace(string line, out int[]? collapsedToRaw)
    {
        if (string.IsNullOrEmpty(line) || !line.Contains('<') || !line.Contains(' '))
        {
            collapsedToRaw = null;
            return line;
        }

        var builder = new StringBuilder(line.Length);
        var angleDepth = 0;
        var tupleDepth = 0;
        var map = new int[line.Length + 1];
        var mapLength = 0;
        var collapsed = false;

        for (int i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '<' && LooksLikeRecordGenericAngleStart(line, i))
            {
                angleDepth++;
                map[mapLength++] = i;
                builder.Append(ch);
                continue;
            }

            if (ch == '>' && angleDepth > 0)
            {
                angleDepth--;
                map[mapLength++] = i;
                builder.Append(ch);
                continue;
            }

            if (angleDepth > 0 && ch == '(')
            {
                tupleDepth++;
                map[mapLength++] = i;
                builder.Append(ch);
                continue;
            }

            if (angleDepth > 0 && ch == ')' && tupleDepth > 0)
            {
                tupleDepth--;
                map[mapLength++] = i;
                builder.Append(ch);
                continue;
            }

            if (angleDepth > 0 && char.IsWhiteSpace(ch))
            {
                collapsed = true;
                int whitespaceEnd = i + 1;
                while (whitespaceEnd < line.Length && char.IsWhiteSpace(line[whitespaceEnd]))
                    whitespaceEnd++;

                if (tupleDepth > 0 && ShouldPreserveCSharpTupleElementWhitespace(line, i, whitespaceEnd))
                {
                    if (builder.Length == 0 || builder[builder.Length - 1] != ' ')
                    {
                        map[mapLength++] = i;
                        builder.Append(' ');
                    }
                }

                i = whitespaceEnd - 1;
                continue;
            }

            map[mapLength++] = i;
            builder.Append(ch);
        }

        if (!collapsed)
        {
            collapsedToRaw = null;
            return line;
        }

        map[mapLength] = line.Length;
        if (mapLength + 1 != map.Length)
            Array.Resize(ref map, mapLength + 1);
        collapsedToRaw = map;
        return builder.ToString();
    }

    private static bool ShouldPreserveCSharpTupleElementWhitespace(string line, int whitespaceStart, int whitespaceEnd)
    {
        int previous = whitespaceStart - 1;
        while (previous >= 0 && char.IsWhiteSpace(line[previous]))
            previous--;

        if (previous < 0)
            return false;

        int next = whitespaceEnd;
        while (next < line.Length && char.IsWhiteSpace(line[next]))
            next++;

        if (next >= line.Length || !CSharpSymbolNameNormalizer.IsIdentifierStart(line[next]))
            return false;

        return IsCSharpTupleElementTypeTokenEnd(line[previous]);
    }

    private static bool IsCSharpTupleElementTypeTokenEnd(char ch)
    {
        return char.IsLetterOrDigit(ch)
            || ch == '_'
            || ch == '@'
            || ch == ')'
            || ch == ']'
            || ch == '>'
            || ch == '?'
            || ch == '*';
    }

    private static bool ShouldSkipCSharpSwitchExpressionPropertyCandidate(
        string? lang,
        SymbolPattern pattern,
        string matchLine,
        bool[]? csharpSwitchExpressionLines,
        int lineIndex) =>
        lang == "csharp"
        && pattern.Kind == "property"
        && csharpSwitchExpressionLines != null
        && csharpSwitchExpressionLines[lineIndex]
        && matchLine.Contains("=>", StringComparison.Ordinal);

    private static string[] BuildCSharpMatchLines(string[] structuralLines)
        => BuildCSharpMatchLines(structuralLines, out _);

    private static string[] BuildCSharpMatchLines(string[] structuralLines, out int[]?[] collapsedToRaw)
    {
        var matchLines = new string[structuralLines.Length];
        collapsedToRaw = new int[]?[structuralLines.Length];
        var csharpLexState = new CSharpLexState();
        var inLeadingAttributeBlock = false;
        var attributeBracketDepth = 0;
        var attributeParenDepth = 0;
        var pendingEnumDeclaration = false;
        var activeEnumBodyDepth = 0;
        for (int lineIndex = 0; lineIndex < structuralLines.Length; lineIndex++)
        {
            var lexedLine = LexCSharpLine(structuralLines[lineIndex], csharpLexState);
            csharpLexState = lexedLine.EndState;
            matchLines[lineIndex] = CollapseCSharpGenericTypeWhitespace(
                StripLeadingCSharpAttributeLists(
                    lexedLine.SanitizedLine,
                    ref inLeadingAttributeBlock,
                    ref attributeBracketDepth,
                    ref attributeParenDepth,
                    activeEnumBodyDepth > 0),
                out var lineCollapsedToRaw);
            collapsedToRaw[lineIndex] = lineCollapsedToRaw;

            var matchLine = matchLines[lineIndex];
            var trimmed = matchLine.Trim();
            var isEnumDeclarationLine = CSharpEnumDeclarationRegex.IsMatch(matchLine);
            if (isEnumDeclarationLine)
                pendingEnumDeclaration = true;

            foreach (var ch in matchLine)
            {
                if (ch == '{')
                {
                    if (pendingEnumDeclaration)
                    {
                        activeEnumBodyDepth++;
                        pendingEnumDeclaration = false;
                    }
                }
                else if (ch == '}' && activeEnumBodyDepth > 0)
                {
                    activeEnumBodyDepth--;
                }
            }

            if (pendingEnumDeclaration && trimmed.Length > 0 && trimmed != "{" && !isEnumDeclarationLine)
                pendingEnumDeclaration = false;
        }

        return matchLines;
    }

    // Explicit-interface implementations reuse CSharpTypePattern for the return type so nested
    // generics and function pointers (`delegate*<List<int>, int>`, `delegate*<delegate*<int, void>, int>`)
    // are handled uniformly with the regular method / property / indexer / delegate paths. The
    // qualifier itself also has to span multi-argument generics (`IMap<string, int>.Prop`),
    // nullable / array / pointer type arguments (`IFoo<string?>.X`, `IFoo<int[]>.X`, `IFoo<int*>.X`),
    // and nested type paths (`Outer.Inner<T>.Bar`). The shape mirrors CSharpTypePattern's token set
    // so comma + whitespace combinations inside generic argument lists are not dropped.
    // 明示的インターフェース実装の戻り値型は CSharpTypePattern を共有するため、入れ子の generic や
    // `delegate*<...>` / `delegate* unmanaged[Cdecl]<...>` も通常メソッドと同じ経路で扱える。
    // qualifier 側も複数型引数 generic (`IMap<string, int>.Prop`)、nullable / array / pointer 型引数
    // (`IFoo<string?>.X` / `IFoo<int[]>.X` / `IFoo<int*>.X`)、入れ子型パス (`Outer.Inner<T>.Bar`)
    // を通せるように CSharpTypePattern と同じトークン集合へ揃え、generic 引数リスト内の
    // `,` + 空白の組み合わせを落とさないようにする。
    private const string CSharpExplicitInterfaceQualifierPattern =
        @"(?:global::)?(?:" + CSharpIdentifierPattern + @"|" + CSharpIdentifierPattern + @"::" + CSharpIdentifierPattern + @")[\w@?.<>\[\],:*]*(?:\s+[\w@?.<>\[\],:*]+)*";

    // Accepts `Type Name`, `Type`, and `Type Name {` (bare brace at end of declaration
    // line) so BuildCSharpPropertyMatchLine also merges the Microsoft-style
    // `public int Wrap {` + next-line `get { ... }` form with its accessor. Without the
    // optional trailing `{`, that shape would early-return unmerged and get rejected by
    // ShouldSkipCSharpBracePropertyCandidate.
    // Closes #233.
    // `Type Name`、`Type`、および宣言行末の bare `{` を含む `Type Name {` を受け付ける。
    // これにより BuildCSharpPropertyMatchLine が `public int Wrap {` の次行 `get { ... }`
    // も accessor と結合できる。末尾 `{` を許さないと、この形が未結合のまま
    // ShouldSkipCSharpBracePropertyCandidate で弾かれてしまう。
    // Closes #233.
    // Visibility / modifier ordering is free so that multi-line declarations like
    // `static public Dictionary<string, int>` + next-line `Map = new();` or
    // `new public const int` + next-line `C = 1;` merge into a single match line.
    // `const` is included alongside the other field-eligible modifiers for the
    // multi-line const field case. Closes #355.
    // visibility / 修飾子の順序は自由にしておき、`static public Dictionary<string, int>`
    // + 次行 `Map = new();` や `new public const int` + 次行 `C = 1;` のような
    // 複数行宣言も 1 つのマッチ行に結合できるようにする。複数行 const フィールド向けに
    // `const` も他の field 対応修飾子と一緒に列挙する。Closes #355.
    private static readonly Regex CSharpPropertyHeaderPrefixRegex = new($@"^\s*(?:(?:{CSharpVisibilityPattern})\s+|(?:static|virtual|override|abstract|sealed|new|required|partial|readonly|volatile|unsafe|extern|const|ref(?:\s+readonly)?)\s+)*(?:{CSharpTypePattern})\s*(?:{CSharpIdentifierPattern})?\s*\{{?\s*$", RegexOptions.Compiled);
    private static readonly Regex CSharpMethodHeaderPrefixRegex = new(
        $@"^\s*(?:(?:{CSharpVisibilityPattern})\s+|(?:static|virtual|override|abstract|sealed|new|required|partial|readonly|volatile|unsafe|extern|async|file|ref(?:\s+readonly)?)\s+)*(?!{CSharpNonTypeKeywordPattern})(?:{CSharpTypePattern})\s+(?:{CSharpExplicitInterfaceQualifierPattern}\s*\.\s*)?(?:{CSharpIdentifierPattern})\s*(?:<[^{{}};=]*|{CSharpMethodTypeParameterListPattern}\([^){{}};]*)\s*$",
        RegexOptions.Compiled);
    // Limit only the lightweight confirmation phase. Once a candidate looks like a real
    // declaration (`name =`, or a named member header before `{`), BuildCSharpPropertyMatchLine
    // switches to a linear terminator/accessor scan so long raw strings / initializers are not
    // truncated. The cap exists solely to stop false-positive statement fragments such as
    // `return o switch` from repeatedly re-normalizing the rest of a large file. Closes #447.
    // 上限は軽量な確認フェーズにだけ適用する。候補が実際の宣言らしく見えた時点
    // （`name =`、または `{` 前まで到達した named member header）で
    // BuildCSharpPropertyMatchLine は線形な終端 / accessor 走査へ切り替え、長い raw string /
    // initializer を途中で切らない。上限の目的は `return o switch` のような false positive 文断片が
    // 大きいファイルの残り全体を何度も再正規化するのを止めることだけ。Closes #447.
    private const int CSharpPropertyMatchLookaheadLineLimit = 16;
    private const int CSharpPropertyMatchLookaheadCharLimit = 4096;
    private static readonly Regex CSharpConfirmedMemberPrefixRegex = new(
        $@"^\s*(?:(?:{CSharpVisibilityPattern})\s+|(?:static|virtual|override|abstract|sealed|new|required|partial|readonly|volatile|unsafe|extern|const|ref(?:\s+readonly)?)\s+)*(?!(?:class|struct|interface|enum|record|namespace|delegate\b(?!\*)|event|using|return|throw|yield|var|typeof|sizeof|nameof|default|if|for|foreach|while|switch|catch|lock|case|else|when|break|continue|goto|await|try|do|operator|this|base)\b)(?:{CSharpTypePattern})\s+(?:{CSharpExplicitInterfaceQualifierPattern}\s*\.\s*)?(?:{CSharpIdentifierPattern})\s*\{{?\s*$",
        RegexOptions.Compiled);
    private static readonly Regex CSharpConfirmedMethodPrefixRegex = new(
        $@"^\s*(?:(?:{CSharpVisibilityPattern})\s+|(?:static|virtual|override|abstract|sealed|new|required|partial|readonly|volatile|unsafe|extern|async|file|ref(?:\s+readonly)?)\s+)*(?!{CSharpNonTypeKeywordPattern})(?:{CSharpTypePattern})\s+(?:{CSharpExplicitInterfaceQualifierPattern}\s*\.\s*)?(?:{CSharpIdentifierPattern})\s*{CSharpMethodTypeParameterListPattern}\([^{{}};]*\)\s*\{{?\s*$",
        RegexOptions.Compiled);
    private static readonly Regex CSharpStandaloneAccessorRegex = new(
        @"^\s*(?:(?:protected\s+internal|private\s+protected|protected|internal|private|public)\s+)*(?:readonly\s+)*(?:get|set|init)\b",
        RegexOptions.Compiled);

    // Detect physical lines that consist solely of C# modifier keywords (no identifier,
    // no parentheses, no punctuation). Used by TryFindCSharpWrappedHeaderModifier to
    // re-assemble wrapped declarations such as `static\nFoo() { ... }` or
    // `public\nBar() { ... }` whose identifier line alone does not satisfy the
    // constructor / static-constructor regexes. Closes #348.
    // 識別子・括弧・句読点を含まず、C# のモディファイアキーワードのみで構成される物理行を検出する。
    // `static\nFoo() { ... }` や `public\nBar() { ... }` のようにラップされた宣言の
    // 識別子行だけでは constructor / static constructor の regex を満たせないため、
    // TryFindCSharpWrappedHeaderModifier が prefix を再構築する用途で使う。Closes #348.
    private static readonly Regex CSharpWrappedHeaderModifierLineRegex = new(
        @"^\s*(?:public|private|protected|internal|static|partial|readonly|abstract|sealed|virtual|override|async|new|file|unsafe|extern|required|volatile)(?:\s+(?:public|private|protected|internal|static|partial|readonly|abstract|sealed|virtual|override|async|new|file|unsafe|extern|required|volatile))*\s*$",
        RegexOptions.Compiled);

    private readonly record struct CSharpWrappedHeaderModifierInfo(string Prefix);


    private static bool TryGetFirstNonWhitespaceColumn(string line, int startColumn, int endColumnExclusive, out int column)
    {
        for (column = Math.Min(startColumn, line.Length); column < Math.Min(endColumnExclusive, line.Length); column++)
        {
            if (!char.IsWhiteSpace(line[column]))
                return true;
        }

        column = -1;
        return false;
    }

    private static int FindCSharpDeclarationStartColumn(string rawLine, string? signature)
    {
        if (!string.IsNullOrWhiteSpace(signature))
        {
            var signatureIndex = rawLine.IndexOf(signature, StringComparison.Ordinal);
            if (signatureIndex >= 0)
                return signatureIndex;
        }

        return rawLine.IndexOf("enum ", StringComparison.Ordinal);
    }

    private static bool TryRecoverBrokenCSharpEnumAttributeInBody(
        string[] csharpMatchLines,
        int startLineIndex,
        int bodyEndLineIndex,
        int bodyEndColumnExclusive,
        out (int LineIndex, int Column) recoveredPosition)
    {
        recoveredPosition = default;
        for (var lineIndex = startLineIndex + 1; lineIndex <= bodyEndLineIndex; lineIndex++)
        {
            var line = csharpMatchLines[lineIndex];
            var scanEndColumnExclusive = lineIndex == bodyEndLineIndex
                ? Math.Min(bodyEndColumnExclusive, line.Length)
                : line.Length;

            if (!TryGetFirstNonWhitespaceColumn(line, 0, scanEndColumnExclusive, out var firstNonWhitespaceColumn))
                continue;

            var first = line[firstNonWhitespaceColumn];
            if (first is '#' or '[' or '}')
                continue;

            if (!CSharpEnumMemberNameRegex.IsMatch(line[firstNonWhitespaceColumn..scanEndColumnExclusive]))
                continue;

            recoveredPosition = (lineIndex, firstNonWhitespaceColumn);
            return true;
        }

        return false;
    }

    private static bool TrySkipLeadingCSharpAttributeListsInEnumBody(
        string[] csharpMatchLines,
        int startLineIndex,
        int startColumn,
        int bodyEndLineIndex,
        int bodyEndColumnExclusive,
        out (int LineIndex, int Column) nextPosition)
    {
        nextPosition = (startLineIndex, startColumn);
        var lineIndex = startLineIndex;
        var column = startColumn;

        while (lineIndex <= bodyEndLineIndex)
        {
            var line = csharpMatchLines[lineIndex];
            var lineEndExclusive = lineIndex == bodyEndLineIndex
                ? Math.Min(bodyEndColumnExclusive, line.Length)
                : line.Length;
            var probe = column;

            while (probe < lineEndExclusive && char.IsWhiteSpace(line[probe]))
                probe++;

            if (probe >= lineEndExclusive)
            {
                lineIndex++;
                column = 0;
                continue;
            }

            if (line[probe] != '[')
            {
                if (probe == startColumn && lineIndex == startLineIndex)
                    return false;

                nextPosition = (lineIndex, probe);
                return true;
            }

            var bracketDepth = 0;
            var parenDepth = 0;
            var currentLineIndex = lineIndex;
            var currentColumn = probe;
            var matchedAttribute = false;

            while (currentLineIndex <= bodyEndLineIndex)
            {
                var currentLine = csharpMatchLines[currentLineIndex];
                var currentLineEndExclusive = currentLineIndex == bodyEndLineIndex
                    ? Math.Min(bodyEndColumnExclusive, currentLine.Length)
                    : currentLine.Length;

                while (currentColumn < currentLineEndExclusive)
                {
                    var ch = currentLine[currentColumn++];
                    if (ch == '[')
                    {
                        bracketDepth++;
                    }
                    else if (ch == '(')
                    {
                        parenDepth++;
                    }
                    else if (ch == ')' && parenDepth > 0)
                    {
                        parenDepth--;
                    }
                    else if (ch == ']')
                    {
                        if (bracketDepth > 0)
                            bracketDepth--;

                        if (bracketDepth == 0)
                        {
                            matchedAttribute = true;
                            break;
                        }
                    }
                }

                if (matchedAttribute)
                {
                    lineIndex = currentLineIndex;
                    column = currentColumn;
                    break;
                }

                currentLineIndex++;
                currentColumn = 0;
            }

            if (!matchedAttribute)
                return false;
        }

        nextPosition = (bodyEndLineIndex, bodyEndColumnExclusive);
        return true;
    }

    private static void TryAddCSharpEnumMemberFromSpan(
        long fileId,
        SymbolRecord enumSymbol,
        string[] rawLines,
        string[] csharpMatchLines,
        (int LineIndex, int Column) start,
        (int LineIndex, int Column) endExclusive,
        List<SymbolRecord> symbols)
    {
        endExclusive = TrimTrailingCSharpEnumMemberSpan(rawLines, start, endExclusive);
        var maskedSnippet = GetSourceSpanText(csharpMatchLines, start, endExclusive);
        if (string.IsNullOrWhiteSpace(maskedSnippet))
            return;

        var match = CSharpEnumMemberNameRegex.Match(maskedSnippet);
        if (!match.Success)
            return;

        var rawSignature = GetSourceSpanText(rawLines, start, endExclusive).Trim();
        if (rawSignature.Length == 0)
            return;

        symbols.Add(new SymbolRecord
        {
            FileId = fileId,
            Kind = "enum",
            Name = CSharpSymbolNameNormalizer.Normalize(match.Groups["name"].Value.Trim(), match, maskedSnippet),
            Line = start.LineIndex + 1,
            StartLine = start.LineIndex + 1,
            EndLine = endExclusive.LineIndex + 1,
            Signature = rawSignature,
            ContainerKind = "enum",
            ContainerName = enumSymbol.Name,
        });
    }

    private static (int LineIndex, int Column) TrimTrailingCSharpEnumMemberSpan(
        string[] rawLines,
        (int LineIndex, int Column) start,
        (int LineIndex, int Column) endExclusive)
    {
        var lineIndex = Math.Min(endExclusive.LineIndex, rawLines.Length - 1);
        var column = lineIndex >= 0
            ? Math.Min(endExclusive.Column, rawLines[lineIndex].Length)
            : 0;

        while (lineIndex > start.LineIndex || (lineIndex == start.LineIndex && column > start.Column))
        {
            if (column == 0)
            {
                lineIndex--;
                if (lineIndex < 0)
                    break;
                column = rawLines[lineIndex].Length;
                continue;
            }

            var previous = rawLines[lineIndex][column - 1];
            if (!char.IsWhiteSpace(previous) && previous != '}')
                break;

            column--;
        }

        return (lineIndex, column);
    }

    private static string GetSourceSpanText(
        string[] lines,
        (int LineIndex, int Column) start,
        (int LineIndex, int Column) endExclusive)
    {
        if (start.LineIndex > endExclusive.LineIndex
            || start.LineIndex < 0
            || endExclusive.LineIndex >= lines.Length)
        {
            return string.Empty;
        }

        if (start.LineIndex == endExclusive.LineIndex)
        {
            var line = lines[start.LineIndex];
            var effectiveLength = GetLineLengthExcludingTrailingCr(line);
            var startColumn = Math.Min(start.Column, effectiveLength);
            var endColumn = Math.Min(Math.Max(endExclusive.Column, startColumn), effectiveLength);
            return line[startColumn..endColumn];
        }

        var builder = new StringBuilder();
        for (int lineIndex = start.LineIndex; lineIndex <= endExclusive.LineIndex; lineIndex++)
        {
            var line = lines[lineIndex];
            // Content was split on '\n', so CRLF lines carry a trailing '\r'. Exclude it from
            // the effective length so the multi-line separator stays '\n' regardless of source
            // line endings and signatures stay OS-independent (see #382 / #405).
            // content は '\n' で分割しているため、CRLF 行は末尾に '\r' が残る。multi-line の
            // 区切りを OS に依存せず '\n' に揃え signature の一致判定を保つため、effective
            // length からは '\r' を除外する（#382 / #405 参照）。
            var effectiveLength = GetLineLengthExcludingTrailingCr(line);
            var startColumn = lineIndex == start.LineIndex
                ? Math.Min(start.Column, effectiveLength)
                : 0;
            var endColumn = lineIndex == endExclusive.LineIndex
                ? Math.Min(Math.Max(endExclusive.Column, startColumn), effectiveLength)
                : effectiveLength;

            builder.Append(line[startColumn..endColumn]);
            if (lineIndex < endExclusive.LineIndex)
                builder.Append('\n');
        }

        return builder.ToString();
    }

    private static int GetLineLengthExcludingTrailingCr(string line)
    {
        var length = line.Length;
        if (length > 0 && line[length - 1] == '\r')
            length--;
        return length;
    }

    private static string StripTrailingCr(string line)
    {
        if (line.Length > 0 && line[^1] == '\r')
            return line[..^1];
        return line;
    }

    // 誤読されてしまう。


    private static int CountBraces(string sanitizedLine)
    {
        var delta = 0;
        foreach (var ch in sanitizedLine)
        {
            if (ch == '{')
                delta++;
            else if (ch == '}')
                delta--;
        }

        return delta;
    }

}
