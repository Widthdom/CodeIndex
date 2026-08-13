namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static PatternScanResult EvaluateCandidateSyntaxGates(
        MatchedPatternCandidateContext candidate,
        out PatternCandidateColumns columns)
    {
        columns = default;
        var lineContext = candidate.LineContext;
        var extraction = lineContext.Extraction;
        var lang = extraction.Lang;
        var pattern = candidate.Pattern;
        var patternMatchLine = candidate.PatternMatchLine;
        var matchLine = lineContext.PreparedLine.MatchLine;
        var lineOffset = candidate.LineOffset;
        var match = candidate.CapturedMatch.Match;
        var absoluteStartColumn = candidate.CapturedMatch.AbsoluteStartColumn;
        var lines = extraction.Lines;
        var line = lineContext.PreparedLine.SourceLine;
        var i = lineContext.LineIndex;
        var scanInputs = extraction.ScanInputs;
        var csharpMatchColumnToRaw = scanInputs.CSharpMatchColumnToRaw;
        var csharpMatchLines = scanInputs.CSharpMatchLines;
        var getCSharpSwitchExpressionLines = scanInputs.GetCSharpSwitchExpressionLines;
        var getCssQualifiedRuleAncestors = scanInputs.GetCssQualifiedRuleAncestors;
        var nextSameLineOffsetAfterRejectedCSharpProperty = -1;
        if (ShouldSkipCSharpSwitchExpressionPropertyCandidate(lang, pattern, patternMatchLine, getCSharpSwitchExpressionLines, i)
            || TrySkipCSharpBracePropertyCandidate(
                lang,
                pattern,
                patternMatchLine,
                absoluteStartColumn,
                match.Value.Contains("=>", StringComparison.Ordinal),
                out nextSameLineOffsetAfterRejectedCSharpProperty))
        {
            // False-positive C# property matches can happen at the start of a
            // same-line type header (`public class C { ... }`) because the
            // property regex allows omitted visibility/modifier runs and can
            // initially treat the header as `returnType + name + {`. Do not break
            // the whole same-line scan on that rejection — advance to the next
            // brace-delimited statement so a real nested property later on the
            // same physical line still gets a chance to match. Closes #470.
            // C# の property 正規表現は visibility / modifier 省略を許すため、
            // 同一行の型ヘッダ先頭 (`public class C { ... }`) を一旦
            // `returnType + name + {` と誤認することがある。この偽候補を弾いた
            // ときに同一行スキャン全体を break せず、次の brace 区切り宣言へ進めて
            // 後続の本物 property にもマッチ機会を残す。Closes #470.
            lineOffset = nextSameLineOffsetAfterRejectedCSharpProperty >= 0
                ? nextSameLineOffsetAfterRejectedCSharpProperty
                : FindNextSameLineBraceStatementStart(
                    matchLine,
                    absoluteStartColumn + Math.Max(1, match.Length),
                    lang);
            return PatternScanResult.ContinueAt(lineOffset);
        }

        // Gate the C# plain-field pattern (internally tagged as `property`,
        // BodyStyle.None, then normalized to the public `field` kind) to lines
        // that sit directly inside a type body. Without this gate, local
        // variable declarations inside method / property / accessor / lambda
        // bodies match the same shape and leak into `symbols`, `definition`,
        // `outline`, `inspect`, and `unused` as phantom field symbols.
        // Closes #298 follow-up (codex review blocker).
        // C# の通常フィールド用パターン（内部タグは `property`、BodyStyle.None、
        // 公開時に `field` へ正規化）は型本体（class / struct / interface /
        // record / enum の直下）でしか許可しない。このゲートを入れないと、
        // メソッド・プロパティ・アクセサ・ラムダの内部にあるローカル変数宣言が
        // 同じ形でマッチしてしまい、
        // `symbols` / `definition` / `outline` / `inspect` / `unused` に
        // 擬似シンボルが混入する。Closes #298 の codex レビュー blocker 対応。
        if (ShouldSkipCssNestedSelectorCandidate(lang, pattern, patternMatchLine, getCssQualifiedRuleAncestors, i))
            return PatternScanResult.NextPattern;

        // JS/TS HOC binding gate: the `styled.` / `styled(` / `styled\`` regex
        // branch matches three shapes — factory capture (`const F = styled.div;`),
        // plain call (`const F = styled(Component);`), and tagged template
        // (`const F = styled.div\`...\``). Only the tagged-template shape
        // actually declares a styled-component binding; the other two produce
        // a factory / a styled wrapper-of-component without a component body
        // on that line and must stay 0-symbol. This gate looks at the raw
        // (unmasked) line because StructuralLineMasker.MaskJsTsTemplateLiteralContents
        // replaces template-literal delimiters with space, so the masked
        // `patternMatchLine` cannot see the backtick. Closes #240 follow-up
        // (codex review #5 blocker).
        // JS/TS HOC 束縛ゲート: `styled.` / `styled(` / `styled\`` の regex
        // 分岐は 3 形状にマッチする — factory 捕捉（`const F = styled.div;`）、
        // 素の呼び出し（`const F = styled(Component);`）、タグ付きテンプレート
        // （`const F = styled.div\`...\``）。実際に styled-component 束縛を
        // 生むのはタグ付きテンプレート形のみで、前者 2 つはその行で component
        // 本体を生やさないため 0 シンボルに保つ必要がある。このゲートは raw 行
        // （マスク前）を参照する — `StructuralLineMasker.MaskJsTsTemplateLiteralContents`
        // がテンプレート区切りを空白にマスクするため、マスク後の
        // `patternMatchLine` ではバッククォートが見えないことへの対処。
        // Closes #240 follow-up（codex レビュー #5 の blocker 対応）。
        if (ShouldSkipJavaScriptTypeScriptStyledFactoryCandidate(lang, pattern, match, lineOffset, lines, i))
        {
            lineOffset = FindNextJavaScriptTypeScriptStatementStart(patternMatchLine, lineOffset + Math.Max(1, match.Length));
            return PatternScanResult.ContinueAt(lineOffset);
        }

        // For C#, collapsed-space column (from CollapseCSharpGenericTypeWhitespace)
        // has to be translated back to raw-space before it can be compared against
        // CSharpTypeBodyScope's per-line transitions, which were built from
        // structural (raw) columns. Only translate when the pattern match runs on
        // the per-line collapsed string (single-line case); multi-line merged
        // candidates use a different composed string whose column domain does not
        // line up with a single line's map, so we leave the column alone there to
        // preserve pre-existing behavior. Closes #400.
        // C# では CollapseCSharpGenericTypeWhitespace で空白を取り除いた列を、
        // structural 行の生列で構築された CSharpTypeBodyScope に渡す前に
        // raw 列へ戻す必要がある。複数行を結合した match では単一行の map が
        // 使えないため、単一行ケース（per-line collapsed line そのものにマッチした
        // 場合）だけ変換する。Closes #400.
        var csharpNormalizedStartColumn = lang == "csharp"
            ? SkipWhitespace(patternMatchLine, absoluteStartColumn)
            : absoluteStartColumn;
        var csharpGateRawStartColumn = csharpNormalizedStartColumn;
        if (lang == "csharp"
            && csharpMatchLines != null
            && ReferenceEquals(patternMatchLine, csharpMatchLines[i]))
        {
            csharpGateRawStartColumn = TranslateCSharpCollapsedColumnToRaw(
                csharpMatchColumnToRaw,
                i,
                csharpNormalizedStartColumn,
                line.Length);
        }

        columns = new PatternCandidateColumns(csharpGateRawStartColumn);
        return PatternScanResult.Accepted;
    }

    private static PatternScanResult EvaluateCandidateScopeAndTypeGates(
        MatchedPatternCandidateContext candidate,
        PatternCandidateColumns columns,
        out string? rawReturnType)
    {
        rawReturnType = null;
        var lineContext = candidate.LineContext;
        var extraction = lineContext.Extraction;
        var lang = extraction.Lang;
        var pattern = candidate.Pattern;
        var patternMatchLine = candidate.PatternMatchLine;
        var matchLine = lineContext.PreparedLine.MatchLine;
        var lineOffset = candidate.LineOffset;
        var match = candidate.CapturedMatch.Match;
        var absoluteStartColumn = candidate.CapturedMatch.AbsoluteStartColumn;
        var csharpPropertyCandidate = candidate.CSharpPropertyCandidate;
        var csharpGateRawStartColumn = columns.CSharpGateRawStartColumn;
        var lines = extraction.Lines;
        var line = lineContext.PreparedLine.SourceLine;
        var i = lineContext.LineIndex;
        var symbols = extraction.Symbols;
        var scanInputs = extraction.ScanInputs;
        var getCSharpLineStartStates = scanInputs.GetCSharpLineStartStates;
        var getPrivateScopeColumns = scanInputs.GetPrivateScopeColumns;
        var GetDartInsideClassBody = extraction.GetDartInsideClassBody;
        var GetCSharpInsideTypeBody = extraction.GetCSharpInsideTypeBody;
        var GetCSharpCallableParameterScope = extraction.GetCSharpCallableParameterScope;
        var GetCSharpDeclarationStartScope = extraction.GetCSharpDeclarationStartScope;
        if (lang == "dart"
            && ReferenceEquals(pattern.Regex, DartBareConstConstructorRegex)
            && !GetDartInsideClassBody().IsInsideClassBodyAt(i))
        {
            // Bare `const` constructors need class-body context; otherwise
            // `const Widget(key: k)` expressions become phantom symbols.
            // bare な `const` コンストラクタは class 本体内でのみ許可する。
            // そうしないと `const Widget(key: k)` の式を phantom symbol にしてしまう。
            lineOffset = FindNextSameLineBraceStatementStart(matchLine, absoluteStartColumn + Math.Max(1, match.Length), lang);
            return PatternScanResult.ContinueAt(lineOffset);
        }

        // C# candidates that only become visible after string-literal content is
        // blanked (for example, code inside an interpolation hole of an outer
        // string) must not be emitted as declarations. A real declaration starts in
        // root code, not in nested interpolation code. Gate on the raw-line start
        // column so exact definition / inspect lookups do not pick up call-site
        // fragments from interpolated log strings. Closes #790.
        // C# では、外側文字列本文を空白化した結果として見えるようになった候補
        // （例: 補間文字列ホール内のコード）を宣言として emit してはならない。
        // 本物の宣言は root code から始まり、入れ子の補間コードからは始まらない。
        // raw 行上の開始列でゲートし、補間ログ文字列内の呼び出し断片が
        // exact definition / inspect に混入しないようにする。Closes #790.
        var csharpLineStartStatesForGate = lang == "csharp"
            ? getCSharpLineStartStates?.Invoke()
            : null;
        if (csharpLineStartStatesForGate != null
            && !IsCSharpRootCodePosition(line, csharpLineStartStatesForGate[i], csharpGateRawStartColumn))
        {
            lineOffset = FindNextSameLineBraceStatementStart(
                matchLine,
                absoluteStartColumn + Math.Max(1, match.Length),
                lang);
            return PatternScanResult.ContinueAt(lineOffset);
        }

        if (lang == "csharp"
            && (pattern.Kind is "function" or "property")
            && match.Groups["name"].Success
            && IsCSharpStaticLambdaHeaderCandidate(
                patternMatchLine,
                lineOffset + match.Groups["name"].Index,
                FindCSharpEnclosingTypeName(symbols, i + 1)))
        {
            // Multi-line call arguments are composed into the same candidate
            // string as the following lambda. A declaration-shaped regex can then
            // reinterpret the state argument as a return type and `static` (or a
            // parameter) as a member name. Reject only names that occupy a
            // confirmed static-lambda header; real static members and local
            // functions remain eligible. Closes #4830; regression of #4453.
            // 複数行の呼び出し引数と後続 lambda は同じ候補文字列に結合されるため、
            // 宣言形 regex が state 引数を戻り値型、`static`（または parameter）を
            // member 名として再解釈し得る。確認済み static-lambda header 内の名前
            // だけを除外し、本物の static member / local function は維持する。
            // Closes #4830; regression of #4453.
            lineOffset = absoluteStartColumn + Math.Max(1, match.Length);
            return PatternScanResult.ContinueAt(lineOffset);
        }

        if (lang == "csharp"
            && pattern.Kind == "function"
            && pattern.BodyStyle == BodyStyle.Brace
            && !GetCSharpDeclarationStartScope().CanStartDeclarationAt(i, csharpGateRawStartColumn))
        {
            // Method and constructor patterns are intentionally broad enough to
            // recognize multi-line declarations. Do not let that merger begin from
            // an invocation argument or callable-parameter continuation, even when
            // a later declaration brace makes the combined text look method-shaped.
            // The scope is column-aware so a real same-line sibling remains eligible.
            // メソッドとコンストラクタのパターンは複数行宣言を認識するため意図的に
            // 広い。後続の宣言 brace によって結合テキストがメソッド形に見えても、
            // 呼び出し引数や callable parameter の継続位置から開始してはならない。
            // 列単位の scope により、同一行の後続にある本物の sibling は維持する。
            // Fixes #4831; prevents regressions covered by #496 and #4413.
            lineOffset = FindNextSameLineBraceStatementStart(
                matchLine,
                absoluteStartColumn + Math.Max(1, match.Length),
                lang);
            return PatternScanResult.ContinueAt(lineOffset);
        }

        if (lang == "csharp"
            && pattern.Kind == "function"
            && HasCSharpTokenBeforeIndex(matchLine, "when", absoluteStartColumn + match.Groups["name"].Index))
        {
            lineOffset = absoluteStartColumn + Math.Max(1, match.Length);
            return PatternScanResult.ContinueAt(lineOffset);
        }
        if (lang == "csharp"
            && pattern.Kind == "function"
            && csharpPropertyCandidate.ExpressionBodyEndLineIndex.HasValue
            && IsCSharpFunctionMatchInsideExpressionBody(
                patternMatchLine,
                absoluteStartColumn + match.Groups["name"].Index))
        {
            // The property/function header merger is shared by all C# member
            // patterns. Once it has identified an expression body, a function-shaped
            // call after `=>` is an expression, not a declaration. Preserve a real
            // same-line sibling after the terminating `;`.
            // C# の property/function header merger は全 member pattern で共有される。
            // 式本体を特定した後、`=>` より後の function 形呼び出しは宣言ではなく式である。
            // 終端 `;` より後にある本物の same-line sibling は維持する。
            lineOffset = absoluteStartColumn + Math.Max(1, match.Length);
            return PatternScanResult.ContinueAt(lineOffset);
        }
        if (lang == "csharp"
            && pattern.BodyStyle == BodyStyle.None
          && (pattern.Kind == "property" || IsCSharpFieldLikeFunctionPattern(pattern))
          && GetCSharpCallableParameterScope().IsInsideParameterListAt(i, csharpGateRawStartColumn))
        {
            lineOffset = FindNextSameLineBraceStatementStart(matchLine, absoluteStartColumn + Math.Max(1, match.Length), lang);
            return PatternScanResult.ContinueAt(lineOffset);
        }
        if (lang == "csharp"
            && pattern.BodyStyle == BodyStyle.None
          && (pattern.Kind == "property" || IsCSharpFieldLikeFunctionPattern(pattern))
          && !GetCSharpInsideTypeBody().IsInsideTypeBodyAt(i, csharpGateRawStartColumn))
        {
            // Move the cursor past this same-line candidate so a later
            // column on the same line (e.g. a real field that lives after
            // a same-line method body or similar non-type-body scope) can
            // still be evaluated against its own column-aware scope.
            // Without this advance, the outer `while` would exit the line
            // entirely on the first rejection and drop any following match.
            // 同一行に続く別候補（例: 同一行の method 本体など非型本体の
            // 後ろにある実フィールド）を取りこぼさないよう、次の候補探索
            // 位置へ進める。この進行が無いと最初の拒否で while ループが
            // 行を抜けてしまい、後続候補が失われる。Closes #400.
            lineOffset = FindNextSameLineBraceStatementStart(matchLine, absoluteStartColumn + Math.Max(1, match.Length), lang);
            return PatternScanResult.ContinueAt(lineOffset);
        }
        if (lang == "csharp"
            && pattern.BodyStyle == BodyStyle.None
            && (pattern.Kind == "property" || IsCSharpFieldLikeFunctionPattern(pattern))
            && IsInsidePreviouslyEmittedCSharpMemberBody(lines, symbols, i + 1, csharpGateRawStartColumn))
        {
            // Brace-based type-body scope tracking correctly rejects locals inside
            // block bodies, but multi-line expression-bodied members have no brace
            // transition for their continuation lines. Without an additional guard,
            // those later lines can still match the plain-field regex and emit
            // phantom `property` rows like `Red` from `value is\n Red\n or Red;`.
            // Only reject lines after the member's declaration line so same-line
            // siblings such as `int M() => 0; int X;` keep working through the
            // existing column-aware scope gate. Closes #779.
            // brace ベースの型本体スコープ追跡は block body 内の local を弾けるが、
            // 複数行の式本体メンバーには continuation 行用の brace 遷移が無い。
            // そのため追加ガードが無いと `value is\n Red\n or Red;` の後続行が
            // plain-field regex にマッチして `property Red` の phantom を出してしまう。
            // `int M() => 0; int X;` のような same-line sibling は既存の列単位
            // ゲートで扱えるよう、宣言行そのものではなく後続行だけを拒否する。
            // Closes #779.
            lineOffset = FindNextSameLineBraceStatementStart(matchLine, absoluteStartColumn + Math.Max(1, match.Length), lang);
            return PatternScanResult.ContinueAt(lineOffset);
        }
        if (lang == "rust"
            && pattern.Kind == "property"
            && pattern.BodyStyle == BodyStyle.None
            && pattern.ReturnTypeGroup != null
            && !IsRustDirectTraitBodyMember(symbols, i + 1))
        {
            return PatternScanResult.NextPattern;
        }
        rawReturnType = NormalizeCSharpImplicitPartialMethodReturnType(
            lang,
            pattern,
            match,
            TryGetGroup(match, pattern.ReturnTypeGroup));
        if (lang == "csharp"
            && pattern.ReturnTypeGroup != null
            && HasInvalidCSharpReturnTypeSuffix(rawReturnType))
        {
            lineOffset = FindNextSameLineBraceStatementStart(matchLine, absoluteStartColumn + Math.Max(1, match.Length), lang);
            return PatternScanResult.ContinueAt(lineOffset);
        }
        if (lang == "csharp"
            && pattern.Kind == "function"
            && HasCSharpTokenBeforeIndex(matchLine, "when", absoluteStartColumn + match.Groups["name"].Index))
        {
            lineOffset = absoluteStartColumn + Math.Max(1, match.Length);
            return PatternScanResult.ContinueAt(lineOffset);
        }
        if (lang == "csharp"
            && pattern.Kind == "property"
            && IsStandaloneCSharpAccessorCandidate(patternMatchLine))
        {
            lineOffset = FindNextSameLineBraceStatementStart(matchLine, absoluteStartColumn + Math.Max(1, match.Length), lang);
            return PatternScanResult.ContinueAt(lineOffset);
        }
        var jsTsPrivateScopeColumnsForClassGate = lang is "javascript" or "typescript" && pattern.Kind == "class"
            ? getPrivateScopeColumns?.Invoke()
            : null;
        if (jsTsPrivateScopeColumnsForClassGate != null
            && pattern.Kind == "class"
            && IsJavaScriptTypeScriptMatchInPrivateScope(jsTsPrivateScopeColumnsForClassGate, i, absoluteStartColumn, matchLine, includeBlockScope: true))
        {
            if (lang is "javascript" or "typescript")
            {
                var skippedEndColumn = pattern.BodyStyle == BodyStyle.Brace
                    ? FindJavaScriptTypeScriptSameLineBraceEndColumn(line, absoluteStartColumn, lang)
                    : -1;
                lineOffset = skippedEndColumn >= absoluteStartColumn
                    ? FindNextJavaScriptTypeScriptStatementStart(patternMatchLine, skippedEndColumn + 1)
                    : FindNextJavaScriptTypeScriptStatementStart(patternMatchLine, absoluteStartColumn + Math.Max(1, match.Length));
                return PatternScanResult.ContinueAt(lineOffset);
            }

            return PatternScanResult.NextPattern;
        }

        if (jsTsPrivateScopeColumnsForClassGate != null
            && pattern.Kind == "class"
            && TryGetGroup(match, pattern.VisibilityGroup) != "export"
            && IsJavaScriptTypeScriptMatchInNamespaceScope(jsTsPrivateScopeColumnsForClassGate, i, absoluteStartColumn, matchLine))
        {
            if (lang is "javascript" or "typescript")
            {
                var skippedEndColumn = pattern.BodyStyle == BodyStyle.Brace
                    ? FindJavaScriptTypeScriptSameLineBraceEndColumn(line, absoluteStartColumn, lang)
                    : -1;
                lineOffset = skippedEndColumn >= absoluteStartColumn
                    ? FindNextJavaScriptTypeScriptStatementStart(patternMatchLine, skippedEndColumn + 1)
                    : FindNextJavaScriptTypeScriptStatementStart(patternMatchLine, absoluteStartColumn + Math.Max(1, match.Length));
                return PatternScanResult.ContinueAt(lineOffset);
            }

            return PatternScanResult.NextPattern;
        }

        return PatternScanResult.Accepted;
    }
}
