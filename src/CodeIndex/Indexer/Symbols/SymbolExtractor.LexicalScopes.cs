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
