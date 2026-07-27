using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static CSharpLexState[] BuildCSharpLineStartStates(string[] lines)
    {
        var result = new CSharpLexState[lines.Length];
        var state = new CSharpLexState();
        for (var i = 0; i < lines.Length; i++)
        {
            result[i] = state;
            state = LexCSharpLine(lines[i], state).EndState;
        }

        return result;
    }

    private static bool IsCSharpRootCodePosition(string line, CSharpLexState lineStartState, int rawColumn)
    {
        var clampedColumn = Math.Clamp(rawColumn, 0, line.Length);
        var stateAtColumn = clampedColumn == 0
            ? lineStartState
            : LexCSharpLine(line[..clampedColumn], lineStartState).EndState;

        return stateAtColumn.Mode == CSharpLexMode.Code
            && stateAtColumn.InterpolationReturnMode == CSharpLexMode.Code
            && stateAtColumn.InterpolationBraceDepth == 0;
    }

    // Translate a column in a CollapseCSharpGenericTypeWhitespace-collapsed match line back
    // to the matching column in the raw source line. Used by the plain-field scope gate and
    // signature clamp so `public class C<T1, T2>{int X;}` does not misalign the type-body
    // scope lookup when internal generic whitespace has been collapsed away, and so field
    // signatures sliced out of the raw line preserve the original separators instead of
    // picking up phantom leading `;` from the next declarator on the same line. Closes #400.
    // CollapseCSharpGenericTypeWhitespace で空白を詰めた match 行上の列を、元の raw 行の
    // 列に戻す。`public class C<T1, T2>{int X;}` のような行で CSharpTypeBodyScope の参照列が
    // ずれないようにしたり、同一行に続くフィールドを raw から slice したときに
    // 先頭に余計な `;` が混入しないようにするため、プレーンフィールドのゲートと
    // signature clamp で利用する。Closes #400.
    private static int TranslateCSharpCollapsedColumnToRaw(int[]?[] mapPerLine, int lineIndex, int collapsedColumn, int rawLength)
    {
        if (mapPerLine == null || lineIndex < 0 || lineIndex >= mapPerLine.Length)
            return collapsedColumn;
        var map = mapPerLine[lineIndex];
        if (map == null)
            return collapsedColumn;
        if (collapsedColumn < 0)
            return 0;
        if (collapsedColumn >= map.Length)
            return rawLength;
        return map[collapsedColumn];
    }

    // Convert a raw-line column back into the per-line collapsed C# match-line domain.
    // Same-line brace-bodied generic members now keep raw columns for signature slicing,
    // but sibling rescan still runs on `csharpMatchLines[i]` (collapsed). Map the
    // closing-brace column back before calling `FindNextSameLineBraceStatementStart`, or
    // a raw column shifted right by removed generic whitespace can restart inside/past the
    // next compact sibling and make later declarations disappear. Closes #533.
    // raw 行の列を、per-line collapsed な C# match 行の列へ戻す。same-line の
    // brace-bodied generic member は signature 切り出しのため raw 列を保持するが、
    // sibling 再スキャン自体は `csharpMatchLines[i]`（collapsed）上で動く。そこで
    // `FindNextSameLineBraceStatementStart` に渡す前に閉じ brace 列を collapsed 側へ戻し、
    // generic 内で消えた空白ぶん右へずれた raw 列が次 sibling の途中/後ろから再開して
    // 後続宣言を落とすのを防ぐ。Closes #533.
    private static int TranslateCSharpRawColumnToCollapsed(int[]?[] mapPerLine, int lineIndex, int rawColumn, int collapsedLength, int rawLength)
    {
        if (mapPerLine == null || lineIndex < 0 || lineIndex >= mapPerLine.Length)
            return rawColumn;
        var map = mapPerLine[lineIndex];
        if (map == null)
            return rawColumn;
        if (rawColumn <= 0)
            return 0;
        if (map.Length == 0)
            return Math.Clamp(rawColumn, 0, collapsedLength);
        if (rawColumn >= rawLength)
            return collapsedLength;

        var lo = 0;
        var hi = map.Length - 1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) / 2);
            var mappedRaw = map[mid];
            if (mappedRaw == rawColumn)
                return mid;
            if (mappedRaw < rawColumn)
                lo = mid + 1;
            else
                hi = mid - 1;
        }

        if (hi < 0)
            return 0;
        if (hi >= map.Length)
            return collapsedLength;
        return hi;
    }

    // Gate only the block-bodied property pattern (requires `{ get|set|init ... }`).
    // Expression-bodied properties (`Name => expr;`) now also use BodyStyle.Brace so
    // FindCSharpBraceRange can detect `=>` and compute a body range, but they never
    // carry `{ get|set|init` on the match line — skipping them here would throw away
    // every expression-bodied property. Closes #233.
    // block-bodied プロパティパターン（`{ get|set|init ... }` を要求）のみガードする。
    // 式本体プロパティ（`Name => expr;`）も FindCSharpBraceRange で '=>' 本体範囲を
    // 取るため BodyStyle.Brace を使うが、match 行に `{ get|set|init` は来ないので
    // ここで弾くと式本体プロパティが全滅してしまう。Closes #233.
    private static bool TrySkipCSharpBracePropertyCandidate(
        string? lang,
        SymbolPattern pattern,
        string matchLine,
        int matchStartColumn,
        bool matchedExpressionArrow,
        out int nextSameLineOffset)
    {
        nextSameLineOffset = -1;
        if (lang != "csharp"
            || pattern.Kind != "property"
            || pattern.BodyStyle != BodyStyle.Brace)
        {
            return false;
        }

        if (matchStartColumn < 0)
            matchStartColumn = 0;
        if (matchStartColumn > matchLine.Length)
            matchStartColumn = matchLine.Length;

        // Same-line type headers can still false-positive as brace properties because the
        // C# property regex accepts omitted visibility/modifier runs. Detect a real
        // class/struct/interface/record header up front and restart from the first member
        // inside that type body, rather than from the regex match tail. The regex tail can
        // overrun into a later sibling expression-bodied property (`A => 1`) or brace-body
        // property (`P { get; set; }`), which would otherwise skip the real member that
        // should be matched next. Closes #472.
        // 同一行の型ヘッダは、visibility / modifier 省略を許す C# property regex により
        // brace-property 偽陽性になりうる。ここでは実際の
        // class/struct/interface/record ヘッダを先に検出し、regex マッチ末尾ではなく
        // 型本体の最初の member 位置から再開する。regex 末尾基準だと後続の
        // 式本体 property (`A => 1`) や brace-body property (`P { get; set; }`) まで
        // 飛び越してしまい、次に取るべき本物の member をスキップしてしまう。Closes #472.
        var matchedDeclaration = matchLine[matchStartColumn..];
        if (CSharpTypeBodyDeclarationMarker.IsMatch(matchedDeclaration))
        {
            var typeBodyOpenBrace = matchedDeclaration.IndexOf('{');
            if (typeBodyOpenBrace >= 0)
            {
                nextSameLineOffset = FindNextSameLineNonClosingBraceStatementStart(
                    matchLine,
                    matchStartColumn + typeBodyOpenBrace + 1,
                    lang);
            }

            return true;
        }

        return !matchedExpressionArrow
            && !HasCSharpPropertyAccessorStart(matchedDeclaration);
    }

    // Mark every line that sits directly inside a C# type body (class / struct /
    // interface / record / enum). Used to gate the plain-field pattern so that
    // local variable declarations inside a method, property accessor, lambda, or
    // other non-type body are not misclassified as kind `property`. The scan uses
    // `structuralLines` (strings / chars / comments already masked), so it is not
    // fooled by braces or type-declaration-looking text inside literals. Only
    // brace-delimited types push a type-body frame — `new { ... }`, collection
    // initializers, and lambda bodies all carry the `class|struct|interface|record|enum`
    // keyword absent from the preceding buffer, so they correctly stay non-type.
    // Closes #298 follow-up (codex review blocker).
    // C# の「現在この行は型本体（class / struct / interface / record / enum）の
    // 直下にあるか」を行単位で事前計算する。新しい通常フィールド抽出パターンが
    // メソッド本体・プロパティアクセサ・ラムダなど「非型本体」に含まれる
    // ローカル変数宣言を kind `property` として誤抽出しないよう、このフラグで
    // ゲートする。走査は既に文字列・文字・コメントを空白化した
    // `structuralLines` を使うため、リテラル内の `{` や `class` 相当の文字列に
    // 騙されない。`new { ... }` や collection initializer、ラムダ本体の `{` は
    // 直前バッファに `class|struct|interface|record|enum` を含まないため
    // 非型本体として扱われる。Closes #298 の codex レビュー blocker 対応。
    // Marks `{` that opens a class-like body where C# plain fields are legal.
    // `enum` is intentionally excluded: enum bodies contain enum members (not
    // fields), and the field regex would otherwise match enum member shapes like
    // `[Obsolete] A = (int)B,` as phantom `property` symbols. The column-aware
    // scope gate relies on this distinction to reject field candidates inside
    // enum bodies while still accepting legitimate fields inside class / struct
    // / interface / record bodies. Closes #400.
    // 型本体に相当する `{` を識別する正規表現。`enum` を意図的に除外することで、
    // enum 本体内の `[Obsolete] A = (int)B,` のような enum member を plain field
    // regex が `property` として拾ってしまう問題を防ぐ。列意識スコープゲートは
    // この区別を使って、enum 本体内の field 候補は拒否し、class / struct /
    // interface / record 本体内の本物のフィールドは引き続き許容する。Closes #400.
    private static readonly Regex CSharpTypeBodyDeclarationMarker = new(
        @"\b(?:class|struct|interface|record)\b\s+\w",
        RegexOptions.Compiled);

    // Return true when the accumulated field header text reaches a top-level `;`.
    // Tracks paren/bracket/brace depth so `;` inside an initializer such as
    // `for (; ; ) { … }` never falsely marks the declaration as complete.
    // 累積ヘッダが paren/bracket/brace の深さ 0 にある `;` に到達したら true を返す。
    // `for (; ; ) { … }` のような初期化式内の `;` を完了と誤認しないよう深さを追跡する。
    private static bool HasCSharpTopLevelSemicolon(string text)
    {
        int paren = 0, bracket = 0, brace = 0;
        for (int i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '(': paren++; continue;
                case ')' when paren > 0: paren--; continue;
                case '[': bracket++; continue;
                case ']' when bracket > 0: bracket--; continue;
                case '{': brace++; continue;
                case '}' when brace > 0: brace--; continue;
                case ';' when paren == 0 && bracket == 0 && brace == 0:
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Records positions where a C# declaration may start without inheriting an
    /// unmatched expression delimiter. Block lambdas establish a nested statement
    /// baseline so valid local functions inside callback bodies remain eligible.
    /// C# 宣言が未閉じの式 delimiter を引き継がずに開始できる位置を記録する。
    /// block lambda は入れ子の statement baseline を作り、callback 本体内の正当な
    /// local function を引き続き宣言候補として扱う。
    /// </summary>
    private sealed class CSharpDeclarationStartScope
    {
        public static readonly CSharpDeclarationStartScope Empty = new(null, null);

        private readonly bool[]? _lineStartInsideExpressionContinuation;
        private readonly List<(int Column, bool IsInsideExpressionContinuation)>?[]? _transitions;

        public CSharpDeclarationStartScope(
            bool[]? lineStartInsideExpressionContinuation,
            List<(int Column, bool IsInsideExpressionContinuation)>?[]? transitions)
        {
            _lineStartInsideExpressionContinuation = lineStartInsideExpressionContinuation;
            _transitions = transitions;
        }

        public bool CanStartDeclarationAt(int lineIndex, int column)
        {
            var isInsideExpressionContinuation =
                _lineStartInsideExpressionContinuation?[lineIndex] ?? false;
            var transitions = _transitions?[lineIndex];
            if (transitions == null)
                return !isInsideExpressionContinuation;

            foreach (var (transitionColumn, transitionState) in transitions)
            {
                if (transitionColumn >= column)
                    break;
                isInsideExpressionContinuation = transitionState;
            }

            return !isInsideExpressionContinuation;
        }
    }

    private enum CSharpPreprocessorDirectiveKind
    {
        None,
        If,
        Alternative,
        EndIf,
        Other,
    }

    private readonly record struct CSharpDeclarationDelimiterState(
        int ParenDepth,
        int ParenBaseline,
        bool[] ParenContributions,
        (int ParenDepth, bool RestoresBaseline)[] BraceBaselines,
        string StatementText);

    private static CSharpDeclarationStartScope BuildCSharpDeclarationStartScope(
        string[] structuralLines,
        CSharpTypeBodyScope typeBodyScope)
    {
        if (!LinesContain(structuralLines, '('))
            return CSharpDeclarationStartScope.Empty;

        bool[]? lineStartInsideExpressionContinuation = null;
        List<(int Column, bool IsInsideExpressionContinuation)>?[]? transitions = null;
        var statementBuffer = new StringBuilder(256);
        var braceBaselines = new Stack<(int ParenDepth, bool RestoresBaseline)>();
        var parenContributions = new Stack<bool>();
        var conditionalBranchStates = new Stack<CSharpDeclarationDelimiterState>();
        var parenDepth = 0;
        var parenBaseline = 0;

        for (var lineIndex = 0; lineIndex < structuralLines.Length; lineIndex++)
        {
            var isInsideExpressionContinuation = parenDepth > parenBaseline;
            if (isInsideExpressionContinuation)
            {
                (lineStartInsideExpressionContinuation ??=
                    new bool[structuralLines.Length])[lineIndex] = true;
            }

            var line = structuralLines[lineIndex];
            var directiveKind = GetCSharpPreprocessorDirectiveKind(line);
            if (directiveKind != CSharpPreprocessorDirectiveKind.None)
            {
                switch (directiveKind)
                {
                    case CSharpPreprocessorDirectiveKind.If:
                        conditionalBranchStates.Push(new CSharpDeclarationDelimiterState(
                            parenDepth,
                            parenBaseline,
                            parenContributions.ToArray(),
                            braceBaselines.ToArray(),
                            statementBuffer.ToString()));
                        break;
                    case CSharpPreprocessorDirectiveKind.Alternative:
                        if (conditionalBranchStates.TryPeek(out var branchStart))
                        {
                            parenDepth = branchStart.ParenDepth;
                            parenBaseline = branchStart.ParenBaseline;
                            RestoreCSharpDeclarationDelimiterStack(
                                parenContributions,
                                branchStart.ParenContributions);
                            RestoreCSharpDeclarationDelimiterStack(
                                braceBaselines,
                                branchStart.BraceBaselines);
                            statementBuffer.Clear();
                            statementBuffer.Append(branchStart.StatementText);
                        }
                        break;
                    case CSharpPreprocessorDirectiveKind.EndIf:
                        if (conditionalBranchStates.Count > 0)
                            conditionalBranchStates.Pop();
                        break;
                }

                // Preprocessor payloads are not C# expressions. In particular, arbitrary
                // `#region` text may contain unmatched delimiters and conditional branches
                // are mutually exclusive rather than one linear token stream.
                // preprocessor payload は C# 式ではない。特に任意の `#region` text は
                // 未閉じ delimiter を含み得て、conditional branch は一つの線形 token
                // stream ではなく相互排他的である。
                statementBuffer.Append(' ');
                continue;
            }

            for (var column = 0; column < line.Length; column++)
            {
                var previousState = isInsideExpressionContinuation;
                var ch = line[column];
                switch (ch)
                {
                    case '(':
                        {
                            // A type-body attribute opener cannot contain a declaration. Exclude
                            // only that same-line `[Attr(` parenthesis from expression depth so
                            // incomplete-attribute recovery stays intact without letting unrelated
                            // square-bracket syntax disable continuation tracking later in the file.
                            // 型本体の attribute opener 内から宣言は始まらない。同一行の
                            // `[Attr(` 括弧だけを expression depth から除外し、未完了 attribute
                            // の回復を維持しつつ、後続の無関係な角括弧構文で追跡を無効化しない。
                            var contributesToExpressionDepth =
                                !IsCSharpTypeBodyAttributeArgumentOpeningParen(
                                    line,
                                    lineIndex,
                                    column,
                                    typeBodyScope);
                            parenContributions.Push(contributesToExpressionDepth);
                            if (contributesToExpressionDepth)
                                parenDepth++;
                            statementBuffer.Append(ch);
                            break;
                        }
                    case ')':
                        if (parenContributions.Count > 0
                            && parenContributions.Pop()
                            && parenDepth > 0)
                        {
                            parenDepth--;
                        }
                        statementBuffer.Append(ch);
                        break;
                    case '[':
                        statementBuffer.Append(ch);
                        break;
                    case ']':
                        statementBuffer.Append(ch);
                        break;
                    case '{':
                        {
                            var establishesStatementBaseline =
                                IsCSharpAnonymousCallableBlock(statementBuffer);
                            braceBaselines.Push((
                                parenBaseline,
                                establishesStatementBaseline));
                            if (establishesStatementBaseline)
                                parenBaseline = parenDepth;
                            statementBuffer.Clear();
                            break;
                        }
                    case '}':
                        if (braceBaselines.Count > 0)
                        {
                            var priorBaseline = braceBaselines.Pop();
                            if (priorBaseline.RestoresBaseline)
                                parenBaseline = priorBaseline.ParenDepth;
                        }
                        statementBuffer.Clear();
                        break;
                    case ';':
                        statementBuffer.Clear();
                        break;
                    default:
                        statementBuffer.Append(ch);
                        break;
                }

                isInsideExpressionContinuation = parenDepth > parenBaseline;
                if (isInsideExpressionContinuation != previousState)
                {
                    var transitionsByLine = transitions ??=
                        new List<(int, bool)>?[structuralLines.Length];
                    (transitionsByLine[lineIndex] ??= []).Add((
                        column,
                        isInsideExpressionContinuation));
                }
            }

            statementBuffer.Append(' ');
        }

        return new CSharpDeclarationStartScope(
            lineStartInsideExpressionContinuation,
            transitions);
    }

    private static CSharpPreprocessorDirectiveKind GetCSharpPreprocessorDirectiveKind(string line)
    {
        var cursor = SkipWhitespace(line, 0);
        if (cursor >= line.Length || line[cursor] != '#')
            return CSharpPreprocessorDirectiveKind.None;

        cursor = SkipWhitespace(line, cursor + 1);
        var directiveStart = cursor;
        while (cursor < line.Length && char.IsLetter(line[cursor]))
            cursor++;

        var directive = line[directiveStart..cursor];
        return directive switch
        {
            "if" => CSharpPreprocessorDirectiveKind.If,
            "elif" or "else" => CSharpPreprocessorDirectiveKind.Alternative,
            "endif" => CSharpPreprocessorDirectiveKind.EndIf,
            _ => CSharpPreprocessorDirectiveKind.Other,
        };
    }

    private static void RestoreCSharpDeclarationDelimiterStack<T>(
        Stack<T> destination,
        T[] topFirstItems)
    {
        destination.Clear();
        for (var index = topFirstItems.Length - 1; index >= 0; index--)
            destination.Push(topFirstItems[index]);
    }

    private static bool IsCSharpTypeBodyAttributeArgumentOpeningParen(
        string line,
        int lineIndex,
        int column,
        CSharpTypeBodyScope typeBodyScope)
    {
        if (!typeBodyScope.IsInsideTypeBodyAt(lineIndex, column))
            return false;

        var cursor = SkipWhitespace(line, 0);
        while (cursor < column && line[cursor] == '[')
        {
            var closeBracket = line.IndexOf(']', cursor + 1, column - cursor - 1);
            if (closeBracket < 0)
                return true;
            cursor = SkipWhitespace(line, closeBracket + 1);
        }

        return false;
    }

    private static bool IsCSharpAnonymousCallableBlock(StringBuilder statementBuffer)
    {
        var text = statementBuffer.ToString().TrimEnd();
        if (text.EndsWith("=>", StringComparison.Ordinal))
            return true;

        const string delegateKeyword = "delegate";
        var delegateIndex = text.LastIndexOf(delegateKeyword, StringComparison.Ordinal);
        if (delegateIndex < 0
            || (delegateIndex > 0
                && (text[delegateIndex - 1] == '@'
                    || text[delegateIndex - 1] == '_'
                    || char.IsLetterOrDigit(text[delegateIndex - 1]))))
        {
            return false;
        }

        var suffix = text[(delegateIndex + delegateKeyword.Length)..].Trim();
        if (suffix.Length == 0)
            return true;
        if (suffix[0] != '(' || suffix[^1] != ')')
            return false;

        var depth = 0;
        foreach (var ch in suffix)
        {
            if (ch == '(')
                depth++;
            else if (ch == ')' && --depth < 0)
                return false;
        }

        return depth == 0;
    }

    private sealed class CSharpCallableParameterScope
    {
        public static readonly CSharpCallableParameterScope Empty = new(null, null);

        private readonly bool[]? _lineStartInsideParameterList;
        private readonly List<(int Column, bool IsInsideParameterList)>?[]? _transitions;

        public CSharpCallableParameterScope(bool[]? lineStartInsideParameterList, List<(int Column, bool IsInsideParameterList)>?[]? transitions)
        {
            _lineStartInsideParameterList = lineStartInsideParameterList;
            _transitions = transitions;
        }

        public bool IsInsideParameterListAt(int lineIndex, int column)
        {
            var state = _lineStartInsideParameterList?[lineIndex] ?? false;
            var transitions = _transitions?[lineIndex];
            if (transitions == null)
                return state;

            foreach (var (col, isInsideParameterList) in transitions)
            {
                if (col >= column)
                    break;
                state = isInsideParameterList;
            }

            return state;
        }
    }

    private static CSharpCallableParameterScope BuildCSharpCallableParameterScope(
        string[] structuralLines,
        CSharpTypeBodyScope typeBodyScope)
    {
        if (!LinesContain(structuralLines, '('))
            return CSharpCallableParameterScope.Empty;

        bool[]? lineStartInsideParameterList = null;
        List<(int Column, bool IsInsideParameterList)>?[]? transitions = null;
        var declarationBuffer = new StringBuilder(256);
        var parameterParenDepth = 0;

        for (int lineIndex = 0; lineIndex < structuralLines.Length; lineIndex++)
        {
            if (parameterParenDepth > 0)
                (lineStartInsideParameterList ??= new bool[structuralLines.Length])[lineIndex] = true;
            var line = structuralLines[lineIndex];

            for (int cursor = 0; cursor < line.Length; cursor++)
            {
                var ch = line[cursor];
                if (parameterParenDepth > 0)
                {
                    if (ch == '(')
                    {
                        parameterParenDepth++;
                    }
                    else if (ch == ')')
                    {
                        parameterParenDepth--;
                        if (parameterParenDepth == 0)
                            AddCSharpCallableParameterTransition(ref transitions, structuralLines.Length, lineIndex, cursor, false);
                    }

                    declarationBuffer.Append(ch);
                    continue;
                }

                if (ch == '('
                    && typeBodyScope.IsInsideTypeBodyAt(lineIndex, cursor)
                    && IsCSharpCallableHeaderBeforeParameterList(declarationBuffer.ToString()))
                {
                    parameterParenDepth = 1;
                    AddCSharpCallableParameterTransition(ref transitions, structuralLines.Length, lineIndex, cursor, true);
                    declarationBuffer.Append(ch);
                    continue;
                }

                if (ch is '{' or '}' or ';')
                {
                    declarationBuffer.Clear();
                    continue;
                }

                declarationBuffer.Append(ch);
            }
        }

        return new CSharpCallableParameterScope(lineStartInsideParameterList, transitions);
    }

    private static void AddCSharpCallableParameterTransition(
        ref List<(int Column, bool IsInsideParameterList)>?[]? transitions,
        int lineCount,
        int lineIndex,
        int column,
        bool isInsideParameterList)
    {
        var transitionsByLine = transitions ??= new List<(int, bool)>?[lineCount];
        (transitionsByLine[lineIndex] ??= []).Add((column, isInsideParameterList));
    }

    private static bool IsCSharpCallableHeaderBeforeParameterList(string header)
    {
        var text = header.Trim();
        if (text.Length == 0 || ContainsCSharpTopLevelAssignment(text))
            return false;

        var end = SkipCSharpTrailingGenericParameterList(text, text.Length);
        while (end > 0 && char.IsWhiteSpace(text[end - 1]))
            end--;
        if (end <= 0)
            return false;

        var tokenEnd = end;
        var tokenStart = tokenEnd;
        while (tokenStart > 0 && IsCSharpIdentifierPart(text[tokenStart - 1]))
            tokenStart--;
        if (tokenStart == tokenEnd)
            return false;

        var token = text[tokenStart..tokenEnd];
        if (token.StartsWith('@') && token.Length > 1)
            return true;

        return token.Length > 0 && !IsCSharpNonCallableHeaderTailToken(token);
    }

    private static int SkipCSharpTrailingGenericParameterList(string text, int end)
    {
        while (end > 0 && char.IsWhiteSpace(text[end - 1]))
            end--;
        if (end <= 0 || text[end - 1] != '>')
            return end;

        var depth = 0;
        for (var index = end - 1; index >= 0; index--)
        {
            if (text[index] == '>')
            {
                depth++;
                continue;
            }

            if (text[index] == '<')
            {
                depth--;
                if (depth == 0)
                    return index;
            }
        }

        return end;
    }

    private static bool ContainsCSharpTopLevelAssignment(string text)
    {
        var angleDepth = 0;
        var parenDepth = 0;
        var bracketDepth = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            switch (ch)
            {
                case '<':
                    angleDepth++;
                    continue;
                case '>' when angleDepth > 0:
                    angleDepth--;
                    continue;
                case '(':
                    parenDepth++;
                    continue;
                case ')' when parenDepth > 0:
                    parenDepth--;
                    continue;
                case '[':
                    bracketDepth++;
                    continue;
                case ']' when bracketDepth > 0:
                    bracketDepth--;
                    continue;
                case '=' when angleDepth == 0 && parenDepth == 0 && bracketDepth == 0:
                    return true;
            }
        }

        return false;
    }

    private static bool IsCSharpIdentifierPart(char ch) =>
        char.IsLetterOrDigit(ch) || ch is '_' or '$' or '@';

    private static bool IsCSharpNonCallableHeaderTailToken(string token) =>
        token is
            "abstract" or
            "async" or
            "await" or
            "base" or
            "case" or
            "catch" or
            "const" or
            "continue" or
            "default" or
            "delegate" or
            "else" or
            "event" or
            "extern" or
            "false" or
            "file" or
            "for" or
            "foreach" or
            "goto" or
            "if" or
            "internal" or
            "lock" or
            "nameof" or
            "new" or
            "null" or
            "override" or
            "private" or
            "protected" or
            "public" or
            "readonly" or
            "ref" or
            "required" or
            "return" or
            "sealed" or
            "sizeof" or
            "stackalloc" or
            "static" or
            "switch" or
            "this" or
            "throw" or
            "true" or
            "typeof" or
            "unsafe" or
            "using" or
            "var" or
            "virtual" or
            "volatile" or
            "when" or
            "while" or
            "yield";

    private sealed class DartClassBodyScope
    {
        public static readonly DartClassBodyScope Empty = new(null);

        private readonly bool[]? _lineStartInsideClassBody;

        public DartClassBodyScope(bool[]? lineStartInsideClassBody)
        {
            _lineStartInsideClassBody = lineStartInsideClassBody;
        }

        public bool IsInsideClassBodyAt(int lineIndex) => _lineStartInsideClassBody?[lineIndex] ?? false;
    }

    private static DartClassBodyScope BuildDartClassBodyScope(string[] structuralLines)
    {
        if (!LinesContain(structuralLines, "class", StringComparison.Ordinal))
            return DartClassBodyScope.Empty;
        if (!LinesContain(structuralLines, '{'))
            return DartClassBodyScope.Empty;

        var lineStartInsideClassBody = new bool[structuralLines.Length];
        var scopeStack = new Stack<bool>();
        scopeStack.Push(false);
        var declBuffer = new StringBuilder(256);

        for (int lineIndex = 0; lineIndex < structuralLines.Length; lineIndex++)
        {
            lineStartInsideClassBody[lineIndex] = scopeStack.Peek();

            var line = structuralLines[lineIndex];
            for (int cursor = 0; cursor < line.Length; cursor++)
            {
                var ch = line[cursor];
                if (ch == '{')
                {
                    var isClassBody = DartClassDeclarationRegex.IsMatch(declBuffer.ToString());
                    scopeStack.Push(isClassBody);
                    declBuffer.Clear();
                }
                else if (ch == '}')
                {
                    if (scopeStack.Count > 1)
                        scopeStack.Pop();
                    declBuffer.Clear();
                }
                else if (ch == ';')
                {
                    declBuffer.Clear();
                }
                else
                {
                    declBuffer.Append(ch);
                }
            }
        }

        return new DartClassBodyScope(lineStartInsideClassBody);
    }
}
