using System.Text;
using System.Text.RegularExpressions;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    // Expand a C# plain-field regex match into one entry per declarator when the
    // declaration is a declarator list such as `int _x, _y;`, `int _x = 5, _y;`,
    // or `int _x = 5, _y = 10;`. Two shapes need to be stitched back together:
    //
    //  1. When the later declarators have no initializer, the field regex backtracks
    //     until the first declarator with `=` or `;` terminates. Earlier names get
    //     swallowed into `returnType` (e.g. `int _x, _y;` -> returnType=`int _x,`,
    //     name=`_y`). Recover them by splitting `returnType` on top-level commas and
    //     treating the last captured name as the trailing declarator.
    //
    //  2. When the first declarator carries an initializer, the regex terminates at
    //     `=` and leaves the comma-separated tail unconsumed (e.g. `int _x = 5, _y;`
    //     -> returnType=`int`, name=`_x`, tail=` 5, _y;`). Walk the tail after the
    //     match to pick up additional names and their optional initializers.
    //
    // Returns null when the match is a single declarator.
    // C# の通常フィールド用 regex が `int _x, _y;` / `int _x = 5, _y;` /
    // `int _x = 5, _y = 10;` のような declarator list を捕まえた場合に、
    // 各 declarator を 1 件ずつのシンボルに展開する。復元すべき形は 2 通り:
    //
    //  1. 後段 declarator に初期化式が無い場合、regex は最初の `=` か `;` まで
    //     バックトラックし、前段の名前は returnType に吸収される
    //     （`int _x, _y;` → returnType=`int _x,`、name=`_y`）。returnType を
    //     トップレベルの `,` で分割し、regex が捕まえた最後の name を末尾の
    //     declarator として繋ぎ直す。
    //
    //  2. 先頭 declarator が初期化式を持つ場合、regex は `=` で終了し、
    //     `,` で続く後段 declarator はマッチ後のテールに残る
    //     （`int _x = 5, _y;` → returnType=`int`、name=`_x`、tail=` 5, _y;`）。
    //     マッチ末尾以降のテールを走査して追加の declarator を拾う。
    //
    // declarator list でないときは null を返す。
    private static List<(string Name, string? ReturnType)>? TryExpandCSharpFieldDeclaratorList(
        string patternMatchLine,
        int absoluteStartColumn,
        Match match,
        string? returnTypeGroup,
        string finalName)
    {
        if (string.IsNullOrEmpty(returnTypeGroup))
            return null;
        var returnTypeGroupMatch = match.Groups[returnTypeGroup];
        if (!returnTypeGroupMatch.Success)
            return null;

        var returnTypeRaw = returnTypeGroupMatch.Value;
        if (string.IsNullOrEmpty(returnTypeRaw))
            return null;

        var matchEnd = absoluteStartColumn + match.Length;
        if (matchEnd > patternMatchLine.Length)
            matchEnd = patternMatchLine.Length;
        var matchEndedAtEquals = matchEnd > 0 && patternMatchLine[matchEnd - 1] == '=';
        var tailText = matchEnd < patternMatchLine.Length
            ? patternMatchLine[matchEnd..]
            : string.Empty;

        var hasCommaInReturnType = ContainsCSharpTopLevelComma(returnTypeRaw);
        var tailDeclaratorNames = ScanCSharpTailDeclaratorNames(tailText, matchEndedAtEquals);

        if (!hasCommaInReturnType && tailDeclaratorNames.Count == 0)
            return null;

        string actualType;
        var results = new List<(string Name, string? ReturnType)>();

        if (hasCommaInReturnType)
        {
            var segments = SplitCSharpTopLevelComma(returnTypeRaw);
            if (segments.Count < 2)
                return null;
            // Expect a trailing empty segment (returnType ends with `,`). If not,
            // the comma is at an unexpected position — bail out.
            // returnType は末尾が `,` なので最後のセグメントは空のはず。そうで
            // なければ想定外の位置にある `,` なので展開を諦める。
            if (segments[^1].Trim().Length != 0)
                return null;
            segments.RemoveAt(segments.Count - 1);
            if (segments.Count < 1)
                return null;

            var firstSegment = segments[0].Trim();
            if (!TrySplitCSharpFieldTypeAndName(firstSegment, out actualType, out var firstDeclaratorName))
                return null;
            results.Add((firstDeclaratorName, actualType));

            for (int i = 1; i < segments.Count; i++)
            {
                var segment = segments[i].Trim();
                var declaratorName = StripCSharpDeclaratorInitializer(segment);
                if (string.IsNullOrEmpty(declaratorName) || !IsCSharpIdentifier(declaratorName))
                    return null;
                results.Add((declaratorName, actualType));
            }

            if (!string.IsNullOrEmpty(finalName) && IsCSharpIdentifier(finalName))
                results.Add((finalName, actualType));
        }
        else
        {
            actualType = returnTypeRaw.Trim();
            if (!string.IsNullOrEmpty(finalName) && IsCSharpIdentifier(finalName))
                results.Add((finalName, actualType));
        }

        foreach (var tailName in tailDeclaratorNames)
        {
            results.Add((tailName, actualType));
        }

        return results.Count > 1 ? results : null;
    }

    private static List<string> ScanCSharpTailDeclaratorNames(string tail, bool matchEndedAtEquals)
    {
        var result = new List<string>();
        var i = 0;

        if (matchEndedAtEquals)
        {
            // Skip the initializer value until the next top-level `,` or `;`.
            // 初期化式を `,` / `;` に到達するまで読み飛ばす。
            i = SkipCSharpTopLevelValue(tail, 0);
            if (i >= tail.Length || tail[i] == ';')
                return result;
            if (tail[i] == ',')
                i++;
        }
        else
        {
            // A plain-field match that ended at `;` is a complete declaration with no
            // continuation declarators — whatever follows on the same line belongs to a
            // separate statement (e.g. a second `public int B;` on the same line inside
            // a same-line class body). Treating that residual text as `A, <tail>` would
            // pick up stray tokens like `public` and emit phantom declarator symbols.
            // Multi-declarator forms like `public int A, B;` already flow through the
            // `hasCommaInReturnType` branch in TryExpandCSharpFieldDeclaratorList, so
            // returning empty here only disables the buggy `;`-separated path.
            // Closes #400.
            // `;` で終わった plain-field マッチは、それ自体で宣言が完結しており、同一行に
            // 続く内容（例: 同一行 class 本体内の 2 つ目の `public int B;`）は別の文。
            // ここで tail をスキャンすると `public` のような周辺トークンが declarator 名
            // として拾われ、phantom シンボルになる。`public int A, B;` のような多重
            // declarator は TryExpandCSharpFieldDeclaratorList の `hasCommaInReturnType`
            // 経路で既に処理されるため、このガードは `;` 区切り経路の誤検出だけを
            // 無効化する。Closes #400.
            return result;
        }

        while (i < tail.Length)
        {
            while (i < tail.Length && char.IsWhiteSpace(tail[i]))
                i++;
            if (i >= tail.Length || tail[i] == ';')
                break;
            if (tail[i] != '_' && !char.IsLetter(tail[i]))
                break;

            var start = i;
            while (i < tail.Length && (tail[i] == '_' || char.IsLetterOrDigit(tail[i])))
                i++;
            var name = tail[start..i];
            if (!IsCSharpIdentifier(name))
                break;
            result.Add(name);

            i = SkipCSharpTopLevelValue(tail, i);
            if (i >= tail.Length || tail[i] == ';')
                break;
            if (tail[i] == ',')
            {
                i++;
                continue;
            }
            break;
        }

        return result;
    }

    private static int SkipCSharpTopLevelValue(string text, int start)
    {
        int paren = 0, bracket = 0, brace = 0;
        int i = start;
        while (i < text.Length)
        {
            var ch = text[i];
            // Distinguish generic `<...>` brackets from comparison operators by
            // looking ahead for a matching `>` before any char that cannot appear
            // inside a type expression. Without this guard, `_a = x < y ? 1 : 2, _b;`
            // inflates angle depth indefinitely and silently drops `_b`.
            // `<...>` を比較演算子と区別するため、型表現に含まれない文字が現れる前に
            // 対応する `>` が見つかるかを先読みする。これを入れないと
            // `_a = x < y ? 1 : 2, _b;` のように比較演算子を含む初期化式で angle 深さが
            // 0 に戻らず後続 declarator が静かに消える。
            if (ch == '<')
            {
                if (TryMatchCSharpGenericBracket(text, i, out var genericEnd))
                {
                    i = genericEnd + 1;
                    continue;
                }

                i++;
                continue;
            }

            switch (ch)
            {
                case '(': paren++; i++; continue;
                case ')' when paren > 0: paren--; i++; continue;
                case '[': bracket++; i++; continue;
                case ']' when bracket > 0: bracket--; i++; continue;
                case '{': brace++; i++; continue;
                case '}' when brace > 0: brace--; i++; continue;
            }

            if ((ch == ',' || ch == ';') && paren == 0 && bracket == 0 && brace == 0)
                return i;

            i++;
        }
        return text.Length;
    }

    // Look ahead from `<` at `ltIndex` and report the position of the matching `>`
    // if the span looks like a generic type argument list. Returns false when the
    // span contains a character that cannot appear inside a type expression, in
    // which case callers should treat the original `<` as a comparison operator.
    // `<` の位置から先読みし、型引数リストに見える範囲で対応する `>` の位置を返す。
    // 型に現れない文字が途中で出てきた時点で false を返し、呼び出し側はその `<` を
    // 比較演算子として扱う。
    private static bool TryMatchCSharpGenericBracket(string text, int ltIndex, out int endIndex)
    {
        int depth = 1;
        for (int i = ltIndex + 1; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '<')
            {
                depth++;
                continue;
            }
            if (ch == '>')
            {
                depth--;
                if (depth == 0)
                {
                    endIndex = i;
                    return true;
                }
                continue;
            }
            if (ch == ';' || ch == '{' || ch == '}' || ch == '=' || ch == '+' || ch == '-'
                || ch == '/' || ch == '%' || ch == '&' || ch == '|' || ch == '!'
                || ch == '^' || ch == '~')
            {
                endIndex = -1;
                return false;
            }
        }
        endIndex = -1;
        return false;
    }

    private static bool ContainsCSharpTopLevelComma(string text)
    {
        int angle = 0, paren = 0, bracket = 0, brace = 0;
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '<': angle++; break;
                case '>' when angle > 0: angle--; break;
                case '(': paren++; break;
                case ')' when paren > 0: paren--; break;
                case '[': bracket++; break;
                case ']' when bracket > 0: bracket--; break;
                case '{': brace++; break;
                case '}' when brace > 0: brace--; break;
                case ',' when angle == 0 && paren == 0 && bracket == 0 && brace == 0:
                    return true;
            }
        }
        return false;
    }

    private static List<string> SplitCSharpTopLevelComma(string text)
    {
        var result = new List<string>(2);
        var segment = new StringBuilder(text.Length);
        int angle = 0, paren = 0, bracket = 0, brace = 0;
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '<': angle++; segment.Append(ch); continue;
                case '>' when angle > 0: angle--; segment.Append(ch); continue;
                case '(': paren++; segment.Append(ch); continue;
                case ')' when paren > 0: paren--; segment.Append(ch); continue;
                case '[': bracket++; segment.Append(ch); continue;
                case ']' when bracket > 0: bracket--; segment.Append(ch); continue;
                case '{': brace++; segment.Append(ch); continue;
                case '}' when brace > 0: brace--; segment.Append(ch); continue;
            }

            if (ch == ',' && angle == 0 && paren == 0 && bracket == 0 && brace == 0)
            {
                result.Add(segment.ToString());
                segment.Clear();
                continue;
            }

            segment.Append(ch);
        }
        result.Add(segment.ToString());
        return result;
    }

    private static bool TrySplitCSharpFieldTypeAndName(string segment, out string type, out string name)
    {
        type = string.Empty;
        name = string.Empty;
        if (string.IsNullOrEmpty(segment))
            return false;

        // Strip initializer portion if any (e.g. `int _x = 5` -> `int _x`).
        segment = StripCSharpDeclaratorInitializer(segment);
        if (string.IsNullOrEmpty(segment))
            return false;

        int angle = 0, paren = 0, bracket = 0;
        var lastWhitespaceIndex = -1;
        for (int i = 0; i < segment.Length; i++)
        {
            var ch = segment[i];
            switch (ch)
            {
                case '<': angle++; continue;
                case '>' when angle > 0: angle--; continue;
                case '(': paren++; continue;
                case ')' when paren > 0: paren--; continue;
                case '[': bracket++; continue;
                case ']' when bracket > 0: bracket--; continue;
            }

            if (angle == 0 && paren == 0 && bracket == 0 && char.IsWhiteSpace(ch))
                lastWhitespaceIndex = i;
        }

        if (lastWhitespaceIndex <= 0)
            return false;

        type = SliceCSharpTrimEnd(segment, lastWhitespaceIndex);
        name = SliceCSharpTrim(segment, lastWhitespaceIndex + 1, segment.Length);
        return !string.IsNullOrEmpty(type) && IsCSharpIdentifier(name);
    }

    private static string StripCSharpDeclaratorInitializer(string segment)
    {
        int angle = 0, paren = 0, bracket = 0, brace = 0;
        for (int i = 0; i < segment.Length; i++)
        {
            var ch = segment[i];
            switch (ch)
            {
                case '<': angle++; continue;
                case '>' when angle > 0: angle--; continue;
                case '(': paren++; continue;
                case ')' when paren > 0: paren--; continue;
                case '[': bracket++; continue;
                case ']' when bracket > 0: bracket--; continue;
                case '{': brace++; continue;
                case '}': if (brace > 0) brace--; continue;
            }

            if (ch == '=' && angle == 0 && paren == 0 && bracket == 0 && brace == 0)
            {
                // Skip `==` / `=>` -- not initializers.
                if (i + 1 < segment.Length && (segment[i + 1] == '=' || segment[i + 1] == '>'))
                    continue;
                return SliceCSharpTrimEnd(segment, i);
            }
        }
        return SliceCSharpTrimEnd(segment, segment.Length);
    }

    private static string SliceCSharpTrimEnd(string text, int endExclusive)
    {
        endExclusive = Math.Clamp(endExclusive, 0, text.Length);
        while (endExclusive > 0 && char.IsWhiteSpace(text[endExclusive - 1]))
            endExclusive--;

        return endExclusive == text.Length ? text : text[..endExclusive];
    }

    private static string SliceCSharpTrim(string text, int start, int endExclusive)
    {
        start = Math.Clamp(start, 0, text.Length);
        endExclusive = Math.Clamp(endExclusive, start, text.Length);

        while (start < endExclusive && char.IsWhiteSpace(text[start]))
            start++;

        while (endExclusive > start && char.IsWhiteSpace(text[endExclusive - 1]))
            endExclusive--;

        return start == 0 && endExclusive == text.Length ? text : text[start..endExclusive];
    }

    private static bool IsCSharpIdentifier(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        if (text[0] != '_' && !char.IsLetter(text[0]))
            return false;
        for (int i = 1; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch != '_' && !char.IsLetterOrDigit(ch))
                return false;
        }
        return true;
    }
}
