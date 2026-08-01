using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static List<SymbolRecord> ExtractCore(
        long fileId,
        string? lang,
        string content,
        bool contentIsNormalized,
        bool? hasOversizeLine,
        int? conflictMarkerLine,
        string? filePath = null,
        string? projectRoot = null,
        bool patternConfigsAlreadyLoaded = false,
        CancellationToken cancellationToken = default,
        int? maxSymbols = null)
    {
        var originalLang = lang;
        if (TryPrepareSymbolExtraction(
            fileId,
            originalLang,
            content,
            contentIsNormalized,
            hasOversizeLine,
            conflictMarkerLine,
            filePath,
            projectRoot,
            patternConfigsAlreadyLoaded,
            cancellationToken,
            out lang,
            out content,
            out var preparedSymbols))
        {
            return preparedSymbols!;
        }

        if (TryExtractSpecializedSymbols(
            fileId,
            lang,
            content,
            filePath,
            projectRoot,
            cancellationToken,
            out var specializedSymbols))
        {
            return specializedSymbols;
        }

        // Normalize CRLF / CR to LF first so direct callers that bypass FileIndexer
        // still present a `\n`-only content stream, and then strip line-leading
        // UTF-8 BOM (U+FEFF) defensively so `^\s*`-anchored patterns match on
        // line 1 and on any mid-file line that begins with a BOM (e.g. from file
        // concatenation or tool insertion). StripLineLeadingBom assumes `\n` is
        // the sole line separator, so the CRLF pass must come first. Non-line-
        // leading U+FEFF is preserved so content with intentional ZWNBSP inside
        // a string literal stays verbatim. Closes #183.
        // まず CRLF / CR を LF に正規化する。StripLineLeadingBom は `\n` を唯一の
        // 行区切りとして行頭判定するので、FileIndexer を経由しない direct call
        // でも CRLF 正規化を済ませてから呼ばないと mid-file の行頭 BOM を剥がし
        // 損なう。続いて行頭 U+FEFF のみ剥がし、1 行目と mid-file の行頭 BOM 両方
        // で `^\s*` 固定パターンを成立させる。行頭以外の U+FEFF (文字列リテラル中
        // の意図的な ZWNBSP 等) はそのまま保持する。Closes #183.
        List<SymbolPattern>? patterns = null;
        var usesLineBasedExtractor = lang is "commonlisp" or "racket" or "solidity" or "html" or "assembly"
            || (lang is not null && PatternCache.TryGetValue(lang, out patterns));
        if (!usesLineBasedExtractor)
            return [];

        var lines = SplitContentLines(content);
        cancellationToken.ThrowIfCancellationRequested();
        if (lang is "commonlisp" or "racket")
            return ExtractLispSymbols(fileId, lang, lines);

        if (lang == "solidity")
            return ExtractSoliditySymbols(fileId, lines);

        // HTML has no brace/indent-scoped bodies, so the generic pattern loop's
        // "first match per line" semantics drop every additional symbol on the
        // same line. HTML also needs cross-line masking of `<!-- ... -->` and
        // raw-text children of `<script>` / `<style>` before patterns run, or
        // phantom imports/classes/properties leak out of commented-out tags
        // and inline template string literals. Closes #215 codex review blocker.
        // HTML は brace/indent スコープの本体を持たないため、汎用パターンループの
        // 「1 行の先勝ち」意味論を通すと同一行の追加シンボルを取りこぼす。加えて
        // `<!-- ... -->` と `<script>` / `<style>` の raw-text 子要素を跨ぎ行で
        // マスクしておかないと、コメントアウトされたタグやインラインテンプレート
        // 文字列から phantom な import / class / property が漏れる。#215 の codex
        // レビュー blocker 対応としてここで専用抽出に分岐する。
        if (lang == "html")
            return ExtractHtmlSymbols(fileId, content, lines);

        if (lang == "assembly")
            return ExtractAssemblySymbols(fileId, lines);

        if (patterns == null || lang == null)
            return [];

        var scanInputs = new PatternScanInputs(lang, filePath, lines);
        var pythonModulePrefix = scanInputs.PythonModulePrefix;
        var structuralLines = scanInputs.StructuralLines;
        var scientificBodyScannerLines = scanInputs.ScientificBodyScannerLines;
        var matlabExplicitOuterClosureByLine = scanInputs.MatlabExplicitOuterClosureByLine;
        Func<string[]> GetJavaScriptTypeScriptSanitizedLines = scanInputs.GetJavaScriptTypeScriptSanitizedLines;
        var cssScannerLines = scanInputs.CssScannerLines;
        var sassStylusScannerLines = scanInputs.SassStylusScannerLines;
        var shellScannerLines = scanInputs.ShellScannerLines;
        var prologClauseContinuationLines = scanInputs.PrologClauseContinuationLines;
        var prologMultilineHeads = scanInputs.PrologMultilineHeads;
        var powershellEnumBodyLines = scanInputs.PowershellEnumBodyLines;
        var csharpMatchColumnToRaw = scanInputs.CSharpMatchColumnToRaw;
        var csharpMatchLines = scanInputs.CSharpMatchLines;
        var getCSharpLineStartStates = scanInputs.GetCSharpLineStartStates;
        Func<DartClassBodyScope> GetDartInsideClassBody = scanInputs.GetDartInsideClassBody;
        var getPrivateScopeColumns = scanInputs.GetPrivateScopeColumns;
        Func<CSharpTypeBodyScope> GetCSharpInsideTypeBody = scanInputs.GetCSharpInsideTypeBody;
        Func<CSharpCallableParameterScope> GetCSharpCallableParameterScope = scanInputs.GetCSharpCallableParameterScope;
        Func<CSharpDeclarationStartScope> GetCSharpDeclarationStartScope = scanInputs.GetCSharpDeclarationStartScope;
        var getCSharpSwitchExpressionLines = scanInputs.GetCSharpSwitchExpressionLines;
        var getCssQualifiedRuleAncestors = scanInputs.GetCssQualifiedRuleAncestors;
        var initialSymbolCapacity = EstimateSymbolListInitialCapacity(lines.Length);
        var symbols = new SymbolExtractionList(initialSymbolCapacity, maxSymbols);
        var extractionState = symbols.ExtractionState;
        var scanState = new PatternScanState();
        List<PendingRecordPrimaryComponents>? pendingRecordPrimaryComponents = null;
        RecordPrimaryComponentParentIndex? recordPrimaryComponentParentIndex = null;
        var cssSeenSymbols = lang == "css"
            ? new HashSet<SymbolLineIdentity>()
            : null;
        var dockerfileStageNames = lang == "dockerfile"
            ? new HashSet<string>(StringComparer.Ordinal)
            : null;
        for (int i = 0; i < lines.Length; i++)
        {
            if (symbols.IsAtCapacity)
                break;

            if ((i & 0x3f) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            if (!TryPreparePatternLine(
                    fileId,
                    lang,
                    filePath,
                    projectRoot,
                    lines,
                    scanInputs,
                    scanState,
                    symbols,
                    extractionState,
                    dockerfileStageNames,
                    i,
                    out var preparedLine))
            {
                continue;
            }

            var line = preparedLine.SourceLine;
            var matchLine = preparedLine.MatchLine;
            var cssScannerLine = preparedLine.CssScannerLine;
            var fortranContinuationCandidate = preparedLine.FortranContinuationCandidate;
            var patternStartOffset = preparedLine.PatternStartOffset;
            var prologContinuationResumeOffset = preparedLine.PrologContinuationResumeOffset;
            while (patternStartOffset >= 0 && patternStartOffset < matchLine.Length)
            {
                var stopAfterFirstPatternMatch = false;
                var restartPatternScanOffset = -1;
                CSharpPropertyMatchCandidate? csharpPropertyCandidateForLine = null;
                foreach (var pattern in patterns)
                {
                    if (prologClauseContinuationLines?[i] == true
                        && prologContinuationResumeOffset < 0
                        && pattern.Kind == "function")
                        continue;
                    if (lang == "csharp" && ReferenceEquals(pattern.Regex, CSharpEnumMemberRegex))
                        continue;
                    if (lang == "powershell"
                        && pattern.Kind == "enum"
                        && pattern.BodyStyle == BodyStyle.None
                        && !powershellEnumBodyLines![i])
                    {
                        continue;
                    }
                    // Merge multi-line field headers for C# regardless of kind. Kind "property" (plain
                    // fields) and kind "function" (const / static readonly fields) both need the
                    // merge. Non-field function patterns (methods, constructors, operators, indexers)
                    // are unaffected because CSharpPropertyHeaderPrefixRegex requires the line to end
                    // before `(` or `{`, so lines like `public int Foo()` never satisfy the header
                    // prefix and the merger returns the original line. Closes #355.
                    // C# の複数行フィールドヘッダ結合は kind に依らず適用する。kind "property"（通常
                    // フィールド）と kind "function"（`const` / `static readonly` フィールド）の両方で
                    // 結合が必要。method / constructor / operator / indexer のような非フィールド
                    // function パターンは `CSharpPropertyHeaderPrefixRegex` が `(` や `{` を含む行を
                    // 受け付けないため影響を受けず、merger は元の行をそのまま返す。Closes #355.
                    var csharpPropertyCandidate = lang == "csharp" && pattern.Kind is "property" or "function"
                        ? csharpPropertyCandidateForLine ??= BuildCSharpPropertyMatchLine(lines, csharpMatchLines!, i)
                        : new CSharpPropertyMatchCandidate(matchLine, i, i);
                    var patternMatchLine = csharpPropertyCandidate.MatchLine;
                    if (fortranContinuationCandidate != null)
                        patternMatchLine = fortranContinuationCandidate.Value.MatchLine;
                    var lineOffset = patternStartOffset;
                    string? csharpWrappedModifierPrefix = null;
                    while (lineOffset >= 0 && lineOffset < patternMatchLine.Length)
                    {
                        var javaLeadingAnnotationOffset = 0;
                        var match = lang is "java" or "kotlin"
                            ? (TryMatchJavaDeclarationSegment(pattern.Regex, patternMatchLine[lineOffset..], lang == "kotlin", out var javaMatch, out javaLeadingAnnotationOffset)
                                ? javaMatch
                                : pattern.Regex.Match(patternMatchLine[lineOffset..]))
                            : pattern.Regex.Match(patternMatchLine[lineOffset..]);
                        if (!match.Success
                            && lang == "csharp"
                            && pattern.Kind == "function"
                            && lineOffset == 0
                            && csharpMatchLines != null
                            && csharpWrappedModifierPrefix == null)
                        {
                            // Wrapped leading modifier recovery: when a C# function-kind pattern
                            // fails at column 0 of the identifier line, try prepending the
                            // modifier prefix accumulated from preceding modifier-only lines
                            // (`static\nFoo() { ... }`, `public\nBar() { ... }`, etc.) and retry.
                            // The method regex already tolerates an omitted modifier run, so it
                            // matches on the identifier line alone — this branch only fires for
                            // constructor / static-constructor shapes that require the modifier
                            // on the same line as the name. Closes #348.
                            // ラップされた先頭モディファイアの救済: C# の function 系パターンが
                            // 識別子行の先頭マッチに失敗した場合、直前のモディファイアのみ行
                            // （`static\nFoo() { ... }` や `public\nBar() { ... }` 等）から
                            // 再構築した prefix を付け直して再試行する。メソッド regex は
                            // 先頭モディファイアが無くても識別子行単体でマッチするため、この
                            // 分岐は修飾子が識別子と同行に必要な constructor / static ctor
                            // シェイプでのみ発火する。Closes #348.
                            var wrappedInfo = TryFindCSharpWrappedHeaderModifier(csharpMatchLines!, i);
                            if (wrappedInfo != null)
                            {
                                foreach (var candidatePrefix in EnumerateCSharpWrappedModifierCandidates(wrappedInfo.Value.Prefix))
                                {
                                    var wrappedMatchLine = candidatePrefix + " " + patternMatchLine.TrimStart();
                                    var wrappedMatch = pattern.Regex.Match(wrappedMatchLine);
                                    if (wrappedMatch.Success)
                                    {
                                        match = wrappedMatch;
                                        patternMatchLine = wrappedMatchLine;
                                        // Preserve the full prefix in the stored signature so
                                        // declarations like `public\nstatic\nP1()` retain
                                        // `public static P1()`, even when the matching regex
                                        // variant only accepted `static P1()`. Closes #348.
                                        // シグネチャには完全な prefix を残し、`public\nstatic\nP1()`
                                        // のような宣言を `public static P1()` として保存する。
                                        // マッチした regex 変種が `static P1()` 形だけを受け付けた
                                        // 場合でも、保存シグネチャは完全な prefix を保持する。Closes #348.
                                        csharpWrappedModifierPrefix = wrappedInfo.Value.Prefix;
                                        break;
                                    }
                                }
                            }
                        }

                        if (!match.Success)
                        {
                            if (lang == "csharp"
                                && pattern.Kind == "property"
                                && pattern.BodyStyle == BodyStyle.Brace
                                && ShouldDeferCSharpBracePropertySameLineAdvance(matchLine, lineOffset))
                            {
                                break;
                            }

                            if (lang == "csharp"
                                && pattern.Kind == "function"
                                && ShouldDeferCSharpFunctionSameLineAdvance(matchLine, lineOffset))
                            {
                                break;
                            }

                            if (lang == "csharp"
                                && pattern.Kind is "event" or "delegate"
                                && pattern.BodyStyle == BodyStyle.None
                                && ShouldDeferCSharpEventOrDelegateSameLineAdvance(matchLine, lineOffset, pattern.Kind))
                            {
                                break;
                            }

                            if (lang is "javascript" or "typescript" or "css" or "java"
                                || (lang == "csharp"
                                    && pattern.Kind == "enum"
                                    && pattern.BodyStyle == BodyStyle.Brace
                                    && patternStartOffset > 0)
                                || (lang == "csharp"
                                    && pattern.Kind == "property"
                                    && pattern.BodyStyle == BodyStyle.None
                                    && !TryMatchAnyRecoverableCSharpPattern(
                                        matchLine[lineOffset..],
                                        insideEnumBody: false,
                                        attributeParenDepth: 0)))
                            {
                                lineOffset = FindNextSameLineBraceStatementStart(matchLine, lineOffset + 1, lang);
                                continue;
                            }

                            break;
                        }

                        var absoluteStartColumn = lineOffset + match.Index;
                        if (lang is "java" or "kotlin" && javaLeadingAnnotationOffset > 0)
                            absoluteStartColumn = lineOffset + javaLeadingAnnotationOffset;
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
                            continue;
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
                            break;

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
                            continue;
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

                        if (lang == "dart"
                            && ReferenceEquals(pattern.Regex, DartBareConstConstructorRegex)
                            && !GetDartInsideClassBody().IsInsideClassBodyAt(i))
                        {
                            // Bare `const` constructors need class-body context; otherwise
                            // `const Widget(key: k)` expressions become phantom symbols.
                            // bare な `const` コンストラクタは class 本体内でのみ許可する。
                            // そうしないと `const Widget(key: k)` の式を phantom symbol にしてしまう。
                            lineOffset = FindNextSameLineBraceStatementStart(matchLine, absoluteStartColumn + Math.Max(1, match.Length), lang);
                            continue;
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
                            continue;
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
                            continue;
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
                            continue;
                        }

                        if (lang == "csharp"
                            && pattern.Kind == "function"
                            && HasCSharpTokenBeforeIndex(matchLine, "when", absoluteStartColumn + match.Groups["name"].Index))
                        {
                            lineOffset = absoluteStartColumn + Math.Max(1, match.Length);
                            continue;
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
                            continue;
                        }
                        if (lang == "csharp"
                            && pattern.BodyStyle == BodyStyle.None
                          && (pattern.Kind == "property" || IsCSharpFieldLikeFunctionPattern(pattern))
                          && GetCSharpCallableParameterScope().IsInsideParameterListAt(i, csharpGateRawStartColumn))
                        {
                            lineOffset = FindNextSameLineBraceStatementStart(matchLine, absoluteStartColumn + Math.Max(1, match.Length), lang);
                            continue;
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
                            continue;
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
                            continue;
                        }
                        if (lang == "rust"
                            && pattern.Kind == "property"
                            && pattern.BodyStyle == BodyStyle.None
                            && pattern.ReturnTypeGroup != null
                            && !IsRustDirectTraitBodyMember(symbols, i + 1))
                        {
                            break;
                        }
                        var rawReturnType = NormalizeCSharpImplicitPartialMethodReturnType(
                            lang,
                            pattern,
                            match,
                            TryGetGroup(match, pattern.ReturnTypeGroup));
                        if (lang == "csharp"
                            && pattern.ReturnTypeGroup != null
                            && HasInvalidCSharpReturnTypeSuffix(rawReturnType))
                        {
                            lineOffset = FindNextSameLineBraceStatementStart(matchLine, absoluteStartColumn + Math.Max(1, match.Length), lang);
                            continue;
                        }
                        if (lang == "csharp"
                            && pattern.Kind == "function"
                            && HasCSharpTokenBeforeIndex(matchLine, "when", absoluteStartColumn + match.Groups["name"].Index))
                        {
                            lineOffset = absoluteStartColumn + Math.Max(1, match.Length);
                            continue;
                        }
                        if (lang == "csharp"
                            && pattern.Kind == "property"
                            && IsStandaloneCSharpAccessorCandidate(patternMatchLine))
                        {
                            lineOffset = FindNextSameLineBraceStatementStart(matchLine, absoluteStartColumn + Math.Max(1, match.Length), lang);
                            continue;
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
                                continue;
                            }

                            break;
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
                                continue;
                            }

                            break;
                        }

                        var name = match.Groups["name"].Success
                            ? match.Groups["name"].ValueSpan.Trim().ToString()
                            : match.ValueSpan.Trim().ToString();
                        name = NormalizeExtractedSymbolName(lang, name, match, matchLine);
                        if (pattern.Kind == "import" && lang is "javascript" or "typescript")
                            name = ResolveJavaScriptTypeScriptModuleSpecifier(lang, filePath, projectRoot, name);
                        var rubyAttrNames = lang == "ruby"
                            && pattern.Kind == "property"
                            ? TryExpandRubyAttrDeclaratorList(patternMatchLine, absoluteStartColumn, match, name)
                            : null;

                        var rangeLines = lang == "css" && cssScannerLines != null
                            ? cssScannerLines
                            : lang == "shell" && shellScannerLines != null
                                ? shellScannerLines
                            : structuralLines;
                        var scalaBracelessClassEndLine = lang == "scala" && pattern.Kind == "class"
                            ? TryFindScalaBracelessClassEndLine(lines, i, absoluteStartColumn)
                            : null;
                        var (endLine, bodyStartLine, bodyEndLine) = lang is "kotlin" or "scala"
                            && pattern.Kind == "function"
                            && TryFindKotlinScalaExpressionBodyEndLine(line, absoluteStartColumn)
                                ? (i + 1, null, null)
                                : scalaBracelessClassEndLine.HasValue
                                        ? (scalaBracelessClassEndLine.Value + 1, null, null)
                                        : lang == "csharp" && pattern.BodyStyle == BodyStyle.Brace && csharpMatchLines != null
                                            ? FindCSharpPatternBraceRange(
                                                lines,
                                                csharpMatchLines,
                                                getCSharpLineStartStates,
                                                i,
                                                absoluteStartColumn,
                                                csharpGateRawStartColumn)
                                            : ResolveRange(
                                                rangeLines,
                                                i,
                                                pattern.BodyStyle,
                                                lang,
                                                absoluteStartColumn,
                                                scientificBodyScannerLines,
                                                matlabExplicitOuterClosureByLine);
                        if (fortranContinuationCandidate != null)
                            endLine = Math.Max(endLine, fortranContinuationCandidate.Value.LastConsumedLineIndex + 1);
                        var startLine = i + 1;
                        if (lang == "csharp"
                            && pattern.Kind == "property"
                            && pattern.BodyStyle == BodyStyle.None
                            && csharpPropertyCandidate.ExpressionBodyEndLineIndex.HasValue)
                        {
                            endLine = Math.Max(endLine, csharpPropertyCandidate.ExpressionBodyEndLineIndex.Value + 1);
                        }

                        // Python @property decorator: reclassify the def as property
                        // Python @property デコレータ: def を property に再分類
                        var kind = pattern.Kind;
                        string? pythonSubKind = null;
                        if (kind == "function" && lang == "python" && HasPythonPropertyDecorator(lines, i))
                        {
                            kind = "property";
                            pythonSubKind = GetPythonPropertyAccessorSubKind(lines, i);
                        }
                        else if (kind == "function" && lang == "python" && IsPythonClassHook(name))
                        {
                            kind = "class_hook";
                            pythonSubKind = "dunder";
                            (endLine, bodyStartLine, bodyEndLine) = FindPythonIndentedBodyRange(lines, i);
                        }
                        else if (kind == "function" && lang is "javascript" or "typescript")
                        {
                            kind = ResolveJavaScriptTypeScriptFunctionKind(
                                TryGetGroup(match, "async") != null,
                                TryGetGroup(match, "generator") != null);
                        }

                        if (lang == "css")
                            name = ResolveCssSymbolName(matchLine[absoluteStartColumn..], name, lines, i, endLine);

                        if (lang == "css" && string.IsNullOrWhiteSpace(name))
                        {
                            var skippedEndColumn = pattern.BodyStyle == BodyStyle.Brace
                                && bodyEndLine == startLine
                                ? FindSameLineBraceEndColumn(line, absoluteStartColumn, lang, kind)
                                : -1;
                            if (skippedEndColumn >= absoluteStartColumn)
                            {
                                lineOffset = FindNextSameLineBraceStatementStart(matchLine, skippedEndColumn + 1, lang);
                                continue;
                            }

                            stopAfterFirstPatternMatch = true;
                            break;
                        }

                        var signatureResult = BuildPatternSignature(
                            lang,
                            pattern,
                            lines,
                            i,
                            line,
                            patternMatchLine,
                            absoluteStartColumn,
                            match,
                            csharpPropertyCandidate,
                            csharpWrappedModifierPrefix,
                            csharpMatchColumnToRaw,
                            csharpMatchLines,
                            csharpGateRawStartColumn,
                            startLine,
                            bodyStartLine,
                            bodyEndLine,
                            kind);
                        var signature = signatureResult.Signature;
                        kind = signatureResult.Kind;
                        var csharpSingleLineCollapsedMatch = signatureResult.Bounds.CSharpSingleLineCollapsedMatch;
                        var csharpSignatureRawStartColumn = signatureResult.Bounds.CSharpSignatureRawStartColumn;
                        var sameLineEndColumn = signatureResult.Bounds.SameLineEndColumn;
                        var sameLineEndUsesRawColumns = signatureResult.Bounds.SameLineEndUsesRawColumns;

                        kind = EmitPatternSymbols(
                            new PatternSymbolEmissionContext(
                                fileId,
                                lang,
                                pattern,
                                lines,
                                i,
                                lineOffset,
                                absoluteStartColumn,
                                line,
                                patternMatchLine,
                                match,
                                name,
                                kind,
                                signature,
                                rawReturnType,
                                pythonSubKind,
                                pythonModulePrefix,
                                rubyAttrNames,
                                new PatternSymbolRange(endLine, bodyStartLine, bodyEndLine),
                                signatureResult.Bounds,
                                symbols,
                                extractionState,
                                cssSeenSymbols,
                                dockerfileStageNames));

                        if (lang == "css"
                            && pattern.Kind == "namespace"
                            && pattern.BodyStyle == BodyStyle.Brace
                            && cssScannerLines != null)
                        {
                            TryAddCssMediaFeatureSymbols(
                                fileId,
                                line,
                                cssScannerLines[i],
                                i,
                                symbols,
                                cssSeenSymbols);
                        }

                        if (lang == "css"
                            && pattern.Kind == "class"
                            && pattern.BodyStyle == BodyStyle.Brace
                            && cssScannerLines != null)
                        {
                            var openingBraceIndex = cssScannerLines[i].IndexOf('{', absoluteStartColumn);
                            if (openingBraceIndex > absoluteStartColumn)
                            {
                                TryAddCssSelectorListSegments(
                                    fileId,
                                    line[absoluteStartColumn..openingBraceIndex],
                                    cssScannerLines[i][absoluteStartColumn..openingBraceIndex],
                                    cssScannerLines,
                                    i,
                                    openingBraceIndex,
                                    patterns,
                                    symbols,
                                    cssSeenSymbols);
                            }
                        }

                        if (lang == "csharp"
                            && pattern.Kind == "property"
                            && csharpPropertyCandidate.ExpressionBodyEndLineIndex.HasValue)
                        {
                            var expressionEndLineIndex = csharpPropertyCandidate.ExpressionBodyEndLineIndex.Value;
                            if (expressionEndLineIndex > i
                                && csharpPropertyCandidate.ExpressionBodyEndLineExclusiveEndColumn.HasValue)
                            {
                                // Suppress complete continuation lines, but resume after the
                                // terminating semicolon so a valid same-line sibling remains visible.
                                // 完全な continuation 行だけを抑止し、終端 semicolon の後から
                                // 再開して有効な same-line sibling を維持する。
                                scanState.CSharpSuppressedContinuationUntil = Math.Max(
                                    scanState.CSharpSuppressedContinuationUntil,
                                    expressionEndLineIndex - 1);
                                scanState.CSharpSuppressedContinuationResumeLine = expressionEndLineIndex;
                                scanState.CSharpSuppressedContinuationResumeRawColumn =
                                    csharpPropertyCandidate.ExpressionBodyEndLineExclusiveEndColumn.Value;
                            }
                            else
                            {
                                scanState.CSharpSuppressedContinuationUntil = Math.Max(
                                    scanState.CSharpSuppressedContinuationUntil,
                                    expressionEndLineIndex);
                            }
                        }

                        if (lang == "csharp"
                            && pattern.Kind is "event" or "delegate"
                            && pattern.BodyStyle == BodyStyle.None
                            && (TryGetCSharpSameLineEventSiblingOffset(patternMatchLine, absoluteStartColumn, out var nextSemicolonSiblingOffset)
                                || TryGetCSharpSameLineSemicolonSiblingOffset(patternMatchLine, absoluteStartColumn, out nextSemicolonSiblingOffset)))
                        {
                            restartPatternScanOffset = nextSemicolonSiblingOffset;
                            break;
                        }

                        if (lang == "java"
                            && pattern.BodyStyle == BodyStyle.Brace
                            && bodyStartLine == null
                            && TryGetJavaSameLineSemicolonSiblingOffset(patternMatchLine, absoluteStartColumn, out var nextJavaSiblingOffset))
                        {
                            // Body-less Java members inside `interface` / `@interface` / abstract-style
                            // declarations can share one physical line (`String[] value(); int age();`).
                            // Restart at the next sibling after the top-level `;` instead of stopping at
                            // the first match, or later members on the same line disappear. Closes #788.
                            // Java の body-less member（`interface` / `@interface` / abstract 形）は
                            // `String[] value(); int age();` のように 1 行へ並ぶ。top-level `;`
                            // の直後から sibling へ再開しないと、同一行の後続 member が最初の 1 個で
                            // 途切れて消える。Closes #788.
                            restartPatternScanOffset = nextJavaSiblingOffset;
                            break;
                        }
                        if (lang is "prolog" or "ambiguous_pl"
                            && pattern.Kind == "function"
                            && TryGetNextPrologClauseOffset(
                                patternMatchLine,
                                absoluteStartColumn,
                                out var nextPrologClauseOffset))
                        {
                            restartPatternScanOffset = nextPrologClauseOffset;
                            break;
                        }

                        CollectRecordPrimaryComponentSymbols(
                            fileId,
                            lang,
                            lines,
                            i,
                            absoluteStartColumn,
                            kind,
                            name,
                            ref pendingRecordPrimaryComponents,
                            ref recordPrimaryComponentParentIndex,
                            symbols);

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
                                break;
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
                                break;
                            }
                            var advance = statementEnd;
                            if (advance <= lineOffset)
                                advance = lineOffset + 1;
                            if (advance >= patternMatchLine.Length)
                                break;
                            lineOffset = advance;
                            continue;
                        }

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
                                    restartPatternScanOffset = nextHeaderLineMemberOffset;
                                    break;
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
                                        restartPatternScanOffset = TranslateCSharpRawColumnToCollapsed(
                                            csharpMatchColumnToRaw,
                                            i,
                                            rawNextSiblingOffset,
                                            matchLine.Length,
                                            line.Length);
                                        break;
                                    }
                                }
                                else
                                {
                                    var nextSiblingOffset = FindNextSameLineNonClosingBraceStatementStart(matchLine, sameLineEndColumn + 1, lang);
                                    if (nextSiblingOffset > sameLineEndColumn
                                        && nextSiblingOffset < matchLine.Length)
                                    {
                                        restartPatternScanOffset = nextSiblingOffset;
                                        break;
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
                                    break;
                                lineOffset = nextBatchOffset;
                                continue;
                            }

                            // Stop after first match per line to avoid duplicate symbols
                            // (e.g. C# method pattern + constructor pattern both matching)
                            // 1行につき最初のマッチのみ採用し重複を防ぐ
                            stopAfterFirstPatternMatch = true;
                            break;
                        }

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
                                restartPatternScanOffset = nextTypeBodyOffset;
                                break;
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
                            restartPatternScanOffset = nextSameLineOffset;
                            break;
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
                            restartPatternScanOffset = nextSameLineOffset;
                            break;
                        }

                        lineOffset = nextSameLineOffset;
                    }

                    if (restartPatternScanOffset >= 0 || stopAfterFirstPatternMatch)
                        break;
                }

                if (restartPatternScanOffset >= 0)
                {
                    if (restartPatternScanOffset <= patternStartOffset)
                        break;

                    patternStartOffset = restartPatternScanOffset;
                    continue;
                }

                break;
            }

            if (lang == "css" && cssScannerLine != null)
            {
                if (cssScannerLine.IndexOf("--", StringComparison.Ordinal) >= 0)
                {
                    foreach (Match match in Regex.EnumerateMatches(CssInlineCustomPropertyRegex, cssScannerLine))
                    {
                        var propertyName = match.Groups["name"].ValueSpan.Trim().ToString();
                        if (propertyName.Length == 0)
                            continue;

                        AddSymbolRecord(
                            symbols,
                            extractionState,
                            cssSeenSymbols,
                            i + 1,
                            new SymbolRecord
                            {
                                FileId = fileId,
                                Kind = "property",
                                Name = propertyName,
                                Line = i + 1,
                                StartLine = i + 1,
                                EndLine = i + 1,
                                Signature = line.Trim(),
                            });
                    }
                }

                ExtractCssInlineGroupingSelectors(
                    fileId,
                    line,
                    cssScannerLine,
                    cssScannerLines!,
                    i,
                    patterns,
                    symbols,
                    cssSeenSymbols);
            }
        }

        if (!symbols.IsAtCapacity)
        {
            AddSupplementalSymbols(
                fileId,
                originalLang,
                lang,
                content,
                filePath,
                lines,
                structuralLines,
                symbols,
                extractionState,
                getPrivateScopeColumns,
                GetJavaScriptTypeScriptSanitizedLines,
                csharpMatchLines,
                pythonModulePrefix,
                prologMultilineHeads);
        }
        if (lang == "csharp")
        {
            PopulateCSharpPartialDeclarationMetadata(
                lines,
                symbols,
                getCSharpLineStartStates);
        }
        FinalizePatternSymbols(
            fileId,
            lang,
            filePath,
            lines,
            symbols,
            extractionState,
            getCSharpLineStartStates,
            pendingRecordPrimaryComponents);
        return symbols;
    }

    private static readonly Regex PrologOpenClauseRegex = new(
        @"^\s*(?:(?:[a-z][A-Za-z0-9_]*\s*(?:\([^\r\n]*\))?\s*(?::-|-->))|:-)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PrologMultilineHeadStartRegex = new(
        @"^\s*(?<name>[a-z][A-Za-z0-9_]*)\s*(?<open>\()",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PrologBareMultilineHeadStartRegex = new(
        @"^\s*(?<name>[a-z][A-Za-z0-9_]*)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private const int PrologMultilineHeadLookaheadLineLimit = 256;
    private readonly record struct PrologMultilineHead(string Name, int StartColumn);
    private readonly record struct PrologSourcePosition(int LineIndex, int Column);

    private static bool TryGetNextPrologClauseOffset(
        string line,
        int currentClauseOffset,
        out int nextClauseOffset)
    {
        for (var column = Math.Max(0, currentClauseOffset); column < line.Length; column++)
        {
            if (!DynamicDeclarativeReferenceExtractor.IsPrologClauseTerminator(line, column))
                continue;

            for (var candidate = column + 1; candidate < line.Length; candidate++)
            {
                if (char.IsWhiteSpace(line[candidate]))
                    continue;

                if (char.IsLower(line[candidate]))
                {
                    nextClauseOffset = candidate;
                    return true;
                }
                break;
            }
        }

        nextClauseOffset = -1;
        return false;
    }

    private static bool[] BuildPrologClauseContinuationLines(
        IReadOnlyList<string> structuralLines,
        Dictionary<int, PrologMultilineHead> multilineHeads)
    {
        var continuationLines = new bool[structuralLines.Count];
        var matchingParentheses = BuildPrologMatchingParentheses(structuralLines);
        var clauseOpen = false;
        for (var lineIndex = 0; lineIndex < structuralLines.Count; lineIndex++)
        {
            continuationLines[lineIndex] = clauseOpen;
            var line = structuralLines[lineIndex];
            var lastTerminatorColumn = FindLastTopLevelPrologClauseTerminator(line);

            if (clauseOpen && lastTerminatorColumn < 0)
                continue;

            clauseOpen = false;
            var clauseCandidateOffset = lastTerminatorColumn + 1;
            var clauseCandidate = line[clauseCandidateOffset..];
            if (PrologOpenClauseRegex.IsMatch(clauseCandidate))
            {
                clauseOpen = true;
                continue;
            }

            var multilineHead = PrologMultilineHeadStartRegex.Match(clauseCandidate);
            if (multilineHead.Success
                && IsValidatedMultilinePrologHead(
                     structuralLines,
                     lineIndex,
                     clauseCandidateOffset + multilineHead.Groups["open"].Index,
                     matchingParentheses))
            {
                multilineHeads[lineIndex] = new PrologMultilineHead(
                    multilineHead.Groups["name"].Value,
                    clauseCandidateOffset + multilineHead.Groups["name"].Index);
                clauseOpen = true;
                continue;
            }

            var bareMultilineHead = PrologBareMultilineHeadStartRegex.Match(clauseCandidate);
            if (bareMultilineHead.Success
                && IsValidatedBareMultilinePrologHead(structuralLines, lineIndex))
            {
                multilineHeads[lineIndex] = new PrologMultilineHead(
                    bareMultilineHead.Groups["name"].Value,
                    clauseCandidateOffset + bareMultilineHead.Groups["name"].Index);
                clauseOpen = true;
            }
        }

        return continuationLines;
    }

    private static IReadOnlyDictionary<PrologSourcePosition, PrologSourcePosition>
        BuildPrologMatchingParentheses(IReadOnlyList<string> structuralLines)
    {
        var openParentheses = new Stack<PrologSourcePosition>();
        var matchingParentheses = new Dictionary<PrologSourcePosition, PrologSourcePosition>();
        for (var lineIndex = 0; lineIndex < structuralLines.Count; lineIndex++)
        {
            var line = structuralLines[lineIndex];
            for (var column = 0; column < line.Length; column++)
            {
                var ch = line[column];
                if (ch is '\'' or '"')
                {
                    column = SkipPrologQuotedTerm(line, column, ch) - 1;
                    continue;
                }

                if (ch == '(')
                {
                    openParentheses.Push(new PrologSourcePosition(lineIndex, column));
                }
                else if (ch == ')' && openParentheses.TryPop(out var openingParenthesis))
                {
                    matchingParentheses[openingParenthesis] = new PrologSourcePosition(lineIndex, column);
                }
            }
        }

        return matchingParentheses;
    }

    private static int FindLastTopLevelPrologClauseTerminator(string line)
        => FindTopLevelPrologClauseTerminator(line, findLast: true);

    private static int FindFirstTopLevelPrologClauseTerminator(string line)
        => FindTopLevelPrologClauseTerminator(line, findLast: false);

    private static int FindTopLevelPrologClauseTerminator(string line, bool findLast)
    {
        var terminatorColumn = -1;
        var parenthesisDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        for (var column = 0; column < line.Length; column++)
        {
            var ch = line[column];
            if (ch is '\'' or '"')
            {
                column = SkipPrologQuotedTerm(line, column, ch) - 1;
                continue;
            }

            switch (ch)
            {
                case '(':
                    parenthesisDepth++;
                    continue;
                case ')' when parenthesisDepth > 0:
                    parenthesisDepth--;
                    continue;
                case '[':
                    bracketDepth++;
                    continue;
                case ']' when bracketDepth > 0:
                    bracketDepth--;
                    continue;
                case '{':
                    braceDepth++;
                    continue;
                case '}' when braceDepth > 0:
                    braceDepth--;
                    continue;
            }

            if (ch != '.'
                || parenthesisDepth != 0
                || bracketDepth != 0
                || braceDepth != 0)
            {
                continue;
            }

            var previous = column > 0 ? line[column - 1] : '\0';
            var next = column + 1 < line.Length ? line[column + 1] : '\0';
            if (previous != '.'
                && next != '.'
                && !(char.IsDigit(previous) && char.IsDigit(next))
                && (next == '\0' || char.IsWhiteSpace(next)))
            {
                terminatorColumn = column;
                if (!findLast)
                    break;
            }
        }

        return terminatorColumn;
    }

    private static bool IsValidatedBareMultilinePrologHead(
        IReadOnlyList<string> structuralLines,
        int startLineIndex)
    {
        var endLineExclusive = Math.Min(
            structuralLines.Count,
            startLineIndex + PrologMultilineHeadLookaheadLineLimit);
        for (var lineIndex = startLineIndex + 1; lineIndex < endLineExclusive; lineIndex++)
        {
            var line = structuralLines[lineIndex];
            for (var column = 0; column < line.Length; column++)
            {
                if (char.IsWhiteSpace(line[column]))
                    continue;
                return line.AsSpan(column).StartsWith(":-", StringComparison.Ordinal)
                    || line.AsSpan(column).StartsWith("-->", StringComparison.Ordinal)
                    || DynamicDeclarativeReferenceExtractor.IsPrologClauseTerminator(line, column);
            }
        }

        return false;
    }

    private static bool IsValidatedMultilinePrologHead(
        IReadOnlyList<string> structuralLines,
        int startLineIndex,
        int openingParenthesisColumn,
        IReadOnlyDictionary<PrologSourcePosition, PrologSourcePosition> matchingParentheses)
    {
        var endLineExclusive = Math.Min(
            structuralLines.Count,
            startLineIndex + PrologMultilineHeadLookaheadLineLimit);
        if (!matchingParentheses.TryGetValue(
                new PrologSourcePosition(startLineIndex, openingParenthesisColumn),
                out var closingParenthesis)
            || closingParenthesis.LineIndex >= endLineExclusive)
        {
            return false;
        }

        for (var lineIndex = closingParenthesis.LineIndex; lineIndex < endLineExclusive; lineIndex++)
        {
            var line = structuralLines[lineIndex];
            var startColumn = lineIndex == closingParenthesis.LineIndex
                ? closingParenthesis.Column + 1
                : 0;
            for (var column = startColumn; column < line.Length; column++)
            {
                var ch = line[column];
                if (ch is '\'' or '"')
                {
                    column = SkipPrologQuotedTerm(line, column, ch) - 1;
                    continue;
                }

                if (char.IsWhiteSpace(ch))
                    continue;
                if (line.AsSpan(column).StartsWith(":-", StringComparison.Ordinal)
                    || line.AsSpan(column).StartsWith("-->", StringComparison.Ordinal)
                    || DynamicDeclarativeReferenceExtractor.IsPrologClauseTerminator(line, column))
                {
                    return true;
                }

                return false;
            }
        }

        return false;
    }

    private static void AddPrologMultilineHeadSymbols(
        long fileId,
        IReadOnlyList<string> lines,
        List<SymbolRecord> symbols,
        SymbolExtractionState extractionState,
        IReadOnlyDictionary<int, PrologMultilineHead> multilineHeads)
    {
        foreach (var (lineIndex, multilineHead) in multilineHeads)
        {
            var lineNumber = lineIndex + 1;
            var line = lines[lineIndex];
            AddSymbolRecord(
                symbols,
                extractionState,
                cssSeenSymbols: null,
                lineNumber,
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "function",
                    Name = multilineHead.Name,
                    Line = lineNumber,
                    StartLine = lineNumber,
                    StartColumn = multilineHead.StartColumn,
                    EndLine = lineNumber,
                    Signature = line[multilineHead.StartColumn..].Trim(),
                },
                line);
        }
    }

    private static bool HasPrologClauseTerminator(string line)
    {
        for (var column = 0; column < line.Length; column++)
        {
            if (line[column] is '\'' or '"')
            {
                column = SkipPrologQuotedTerm(line, column, line[column]) - 1;
                continue;
            }

            if (DynamicDeclarativeReferenceExtractor.IsPrologClauseTerminator(line, column))
                return true;
        }

        return false;
    }

    private static int SkipPrologQuotedTerm(string line, int startColumn, char delimiter)
    {
        for (var column = startColumn + 1; column < line.Length; column++)
        {
            if (line[column] == '\\')
            {
                column++;
                continue;
            }

            if (line[column] != delimiter)
                continue;

            if (column + 1 < line.Length && line[column + 1] == delimiter)
            {
                column++;
                continue;
            }

            return column + 1;
        }

        return line.Length;
    }

    private const int CSharpFieldInitializerSignatureLimit = 1024;

    private static string BoundCSharpFieldInitializerSignature(string signature)
    {
        if (signature.Length <= CSharpFieldInitializerSignatureLimit
            || !signature.Contains('='))
        {
            return signature;
        }

        // Field signatures are metadata, not bodies. Large object/collection initializers can
        // otherwise consume an entire CLI, JSON, MCP, or LSP response budget after multiline
        // signatures are collapsed to one line. Replace each top-level initializer with a
        // deterministic marker so modifiers, type, and every declarator remain readable without
        // persisting an arbitrary initializer prefix. Keep the legacy hard limit as a final guard
        // for pathologically large declarator lists. #4445, #4865
        // field signature は body ではなくメタデータである。複数行を1行へ畳み込んだ巨大な
        // object/collection initializer が CLI / JSON / MCP / LSP の応答予算を使い切らないよう、
        // top-level initializer を決定的 marker に置換し、任意の initializer prefix を保存せずに
        // modifier / type / 全 declarator を読める形で維持する。異常に長い declarator list には
        // 従来の hard limit を最終ガードとして残す。#4445, #4865
        var sanitized = LexCSharpLine(signature, new CSharpLexState()).SanitizedLine;
        var summarized = new StringBuilder(Math.Min(signature.Length, CSharpFieldInitializerSignatureLimit));
        var copyStart = 0;
        var searchStart = 0;

        while (TryFindCSharpTopLevelInitializerAssignment(sanitized, searchStart, out var assignmentColumn))
        {
            summarized.Append(signature.AsSpan(copyStart, assignmentColumn - copyStart).TrimEnd());
            summarized.Append(" = …");

            var delimiterColumn = FindCSharpTopLevelInitializerDelimiter(sanitized, assignmentColumn + 1);
            if (delimiterColumn >= signature.Length)
            {
                summarized.Append(';');
                copyStart = signature.Length;
                break;
            }

            summarized.Append(signature[delimiterColumn]);
            copyStart = delimiterColumn + 1;
            searchStart = copyStart;
        }

        if (copyStart < signature.Length)
            summarized.Append(signature.AsSpan(copyStart));

        var result = summarized.ToString().Trim();
        return result.Length <= CSharpFieldInitializerSignatureLimit
            ? result
            : string.Concat(result.AsSpan(0, CSharpFieldInitializerSignatureLimit - 2), "…;");
    }

    private static bool TryFindCSharpTopLevelInitializerAssignment(
        string sanitized,
        int startColumn,
        out int assignmentColumn)
    {
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;

        for (var column = startColumn; column < sanitized.Length; column++)
        {
            var ch = sanitized[column];
            switch (ch)
            {
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
                case '{':
                    braceDepth++;
                    continue;
                case '}' when braceDepth > 0:
                    braceDepth--;
                    continue;
                case '<' when TryMatchCSharpGenericBracket(sanitized, column, out var genericEnd):
                    column = genericEnd;
                    continue;
            }

            if (ch != '=' || parenDepth != 0 || bracketDepth != 0 || braceDepth != 0)
                continue;

            var previous = column > 0 ? sanitized[column - 1] : '\0';
            var next = column + 1 < sanitized.Length ? sanitized[column + 1] : '\0';
            if (next is '=' or '>'
                || previous is '=' or '!' or '<' or '>' or '+' or '-' or '*' or '/' or '%'
                    or '&' or '|' or '^' or '?')
            {
                continue;
            }

            assignmentColumn = column;
            return true;
        }

        assignmentColumn = -1;
        return false;
    }

    private static int FindCSharpTopLevelInitializerDelimiter(string sanitized, int startColumn)
    {
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;

        for (var column = startColumn; column < sanitized.Length; column++)
        {
            var ch = sanitized[column];
            switch (ch)
            {
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
                case '{':
                    braceDepth++;
                    continue;
                case '}' when braceDepth > 0:
                    braceDepth--;
                    continue;
                case '<' when TryMatchCSharpGenericBracket(sanitized, column, out var genericEnd):
                    column = genericEnd;
                    continue;
            }

            if (parenDepth == 0
                && bracketDepth == 0
                && braceDepth == 0
                && ch is ',' or ';')
            {
                return column;
            }
        }

        return sanitized.Length;
    }

    private static void AddScriptScopeSymbol(long fileId, string[] lines, List<SymbolRecord> symbols)
    {
        if (lines.Length == 0)
            return;

        // Add this after AssignContainers so the synthetic file-wide scope can own top-level
        // references without making every declared function appear nested under `<script>`.
        // AssignContainers の後で追加し、top-level reference の帰属先だけを提供する。
        // 宣言済み関数の親を `<script>` に変えないことで既存の symbol contract を維持する。
        symbols.Add(new SymbolRecord
        {
            FileId = fileId,
            Kind = "function",
            SubKind = "script_scope",
            Name = "<script>",
            Line = 1,
            StartLine = 1,
            EndLine = lines.Length,
            BodyStartLine = 1,
            BodyEndLine = lines.Length,
            Signature = "<script>",
        });
    }


}
