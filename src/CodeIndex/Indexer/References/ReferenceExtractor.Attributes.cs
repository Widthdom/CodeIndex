using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static string? TryClassifyMetadataReference(
        string language,
        string preparedLine,
        int nameIndex,
        bool insideCSharpAttributeRange)
    {
        if (language == "csharp")
            return insideCSharpAttributeRange ? "attribute" : null;

        if (nameIndex >= 0
            && nameIndex < preparedLine.Length
            && preparedLine[nameIndex] == '@'
            && AnnotationLanguages.Contains(language))
        {
            return "annotation";
        }

        var probe = nameIndex - 1;
        while (probe >= 0 && char.IsWhiteSpace(preparedLine[probe]))
            probe--;
        if (probe < 0)
            return null;

        if (AnnotationLanguages.Contains(language))
            return IsAnnotationContext(preparedLine, probe) ? "annotation" : null;

        return null;
    }

    /// <summary>
    /// Build per-line column ranges that identify C# `[...]` attribute sections. Handles
    /// declaration-position detection (including parameter attributes preceded by `(` / `,`
    /// via forward look-ahead) and multi-line `[\n ... \n]` sections. Each inner list holds
    /// ordered `(startColumn, endColumnExclusive)` ranges that are inside an attribute section
    /// on that line. Call sites whose name column falls inside one of these ranges are
    /// reclassified as `attribute` instead of `call`.
    /// C# の `[...]` 属性セクションを行ごとの列範囲で表すテーブルを構築する。
    /// `(` / `,` の直後に置かれるパラメータ属性を forward lookahead で、複数行にわたる
    /// `[\n ... \n]` 属性を跨行トラッキングで検出する。各行のリストは属性セクションに含まれる
    /// `(開始列, 終端列 (exclusive))` のレンジを保持し、呼び出し名の列がどれかのレンジに含まれる場合に
    /// `call` ではなく `attribute` へ再分類する。
    /// </summary>
    private static (List<(int start, int end)>?[] Ranges, List<(int start, int end)>?[] TopLevelRanges) BuildCSharpAttributeRanges(string[] preparedLines)
    {
        var perLine = new List<(int start, int end)>?[preparedLines.Length];
        var perLineTopLevel = new List<(int start, int end)>?[preparedLines.Length];

        // Stack entries capture the opening `[` position, whether that bracket was at
        // a C# declaration (attribute) position, and a snapshot of the global paren depth
        // at that moment. The snapshot lets us compute an attribute-section-local paren
        // depth (`parenDepth - parenDepthAtOpen`), which is what the top-level zone tracking
        // uses so that parameter attributes like `void M([Attr] int x)` still have their
        // attribute-list top level at section-local depth 0 even though the global depth
        // is inside the method's parameter list.
        // スタックは `[` の位置、その bracket が属性位置だったか、および開いた瞬間の
        // グローバル paren 深さのスナップショットを保持する。スナップショットを使うと
        // 属性セクション内ローカルの paren 深さ (`parenDepth - parenDepthAtOpen`) が
        // 得られるので、`void M([Attr] int x)` のように外側の method 引数リストの中で
        // 開く属性セクションでも、セクション内では top-level (local depth 0) として扱える。
        var bracketStack = new Stack<(int li, int ci, bool isAttr, int parenDepthAtOpen)>();
        char lastMeaningful = '\0';
        int parenDepth = 0;
        bool lastClosedBracketWasAttribute = false;

        // Top-level zone tracking: while we are inside an attribute section and the paren
        // depth is at the section's open snapshot (section-local depth 0), the current zone
        // span is open. When parens open inside the section we close it; when they fully
        // close again we reopen. When the attribute section itself closes, we emit the span.
        // top-level ゾーン追跡: 属性セクション内かつセクションローカルの paren 深さが 0 の
        // あいだだけゾーンを開いておき、セクション内の `(` で閉じ、`)` で再び開く。
        // セクションが閉じる `]` で確定させる。
        int topZoneStartLi = -1;
        int topZoneStartCi = 0;

        void EmitTopZone(int endLi, int endCi)
        {
            if (topZoneStartLi < 0)
                return;
            for (var l = topZoneStartLi; l <= endLi; l++)
            {
                int s = (l == topZoneStartLi) ? topZoneStartCi : 0;
                int e = (l == endLi) ? endCi : preparedLines[l].Length;
                if (e > s)
                    AddCSharpAttributeRange(perLineTopLevel, l, s, e);
            }
            topZoneStartLi = -1;
        }

        for (var li = 0; li < preparedLines.Length; li++)
        {
            var line = preparedLines[li];
            for (var ci = 0; ci < line.Length; ci++)
            {
                var c = line[ci];
                if (c == '/' && ci + 1 < line.Length && line[ci + 1] == '/')
                    break;

                if (char.IsWhiteSpace(c))
                    continue;

                if (c == '(')
                {
                    // If the innermost enclosing bracket is an attribute section and we are
                    // currently at that section's local top level, close the top-level zone
                    // just before the `(`. Use the stack top's `parenDepthAtOpen` snapshot so
                    // parameter attributes inside an outer `(...)` still get their top level
                    // tracked correctly.
                    // 直近の `[` が属性セクションで、かつその section-local 深さで top-level のとき、
                    // `(` 直前でゾーンを閉じる。外側の `(...)` の中で開く属性セクションにも対応するため、
                    // グローバル depth ではなくスタック top の開いたときの snapshot と比較する。
                    if (bracketStack.Count > 0)
                    {
                        var top = bracketStack.Peek();
                        if (top.isAttr && parenDepth == top.parenDepthAtOpen && topZoneStartLi >= 0)
                            EmitTopZone(li, ci);
                    }
                    parenDepth++;
                    lastMeaningful = c;
                    continue;
                }
                if (c == ')')
                {
                    if (parenDepth > 0)
                    {
                        parenDepth--;
                        // If the innermost `[` is an attribute section and we just returned
                        // to that section's local top level, reopen the top-level zone.
                        // 直近の `[` が属性セクションで、section-local top-level に戻ってきたら
                        // top-level ゾーンを再開する。
                        if (bracketStack.Count > 0)
                        {
                            var top = bracketStack.Peek();
                            if (top.isAttr && parenDepth == top.parenDepthAtOpen && topZoneStartLi < 0)
                            {
                                topZoneStartLi = li;
                                topZoneStartCi = ci + 1;
                            }
                        }
                    }
                    lastMeaningful = c;
                    continue;
                }

                if (c == '[')
                {
                    bool isAttr = EvaluateCSharpAttributePosition(
                        lastMeaningful, lastClosedBracketWasAttribute, preparedLines, li, ci);
                    bracketStack.Push((li, ci, isAttr, parenDepth));
                    if (isAttr && topZoneStartLi < 0)
                    {
                        // Start top-level zone just after the `[` so the `[` itself is not
                        // inside the zone. Section-local depth is 0 by construction at the
                        // open bracket.
                        // `[` 直後から top-level ゾーンを開始する。開いた瞬間は section-local 深さ 0。
                        topZoneStartLi = li;
                        topZoneStartCi = ci + 1;
                    }
                    lastMeaningful = c;
                    continue;
                }

                if (c == ']')
                {
                    if (bracketStack.Count > 0)
                    {
                        var opened = bracketStack.Pop();
                        lastClosedBracketWasAttribute = opened.isAttr;
                        if (opened.isAttr)
                        {
                            // Record the attribute section span for every line it covers so
                            // cross-line `[\n Foo("x")\n]` also classifies `Foo` as attribute.
                            // 属性セクションがまたぐ全ての行に対して範囲を記録し、
                            // `[\n Foo("x")\n]` のような跨行ケースでも `Foo` が属性として分類されるようにする。
                            for (var l = opened.li; l <= li; l++)
                            {
                                int s = (l == opened.li) ? opened.ci : 0;
                                int e = (l == li) ? ci + 1 : preparedLines[l].Length;
                                AddCSharpAttributeRange(perLine, l, s, e);
                            }
                            // Close the top-level zone at the `]`. Section-local depth should
                            // be 0 here (we are at the closing bracket of this section) — if
                            // it is not, we drop the open zone because paren balancing was
                            // malformed.
                            // `]` で top-level ゾーンを確定する。section-local 深さが 0 のはず。
                            // 不整合入力ならゾーンを捨てる。
                            if (parenDepth == opened.parenDepthAtOpen)
                            {
                                EmitTopZone(li, ci + 1);
                            }
                            else
                            {
                                topZoneStartLi = -1;
                            }
                        }
                    }
                    else
                    {
                        lastClosedBracketWasAttribute = false;
                    }
                    lastMeaningful = c;
                    continue;
                }

                lastMeaningful = c;
            }
        }

        return (perLine, perLineTopLevel);
    }

    private static void AddCSharpAttributeRange(
        List<(int start, int end)>?[] rangesByLine,
        int lineIndex,
        int start,
        int end)
    {
        (rangesByLine[lineIndex] ??= []).Add((start, end));
    }

    /// <summary>
    /// Decide whether a `[` token sits at a C# attribute position based on the immediately
    /// preceding meaningful character. `(` / `,` (parameter attributes) are disambiguated via
    /// forward look-ahead because both attributes and C# 12 collection expressions can follow.
    /// `[` が C# の属性位置にあるかを、直前の非空白文字から判定する。`(` / `,` の直後は
    /// パラメータ属性にも collection expression にもなりうるため、forward lookahead で区別する。
    /// </summary>
    private static bool EvaluateCSharpAttributePosition(
        char lastMeaningful,
        bool lastClosedBracketWasAttribute,
        string[] preparedLines,
        int startLi,
        int startCi)
    {
        // Start of file or after a scope/statement boundary — attribute position.
        // ファイル先頭、あるいはスコープ・文境界の直後は属性位置。
        if (lastMeaningful is '\0' or '{' or '}' or ';')
            return true;

        // Chained attribute list `[A][B]`: the prior `]` must have closed an attribute section.
        // `arr[i][Compute()]` → the prior `]` closed an indexer, so stays `call`.
        // 連続した属性リスト `[A][B]` は、直前の `]` が属性セクションを閉じていたときのみ属性扱い。
        // `arr[i][Compute()]` の `]` は indexer を閉じているため `call` のまま。
        if (lastMeaningful == ']')
            return lastClosedBracketWasAttribute;

        // Parameter / type-parameter / lambda attribute candidates (`(`, `,`, `<`, `=`):
        // `void M([Attr] T x)`, `class C<[Attr] T>`, `var f = [Attr] () => body`, or
        // `Consume([Make()])`. Disambiguate by scanning forward to the matching `]` and
        // checking whether the next meaningful token begins a declaration (identifier /
        // `@` / `(` for tuple types or lambda parameter lists / `[` chained).
        // パラメータ / 型パラメータ / ラムダ属性候補 (`(`, `,`, `<`, `=`) は
        // `void M([Attr] T x)`・`class C<[Attr] T>`・`var f = [Attr] () => body`・
        // `Consume([Make()])` いずれにもなりうる。対応する `]` まで進んで次トークンが
        // 宣言やラムダを開始するか（識別子 / `@` / tuple・ラムダ仮引数の `(` / chained `[`）で区別する。
        if (lastMeaningful is '(' or ',' or '<' or '=')
            return IsCSharpAttributeFollowedByDeclaration(preparedLines, startLi, startCi);

        return false;
    }

    /// <summary>
    /// Keywords that indicate the preceding `[...]` is an expression (collection / pattern /
    /// switch target) rather than an attribute section when they appear after `]`.
    /// `]` の直後に現れると、直前の `[...]` が属性ではなく式（collection / pattern / switch 対象）
    /// であることを示す C# のキーワード集合。
    /// </summary>
    private static readonly HashSet<string> CSharpExpressionContinuationKeywords = new(StringComparer.Ordinal)
    {
        "is", "as", "switch", "with", "when",
    };

    /// <summary>
    /// Scan forward from a `[` to its matching `]` (skipping balanced parens) and return true
    /// when the next meaningful character begins an identifier-like token. Works across lines so
    /// `void M(\n    [Attr]\n    T x\n)` is recognized as a parameter attribute.
    /// `[` から対応する `]` まで進んで、`]` の次の非空白文字が識別子を始める場合に true を返す。
    /// 行を跨ぐ走査にも対応しているため `void M(\n    [Attr]\n    T x\n)` も属性として認識される。
    /// </summary>
    private static bool IsCSharpAttributeFollowedByDeclaration(string[] preparedLines, int startLi, int startCi)
    {
        var bracketDepth = 1;
        var parenDepth = 0;
        var li = startLi;
        var ci = startCi + 1;
        while (li < preparedLines.Length)
        {
            var line = preparedLines[li];
            while (ci < line.Length)
            {
                var c = line[ci];
                if (c == '/' && ci + 1 < line.Length && line[ci + 1] == '/' && parenDepth == 0)
                    break;

                if (c == '(')
                {
                    parenDepth++;
                    ci++;
                    continue;
                }
                if (c == ')')
                {
                    if (parenDepth > 0)
                        parenDepth--;
                    ci++;
                    continue;
                }
                if (parenDepth > 0)
                {
                    ci++;
                    continue;
                }
                if (c == '[')
                {
                    bracketDepth++;
                    ci++;
                    continue;
                }
                if (c == ']')
                {
                    bracketDepth--;
                    if (bracketDepth == 0)
                    {
                        ci++;
                        return NextTokenStartsDeclaration(preparedLines, li, ci);
                    }
                    ci++;
                    continue;
                }
                ci++;
            }
            li++;
            ci = 0;
        }
        return false;
    }

    /// <summary>
    /// After the closing `]` of a candidate `[...]`, inspect the next meaningful token to decide
    /// whether it begins a declaration. Accepts identifiers (except expression-continuation
    /// keywords like `is` / `as` / `switch` / `with` / `when`), leading `@` (verbatim identifier),
    /// `(` (tuple-typed parameter), and chained `[` (recurse for `[A][B]`).
    /// 閉じ `]` の直後のトークンで宣言が始まるかを判定する。識別子（式継続の `is` / `as` /
    /// `switch` / `with` / `when` は除外）、`@`（verbatim 識別子）、`(`（tuple パラメータ型）、
    /// `[`（`[A][B]` の連結）を受け入れる。
    /// </summary>
    private static bool NextTokenStartsDeclaration(string[] preparedLines, int li, int ci)
    {
        while (li < preparedLines.Length)
        {
            var line = preparedLines[li];
            while (ci < line.Length && char.IsWhiteSpace(line[ci]))
                ci++;
            if (ci < line.Length)
            {
                var first = line[ci];
                if (first == '@' || first == '(')
                    return true;
                if (first == '[')
                    return IsCSharpAttributeFollowedByDeclaration(preparedLines, li, ci);
                if (!IsIdentifierChar(first))
                    return false;
                var start = ci;
                while (ci < line.Length && IsIdentifierChar(line[ci]))
                    ci++;
                var token = line.Substring(start, ci - start);
                return !CSharpExpressionContinuationKeywords.Contains(token);
            }
            li++;
            ci = 0;
        }
        return false;
    }

    private static bool IsInsideCSharpAttributeRange(IReadOnlyList<(int start, int end)> ranges, int index)
    {
        for (var i = 0; i < ranges.Count; i++)
        {
            var (start, end) = ranges[i];
            if (index >= start && index < end)
                return true;
        }
        return false;
    }

    private static bool IsAnnotationContext(string line, int probe)
    {
        // `@Annotation(args)` — direct marker. 直接 `@Annotation(args)` の場合。
        if (line[probe] == '@')
            return true;

        // `@module.Annotation(args)` — walk past the dotted qualifier chain first so that
        // both `@module.Annotation` and `@field:com.example.Annotation` land the probe on
        // either `@` or the Kotlin use-site target `:`.
        // `@module.Annotation(args)` や `@field:com.example.Annotation(args)` のように修飾子が
        // 付く場合も対応するため、先にドット区切り修飾子チェーンを剥がしてから `@` または
        // Kotlin の use-site target `:` を判定する。
        while (probe >= 0 && line[probe] == '.')
        {
            probe--;
            while (probe >= 0 && IsIdentifierChar(line[probe]))
                probe--;
            while (probe >= 0 && char.IsWhiteSpace(line[probe]))
                probe--;
        }

        if (probe < 0)
            return false;

        if (line[probe] == '@')
            return true;

        // Kotlin use-site target: `@field:Deprecated("msg")` or
        // `@field:com.example.Deprecated("msg")`. After unwinding the dotted qualifier, the
        // probe lands on `:`; walk past the target identifier and confirm `@`.
        // Kotlin の use-site target `@field:Deprecated("msg")` や
        // `@field:com.example.Deprecated("msg")` では、ドット修飾子を剥がしたあと probe が `:`
        // に着地するため、target 識別子を読み飛ばして `@` を確認する。
        if (line[probe] == ':')
        {
            var j = probe - 1;
            var idEnd = j;
            while (j >= 0 && IsIdentifierChar(line[j]))
                j--;
            if (j + 1 <= idEnd)
            {
                var target = line[(j + 1)..(idEnd + 1)];
                if (KotlinAnnotationTargets.Contains(target))
                {
                    var k = j;
                    while (k >= 0 && char.IsWhiteSpace(line[k]))
                        k--;
                    if (k >= 0 && line[k] == '@')
                        return true;
                }
            }
        }

        return false;
    }

    private static bool UsesHashComments(string lang) =>
        lang is "python" or "ruby" or "perl" or "php" or "elixir" or "r" or "powershell"
            or "shell" or "makefile" or "terraform" or "dockerfile" or "protobuf"
            or "nim" or "julia" or "cython";

    private static bool UsesSlashComments(string lang) =>
        lang is not "python" and not "ruby" and not "r" and not "haskell"
            and not "makefile" and not "terraform" and not "dockerfile"
            and not "css" and not "fortran" and not "crystal" and not "tcl"
            and not "prolog" and not "ambiguous_pl" and not "nim" and not "matlab"
            and not "julia" and not "cython" and not "ada";

    private static bool UsesDashDashComments(string lang) =>
        lang is "lua" or "sql" or "haskell" or "ada";


}
