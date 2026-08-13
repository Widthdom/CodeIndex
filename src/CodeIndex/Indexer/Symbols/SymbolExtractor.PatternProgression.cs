namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static PatternScanResult ResolveCSharpFieldProgression(
        MatchedPatternCandidateContext candidate)
    {
        var lineContext = candidate.LineContext;
        var extraction = lineContext.Extraction;
        var lang = extraction.Lang;
        var pattern = candidate.Pattern;
        var patternMatchLine = candidate.PatternMatchLine;
        var lineOffset = candidate.LineOffset;
        var absoluteStartColumn = candidate.CapturedMatch.AbsoluteStartColumn;
        var i = lineContext.LineIndex;
        var csharpMatchLines = extraction.ScanInputs.CSharpMatchLines;
        // C# plain-field matches (internally tagged as `property`, BodyStyle.None,
        // then normalized to public `field`) need their own advance path. The
        // generic `sameLineEndColumn`-based advance below resolves
        // to -1 for BodyStyle.None and would set `stopAfterFirstPatternMatch`, which
        // prevents structural siblings on the same line (e.g. the enclosing
        // `public class C` in `public class C { public int X; }`) from being
        // captured by later patterns. Instead, advance past the field terminator
        // and continue the same-pattern scan so multiple same-line fields are
        // still collected, and skip the stop flag so later patterns can still run.
        // Closes #400.
        // C# 通常フィールド（内部タグは `property`、BodyStyle.None、公開時に
        // `field` へ正規化）は専用の前進経路を使う。
        // 既定の `sameLineEndColumn` ベースの前進は BodyStyle.None では -1 に落ち、
        // `stopAfterFirstPatternMatch` を立ててしまうため、同一行に存在する構造宣言
        // （例: `public class C { public int X; }` の外側 class）を後続パターンで
        // 取得できなくなる。代わりにフィールド終端を越えて同一パターンのスキャンを
        // 続け、stop フラグを立てずに次のパターンにも機会を残す。Closes #400.
        if (lang == "csharp"
            && pattern.Kind == "property"
            && pattern.BodyStyle == BodyStyle.None)
        {
            // Advance past the end of the full field declaration statement
            // (the top-level `;`, with paren / bracket / brace depth tracking
            // so `{` / `;` inside an initializer cannot short-circuit the
            // scan) and continue. Using the statement end rather than the
            // regex match end keeps later same-line field statements visible
            // to the same pattern: without this, `A = 1; B;` stopped after
            // capturing `A` and dropped `B`, and `A, B; C;` stopped after
            // expanding `A, B` as a declarator list and dropped `C`. It also
            // avoids the earlier regression where advancing to the match end
            // (which sits on `=` when the field has an initializer) made the
            // regex re-match the tail `1, _b, _c =` as a bogus field with
            // `return_type = "1, _b,"`. If the scanner hits an unbalanced
            // `}` (the closing brace of the enclosing type body) before a
            // `;`, break out without setting `stopAfterFirstPatternMatch` so
            // later unrelated patterns on the same line still get a chance
            // to run. Closes #400.
            // フィールド宣言文全体の終端（`;`、paren / bracket / brace 深さを
            // 追って初期化子内の `{` や `;` で途切れないようにする）まで進めて
            // 同一パターンで scan を続ける。regex match の末尾ではなく文の
            // 終端で advance するのが肝心で、これが無いと `A = 1; B;` は
            // `A` を拾った時点で止まって `B` を取り落とし、`A, B; C;` は
            // `A, B` を declarator list として展開した時点で `C` を取り落とす。
            // さらに、match の末尾（初期化子付きなら `=`）まで進めて continue
            // すると正規表現が残りの `1, _b, _c =` を `return_type = "1, _b,"`
            // の偽フィールドとして再マッチしていた旧 regression も再発しない。
            // `;` より先に囲む型本体の閉じ `}`（深さ 0）に到達した場合は、
            // `stopAfterFirstPatternMatch` を立てずに break して同一行の他
            // パターン（class 等）へ機会を残す。Closes #400.
            var statementEnd = FindCSharpSameLineStatementEnd(patternMatchLine, absoluteStartColumn);
            if (statementEnd < patternMatchLine.Length
                && patternMatchLine[statementEnd] == '}')
            {
                return PatternScanResult.NextPattern;
            }
            // Only continue the same-pattern same-line scan when the regex
            // ran on a per-line single-line candidate (patternMatchLine ===
            // csharpMatchLines[i]). For multi-line merged candidates,
            // BuildCSharpPropertyMatchLine joined the header line with one
            // or more continuation lines, so absoluteStartColumn sits in
            // the merged-string column domain and does not line up with
            // lines[i]'s raw columns. Continuing past statementEnd into a
            // second regex hit would then feed a column > lines[i].Length
            // into BuildCSharpMultilineSignature (which slices
            // lines[startLineIndex][startColumn..]) and crash indexing with
            // `startIndex cannot be larger than length of string`. The
            // continuation line is revisited by the outer physical-line
            // loop anyway (csharpSuppressedContinuationUntil is only bumped
            // for expression-bodied properties), so for multi-line merged
            // candidates we break here and let the outer loop handle any
            // additional fields on that line. Closes #400.
            // same-pattern での同一行 scan 継続は、per-line の単一行候補
            // （patternMatchLine === csharpMatchLines[i]）のときだけ許す。
            // BuildCSharpPropertyMatchLine が header 行と continuation 行を
            // マージした複数行候補では、absoluteStartColumn がマージ後文字列の
            // 列を指しており lines[i] の raw 列として使えない。この状態で
            // statementEnd を越えて 2 個目の regex ヒットに進むと、
            // BuildCSharpMultilineSignature の lines[startLineIndex][startColumn..]
            // で範囲外アクセスとなり
            // 「startIndex cannot be larger than length of string」で indexing が
            // 落ちる。continuation 行は外側の物理行ループが再訪する
            // （csharpSuppressedContinuationUntil は expression-bodied property
            // でしか進まない）ため、複数行候補ではここで break して後続の
            // 同一行フィールド抽出を外側ループに任せる。Closes #400.
            if (csharpMatchLines == null
                || !ReferenceEquals(patternMatchLine, csharpMatchLines[i]))
            {
                return PatternScanResult.NextPattern;
            }
            var advance = statementEnd;
            if (advance <= lineOffset)
                advance = lineOffset + 1;
            if (advance >= patternMatchLine.Length)
                return PatternScanResult.NextPattern;
            lineOffset = advance;
            return PatternScanResult.ContinueAt(lineOffset);
        }

        return PatternScanResult.Accepted;
    }

    private static PatternScanResult ResolveTerminalPatternProgression(
        MatchedPatternCandidateContext candidate,
        ShapedPatternSymbol shapedSymbol,
        string emittedKind)
    {
        var lineContext = candidate.LineContext;
        var extraction = lineContext.Extraction;
        var lang = extraction.Lang;
        var pattern = candidate.Pattern;
        var matchLine = lineContext.PreparedLine.MatchLine;
        var lineOffset = candidate.LineOffset;
        var match = candidate.CapturedMatch.Match;
        var absoluteStartColumn = candidate.CapturedMatch.AbsoluteStartColumn;
        var line = lineContext.PreparedLine.SourceLine;
        var i = lineContext.LineIndex;
        var csharpMatchColumnToRaw = extraction.ScanInputs.CSharpMatchColumnToRaw;
        var kind = emittedKind;
        var startLine = shapedSymbol.StartLine;
        var bodyStartLine = shapedSymbol.BodyStartLine;
        var bodyEndLine = shapedSymbol.BodyEndLine;
        var csharpSingleLineCollapsedMatch = shapedSymbol.SignatureResult.Bounds.CSharpSingleLineCollapsedMatch;
        var sameLineEndColumn = shapedSymbol.SignatureResult.Bounds.SameLineEndColumn;
        var sameLineEndUsesRawColumns = shapedSymbol.SignatureResult.Bounds.SameLineEndUsesRawColumns;
        if (!CanContinueScanningSameLineBraceBody(lang, kind, pattern.BodyStyle, bodyEndLine, startLine, sameLineEndColumn, absoluteStartColumn))
        {
            if (lang == "csharp"
                && pattern.BodyStyle == BodyStyle.Brace
                && bodyStartLine == startLine
                && kind is "class" or "struct" or "interface" or "enum" or "namespace")
            {
                // Hybrid same-line C# type headers can open the body on the header
                // line and still close on a later line (`class C { int P { get; }`
                // + next-line `}`). They are not compact same-line bodies, so the
                // generic same-line brace-body path does not restart inside them.
                // Explicitly restart just after the opening `{` so the first member
                // that shares the header line is still visible to the full pattern
                // list. Closes #580.
                // ハイブリッドな C# の same-line 型ヘッダは、本体開始 `{` がヘッダ行に
                // ありつつ閉じ `}` は後続行に置かれうる (`class C { int P { get; }`
                // + 次行 `}`)。これは compact な same-line body ではないため、
                // 既定の same-line brace-body 経路だけでは本体内へ再開できない。
                // そこで開始 `{` の直後から明示的に再開し、ヘッダ行を共有する最初の
                // member も通常の pattern 列で拾えるようにする。Closes #580.
                var nextHeaderLineMemberOffset = FindNextSameLineNonClosingBraceStatementStart(
                    matchLine,
                    absoluteStartColumn + Math.Max(1, match.Length),
                    lang);
                if (nextHeaderLineMemberOffset > absoluteStartColumn
                    && nextHeaderLineMemberOffset < matchLine.Length)
                {
                    return PatternScanResult.RestartAt(nextHeaderLineMemberOffset);
                }
            }

            if (lang == "csharp"
                && sameLineEndColumn >= absoluteStartColumn
                && CanRestartCSharpSameLineSiblingScan(kind))
            {
                // Compact same-line C# members form a sibling stream rather than a
                // single terminal match: after `event E;`, `void M();`, or
                // `int P { get; set; }`, later same-line declarations still need
                // to reach earlier patterns in the list. Restart from the next
                // top-level statement boundary so mixed-kind siblings like
                // `event + property`, `method + property`, and `property + event`
                // are all visible. When there is no later statement, keep the old
                // stop-after-first-match behavior to avoid reopening duplicate
                // paths on ordinary single-declaration lines. Closes #470 / #473.
                // 同一行のコンパクトな C# member は 1 回限りの terminal match ではなく、
                // sibling 宣言のストリームとして扱う。`event E;` や `void M();`、
                // `int P { get; set; }` の後ろに続く宣言も、pattern 列の先頭側にある
                // property などへ到達できる必要がある。そこで次の top-level 文境界から
                // pattern 列全体を再走査し、`event + property`、`method + property`、
                // `property + event` のような mixed-kind sibling をすべて可視化する。
                // 後続宣言が無い行では従来どおり stop-after-first-match を維持し、
                // 通常の単独宣言行で duplicate 経路を再び開かない。Closes #470 / #473.
                if (csharpSingleLineCollapsedMatch && sameLineEndUsesRawColumns)
                {
                    var rawNextSiblingOffset = FindNextSameLineNonClosingBraceStatementStart(line, sameLineEndColumn + 1, lang);
                    if (rawNextSiblingOffset > sameLineEndColumn)
                    {
                        return PatternScanResult.RestartAt(
                            TranslateCSharpRawColumnToCollapsed(
                                csharpMatchColumnToRaw,
                                i,
                                rawNextSiblingOffset,
                                matchLine.Length,
                                line.Length));
                    }
                }
                else
                {
                    var nextSiblingOffset = FindNextSameLineNonClosingBraceStatementStart(matchLine, sameLineEndColumn + 1, lang);
                    if (nextSiblingOffset > sameLineEndColumn
                        && nextSiblingOffset < matchLine.Length)
                    {
                        return PatternScanResult.RestartAt(nextSiblingOffset);
                    }
                }
            }

            // Batch `set` assignments can legitimately repeat on a single line via
            // `&` command-chaining (`set A=1 & set B=2`), parenthesized grouping
            // (`if ... ( set P=1 ) else set Q=2`), or `for`-loop bodies
            // (`for %%I in (1) do set LOOPVAR=%%I`). The brace-body rescan path
            // above is JS/TS/CSS/C#-only, so drive the advance explicitly for the
            // batch property pattern instead of short-circuiting after the first
            // match. Forward progress is guaranteed because `match.Length >= 1`
            // (the regex requires a literal `set\s+NAME=` tail).
            // batch の `set` 代入は `&` 連結や `( ... ) else ... `、`for ... do ...` で
            // 1 行に複数回現れうる。上の brace-body 再スキャンは JS/TS/CSS/C# 限定なので、
            // batch の property パターンだけは explicit に advance して追加マッチも拾う。
            // 前進は `match.Length >= 1` (正規表現が `set\s+NAME=` を要求するため) で保証される。
            if (lang == "batch"
                && pattern.BodyStyle == BodyStyle.None
                && pattern.Kind == "property")
            {
                var nextBatchOffset = absoluteStartColumn + Math.Max(1, match.Length);
                if (nextBatchOffset <= lineOffset)
                    return PatternScanResult.NextPattern;
                lineOffset = nextBatchOffset;
                return PatternScanResult.ContinueAt(lineOffset);
            }

            // Stop after first match per line to avoid duplicate symbols
            // (e.g. C# method pattern + constructor pattern both matching)
            // 1行につき最初のマッチのみ採用し重複を防ぐ
            return PatternScanResult.StopLine;
        }

        return PatternScanResult.Accepted;
    }

    private static PatternScanResult ResolveBraceBodyPatternProgression(
        MatchedPatternCandidateContext candidate,
        ShapedPatternSymbol shapedSymbol,
        string emittedKind)
    {
        var lineContext = candidate.LineContext;
        var extraction = lineContext.Extraction;
        var lang = extraction.Lang;
        var pattern = candidate.Pattern;
        var matchLine = lineContext.PreparedLine.MatchLine;
        var match = candidate.CapturedMatch.Match;
        var absoluteStartColumn = candidate.CapturedMatch.AbsoluteStartColumn;
        var line = lineContext.PreparedLine.SourceLine;
        var i = lineContext.LineIndex;
        var csharpMatchColumnToRaw = extraction.ScanInputs.CSharpMatchColumnToRaw;
        var kind = emittedKind;
        var csharpSingleLineCollapsedMatch = shapedSymbol.SignatureResult.Bounds.CSharpSingleLineCollapsedMatch;
        var sameLineEndColumn = shapedSymbol.SignatureResult.Bounds.SameLineEndColumn;
        var sameLineEndUsesRawColumns = shapedSymbol.SignatureResult.Bounds.SameLineEndUsesRawColumns;
        // For C# class-like kinds with a same-line brace body, step into the body
        // (advance just past the match header) instead of jumping past the closing
        // `}`. This lets nested same-line declarations be captured, e.g.
        // `public class Outer { public class Inner { public int X; } }` matches
        // Outer and Inner, with X correctly attached to Inner. JavaScript/TypeScript
        // does not need this because class-body members there are extracted via the
        // separate JS/TS lexer/state machine; the brace-skip path only handles
        // same-line siblings like `class A {} class B {}`. Closes #400.
        // C# の class 系 kind は同一行の `{...}` 本体を飛び越えず、ヘッダ直後へ
        // 進めて本体内部の宣言（例: `public class Outer { public class Inner { ... } }`
        // の Inner）を拾えるようにする。JavaScript/TypeScript は class body の
        // member 抽出を専用 lexer/state machine で行うため従来通り終端の後ろへ
        // 進め、同一行 sibling（`class A {} class B {}` など）だけを扱う。Closes #400.
        var sameLineRestartComparisonColumn = csharpSingleLineCollapsedMatch && sameLineEndUsesRawColumns
            ? TranslateCSharpRawColumnToCollapsed(
                csharpMatchColumnToRaw,
                i,
                sameLineEndColumn,
                matchLine.Length,
                line.Length)
            : sameLineEndColumn;
        if (CanStepIntoSameLineTypeBody(lang, kind))
        {
            var nextTypeBodyOffset = FindNextSameLineNonClosingBraceStatementStart(
                matchLine,
                absoluteStartColumn + Math.Max(1, match.Length),
                lang);
            if (nextTypeBodyOffset > absoluteStartColumn
                && nextTypeBodyOffset < sameLineRestartComparisonColumn
                && (nextTypeBodyOffset >= matchLine.Length || matchLine[nextTypeBodyOffset] != '}'))
            {
                return PatternScanResult.RestartAt(nextTypeBodyOffset);
            }
        }

        var nextSameLineOffset = -1;
        if (csharpSingleLineCollapsedMatch && sameLineEndUsesRawColumns)
        {
            var rawNextSameLineOffset = FindNextSameLineNonClosingBraceStatementStart(line, sameLineEndColumn + 1, lang);
            if (rawNextSameLineOffset > sameLineEndColumn)
            {
                nextSameLineOffset = TranslateCSharpRawColumnToCollapsed(
                    csharpMatchColumnToRaw,
                    i,
                    rawNextSameLineOffset,
                    matchLine.Length,
                    line.Length);
            }
        }
        else
        {
            nextSameLineOffset = FindNextSameLineNonClosingBraceStatementStart(matchLine, sameLineEndColumn + 1, lang);
        }
        var sameLineAdvanceComparisonColumn = sameLineRestartComparisonColumn;
        if (CanStepIntoSameLineTypeBody(lang, kind)
            && nextSameLineOffset > sameLineAdvanceComparisonColumn
            && nextSameLineOffset < matchLine.Length
            && matchLine[nextSameLineOffset] != '}')
        {
            return PatternScanResult.RestartAt(nextSameLineOffset);
        }
        if (lang == "csharp"
            && kind == "property"
            && pattern.BodyStyle == BodyStyle.Brace
            && nextSameLineOffset > sameLineAdvanceComparisonColumn
            && nextSameLineOffset < matchLine.Length)
        {
            // A same-line brace-body property that is followed by another sibling
            // declaration (`P { get; set; } public void M() { }`) must hand control
            // back to the whole pattern list at the next statement start, otherwise
            // earlier rows like the C# method regex never get a chance to see the
            // trailing sibling and mixed-kind lines lose one side.
            // Closes #473 follow-up.
            // 後続 sibling 宣言を伴う same-line brace-body property
            // (`P { get; set; } public void M() { }`) は、次の文開始位置から
            // pattern 全体へ制御を戻す必要がある。そうしないと、C# method regex
            // のような earlier row が後続 sibling を見られず、mixed-kind の
            // 同一行で片側が欠落する。Closes #473 follow-up.
            return PatternScanResult.RestartAt(nextSameLineOffset);
        }

        return PatternScanResult.ContinueAt(nextSameLineOffset);
    }
}
