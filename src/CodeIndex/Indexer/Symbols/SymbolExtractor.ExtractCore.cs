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
        CancellationToken cancellationToken = default)
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

        if (lang == "xml")
        {
            var xmlLines = SplitContentLines(content);
            return ExtractXmlSymbols(fileId, content, xmlLines);
        }

        if (lang == "json")
        {
            return ExtractJsonSymbols(fileId, content, SplitContentLines(content));
        }

        if (lang == "jsonl")
        {
            return ExtractJsonLinesSymbols(fileId, content, SplitContentLines(content));
        }

        if (lang is "toml" or "gitignore" or "gitattributes" or "editorconfig" or "dockerignore" or "config")
        {
            return ExtractRepositoryMetadataSymbols(fileId, lang, SplitContentLines(content));
        }

        if (lang == "yaml")
        {
            return ExtractYamlSymbols(fileId, SplitContentLines(content));
        }

        if (lang == "msbuild")
        {
            return ExtractMsBuildSymbols(fileId, content, SplitContentLines(content));
        }

        if (lang == "solution")
        {
            return ExtractSolutionSymbols(fileId, SplitContentLines(content));
        }

        if (lang == "app_manifest")
        {
            return ExtractAppManifestSymbols(fileId, content, SplitContentLines(content));
        }

        if (lang is "dependency_manifest" or "dependency_lock")
        {
            return DependencyPackageExtractor.ExtractSymbols(fileId, content, SplitContentLines(content), filePath, lang);
        }

        if (lang == "ambiguous_m")
        {
            var matlabContent = AmbiguousMContentMasker.MaskComments(
                content,
                maskMatlabComments: true,
                maskObjectiveCComments: true);
            var objectiveCContent = AmbiguousMContentMasker.MaskComments(
                content,
                maskMatlabComments: true,
                maskObjectiveCComments: true,
                preserveObjectiveCModuloExpressions: true);
            var matlabSymbols = ExtractCore(
                fileId,
                "matlab",
                matlabContent,
                contentIsNormalized: true,
                hasOversizeLine: false,
                conflictMarkerLine: 0,
                filePath,
                projectRoot,
                patternConfigsAlreadyLoaded: true,
                cancellationToken);
            var objectiveCSymbols = ExtractCore(
                fileId,
                "objc",
                objectiveCContent,
                contentIsNormalized: true,
                hasOversizeLine: false,
                conflictMarkerLine: 0,
                filePath,
                projectRoot,
                patternConfigsAlreadyLoaded: true,
                cancellationToken);
            matlabSymbols.AddRange(objectiveCSymbols);
            return matlabSymbols;
        }

        if (lang == "markdown")
        {
            var markdownLines = SplitContentLines(content);
            var markdownSymbols = ExtractMarkdownSymbols(fileId, markdownLines);
            AssignContainers(markdownSymbols, markdownLines, null);
            PopulateDeclaredContainerQualifiedNames(markdownSymbols);
            return markdownSymbols;
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

        var pythonModulePrefix = lang == "python"
            ? GetPythonModulePrefix(filePath)
            : null;

        var structuralMaskLanguage = lang == "cython" ? "python" : lang;
        var structuralLines = StructuralLineMasker.MaskLines(structuralMaskLanguage, lines);
        if (lang is "d" or "julia" or "matlab" or "nim")
            structuralLines = ScientificNativeCommentMasker.MaskBlockComments(lang, structuralLines);
        var scientificBodyScannerLines = lang is "julia" or "matlab"
            ? PrepareScientificBodyScannerLines(structuralLines, lang)
            : null;
        var matlabExplicitOuterClosureByLine = lang == "matlab" && scientificBodyScannerLines != null
            ? BuildMatlabExplicitOuterClosureMap(scientificBodyScannerLines)
            : null;
        string[]? javaScriptTypeScriptSanitizedLines = null;
        string[] GetJavaScriptTypeScriptSanitizedLines() =>
            javaScriptTypeScriptSanitizedLines ??= BuildJavaScriptTypeScriptSanitizedLines(lines);
        var cssScannerLines = lang == "css"
            ? MaskCssScannerLines(lines)
            : null;
        var sassStylusScannerLines = lang is "sass" or "stylus"
            ? MaskSassStylusBlockCommentLines(lang, lines)
            : null;
        var shellScannerLines = lang == "shell"
            ? MaskShellHeredocLines(lines)
            : null;
        var powershellEnumBodyLines = lang == "powershell"
            ? FindPowerShellEnumBodyLines(structuralLines)
            : null;
        int[]?[] csharpMatchColumnToRaw = null!;
        var csharpMatchLines = lang == "csharp"
            ? BuildCSharpMatchLines(lines, out csharpMatchColumnToRaw)
            : null;
        CSharpLexState[]? csharpLineStartStates = null;
        CSharpLexState[] GetCSharpLineStartStates() =>
            csharpLineStartStates ??= BuildCSharpLineStartStates(lines);
        Func<CSharpLexState[]>? getCSharpLineStartStates = lang == "csharp"
            ? GetCSharpLineStartStates
            : null;
        DartClassBodyScope? dartInsideClassBody = null;
        DartClassBodyScope GetDartInsideClassBody() =>
            dartInsideClassBody ??= BuildDartClassBodyScope(structuralLines);
        JavaScriptScopePrivacyFlags[][]? privateScopeColumns = null;
        JavaScriptScopePrivacyFlags[][] GetPrivateScopeColumns() =>
            privateScopeColumns ??= BuildJavaScriptTypeScriptPrivateScopeColumns(lines, lang!);
        Func<JavaScriptScopePrivacyFlags[][]>? getPrivateScopeColumns = lang is "javascript" or "typescript"
            ? GetPrivateScopeColumns
            : null;
        CSharpTypeBodyScope? csharpInsideTypeBody = null;
        CSharpTypeBodyScope GetCSharpInsideTypeBody() =>
            csharpInsideTypeBody ??= BuildCSharpTypeBodyScope(structuralLines);
        CSharpCallableParameterScope? csharpCallableParameterScope = null;
        CSharpCallableParameterScope GetCSharpCallableParameterScope() =>
            csharpCallableParameterScope ??= BuildCSharpCallableParameterScope(structuralLines, GetCSharpInsideTypeBody());
        bool[]? csharpSwitchExpressionLines = null;
        var csharpSwitchExpressionLinesInitialized = false;
        bool[]? GetCSharpSwitchExpressionLines()
        {
            if (!csharpSwitchExpressionLinesInitialized)
            {
                csharpSwitchExpressionLinesInitialized = true;
                csharpSwitchExpressionLines = LinesContain(structuralLines, "switch", StringComparison.Ordinal)
                    ? FindCSharpSwitchExpressionLines(structuralLines)
                    : null;
            }

            return csharpSwitchExpressionLines;
        }

        Func<bool[]?>? getCSharpSwitchExpressionLines = lang == "csharp"
            ? GetCSharpSwitchExpressionLines
            : null;
        bool[]? cssQualifiedRuleAncestors = null;
        bool[] GetCssQualifiedRuleAncestors() =>
            cssQualifiedRuleAncestors ??= FindCssQualifiedRuleAncestors(cssScannerLines!);
        Func<bool[]?>? getCssQualifiedRuleAncestors = lang == "css"
            ? GetCssQualifiedRuleAncestors
            : null;
        var fsharpTypeBodyState = FSharpTypeBodyState.None;
        var initialSymbolCapacity = EstimateSymbolListInitialCapacity(lines.Length);
        var symbols = new SymbolExtractionList(initialSymbolCapacity);
        var extractionState = symbols.ExtractionState;
        List<PendingRecordPrimaryComponents>? pendingRecordPrimaryComponents = null;
        RecordPrimaryComponentParentIndex? recordPrimaryComponentParentIndex = null;
        var cssSeenSymbols = lang == "css"
            ? new HashSet<SymbolLineIdentity>()
            : null;
        var dockerfileStageNames = lang == "dockerfile"
            ? new HashSet<string>(StringComparer.Ordinal)
            : null;
        var csharpSuppressedContinuationUntil = -1;
        var csharpSuppressedContinuationResumeLine = -1;
        var csharpSuppressedContinuationResumeRawColumn = 0;
        var goImportBlock = false;

        for (int i = 0; i < lines.Length; i++)
        {
            if ((i & 0x3f) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            if (lang == "csharp" && i <= csharpSuppressedContinuationUntil)
                continue;

            var line = lines[i];
            if (lang == "csharp" && IsCSharpLineCommentOnly(line))
                continue;

            if (lang == "go"
                && TryHandleGoBlockLine(fileId, line, i, symbols, extractionState, ref goImportBlock))
            {
                continue;
            }
            if (lang == "go")
                TryAddGoLabelSymbol(fileId, line, i, symbols, extractionState);
            if (lang == "r" && TryAddRPacmanPackageLoaderSymbols(fileId, line, i + 1, symbols, extractionState))
                continue;

            if (lang == "dockerfile")
            {
                AddDockerfileAdditionalSymbols(fileId, line, i + 1, symbols, dockerfileStageNames!);
            }

            var structuralLine = structuralLines[i];
            var cssScannerLine = cssScannerLines?[i];
            var sassStylusScannerLine = sassStylusScannerLines?[i];
            var shellScannerLine = shellScannerLines?[i];
            var matchLine = structuralLine;
            if (lang == "css" && cssScannerLine != null)
            {
                // Use raw CSS text for symbol-name matching so quoted selector payloads and
                // @import values stay queryable, while brace/depth scans still rely on the
                // separately masked scanner lines.
                // CSS のシンボル名マッチは raw line を使い、引用付きセレクタや @import 値を
                // 保持する。brace/depth 判定だけ別の scanner line を使う。
                matchLine = line;
            }
            else if (lang is "sass" or "stylus" && sassStylusScannerLine != null)
            {
                matchLine = sassStylusScannerLine;
            }
            else if (lang == "shell" && shellScannerLine != null)
            {
                matchLine = shellScannerLine;
            }
            else if (lang == "csharp")
            {
                matchLine = csharpMatchLines![i];
            }

            var fortranContinuationCandidate = lang == "fortran"
                ? TryBuildFortranContinuationMatchLine(lines, i)
                : null;
            if (fortranContinuationCandidate != null)
                matchLine = fortranContinuationCandidate.Value.MatchLine;

            if (lang == "fsharp")
                TryAddFSharpTypeMemberSymbols(symbols, fileId, line, i + 1, ref fsharpTypeBodyState);

            if (lang == "fsharp"
                && TryAddFSharpRecordFieldsFromContext(symbols, fileId, lines, i, line, i + 1))
            {
                continue;
            }

            if (lang == "fsharp" && TryAddFSharpActivePatternSymbols(symbols, fileId, line, i + 1))
                continue;

            if (lang == "fsharp" && TryAddFSharpOperatorSymbols(symbols, fileId, line, i + 1))
                continue;

            if (lang == "php")
                ExtractPhpImportSymbols(symbols, line, i + 1);

            if (lang is "javascript" or "typescript")
            {
                if (line.IndexOf("import", StringComparison.Ordinal) >= 0
                    || line.IndexOf("require", StringComparison.Ordinal) >= 0
                    || line.IndexOf("URL", StringComparison.Ordinal) >= 0
                    || line.IndexOf("importScripts", StringComparison.Ordinal) >= 0
                    || line.IndexOf("serviceWorker", StringComparison.Ordinal) >= 0
                    || line.IndexOf("register", StringComparison.Ordinal) >= 0
                    || line.IndexOf("addModule", StringComparison.Ordinal) >= 0
                    || line.IndexOf("Worker", StringComparison.Ordinal) >= 0)
                {
                    var jsTsSanitizedLines = GetJavaScriptTypeScriptSanitizedLines();
                    var sanitizedLine = jsTsSanitizedLines[i];
                    if (sanitizedLine.IndexOf("import", StringComparison.Ordinal) >= 0)
                    {
                        ExtractJavaScriptTypeScriptDynamicImportSymbols(fileId, lang, filePath, projectRoot, lines, jsTsSanitizedLines, i, symbols);
                        ExtractJavaScriptTypeScriptStaticImportModuleSymbols(fileId, lang, filePath, projectRoot, lines, jsTsSanitizedLines, i, symbols);
                        ExtractJavaScriptTypeScriptImportMetaResolveModuleSymbols(fileId, lang, filePath, projectRoot, lines, jsTsSanitizedLines, i, symbols);
                    }

                    if (sanitizedLine.IndexOf("require", StringComparison.Ordinal) >= 0)
                        ExtractJavaScriptTypeScriptRequireModuleSymbols(fileId, lang, filePath, projectRoot, lines, jsTsSanitizedLines, i, symbols);
                    if (sanitizedLine.IndexOf("URL", StringComparison.Ordinal) >= 0)
                        ExtractJavaScriptTypeScriptNewUrlModuleSymbols(fileId, lang, filePath, projectRoot, lines, jsTsSanitizedLines, i, symbols);
                    if (sanitizedLine.IndexOf("importScripts", StringComparison.Ordinal) >= 0)
                        ExtractJavaScriptTypeScriptImportScriptsModuleSymbols(fileId, lang, filePath, projectRoot, lines, jsTsSanitizedLines, i, symbols);
                    if (sanitizedLine.IndexOf("serviceWorker", StringComparison.Ordinal) >= 0
                        || sanitizedLine.IndexOf("register", StringComparison.Ordinal) >= 0)
                    {
                        ExtractJavaScriptTypeScriptServiceWorkerRegisterModuleSymbols(fileId, lang, filePath, projectRoot, lines, jsTsSanitizedLines, i, symbols);
                    }

                    if (sanitizedLine.IndexOf("addModule", StringComparison.Ordinal) >= 0)
                        ExtractJavaScriptTypeScriptWorkletAddModuleSymbols(fileId, lang, filePath, projectRoot, lines, jsTsSanitizedLines, i, symbols);
                    if (sanitizedLine.IndexOf("Worker", StringComparison.Ordinal) >= 0)
                        ExtractJavaScriptTypeScriptWorkerConstructorModuleSymbols(fileId, lang, filePath, projectRoot, lines, jsTsSanitizedLines, i, symbols);
                }
            }

            if (lang is "javascript" or "typescript"
                && TryHandleJavaScriptTypeScriptImportEqualsLine(fileId, lang, filePath, projectRoot, line, i + 1, symbols))
            {
                continue;
            }

            if (lang == "cpp" && TryAddCppIndentedAlias(fileId, line, i + 1, symbols))
                continue;

            // Batch `rem` / `@rem` / `::` comment lines contain the same `&` / `(` / `else` /
            // `do` boundary tokens that the property regex now accepts for inline `set`
            // capture, so `REM & set FAKE=1` or `:: else set FAKE=2` would otherwise leak a
            // phantom property. Short-circuit those lines before any pattern fires — batch
            // labels never match on `::` / `rem` lines anyway because the label regex
            // requires `:<name-char>`, not `::` or `r`.
            // batch の `rem` / `@rem` / `::` コメント行は、inline `set` 捕捉のために property 正規表現が
            // 受け付ける `&` / `(` / `else` / `do` の境界トークンを含みうるため、`REM & set FAKE=1` や
            // `:: else set FAKE=2` が偽 property を出す恐れがある。パターン適用前に当該行ごと
            // 早期スキップする — batch ラベル側は `::` / `rem` 行ではそもそも `:<名前文字>` の要件を
            // 満たさないため影響を受けない。
            if (lang == "batch" && IsBatchCommentLine(line))
                continue;

            if (string.IsNullOrWhiteSpace(matchLine))
                continue;

            var patternStartOffset = lang is "javascript" or "typescript"
                ? FindNextJavaScriptTypeScriptStatementStart(matchLine, 0)
                : 0;
            if (lang == "csharp" && patternStartOffset == 0)
            {
                var firstNonWhitespace = 0;
                while (firstNonWhitespace < matchLine.Length && char.IsWhiteSpace(matchLine[firstNonWhitespace]))
                    firstNonWhitespace++;

                if (firstNonWhitespace < matchLine.Length
                    && matchLine[firstNonWhitespace] is '}' or ';' or '"')
                    patternStartOffset = FindNextSameLineNonClosingBraceStatementStart(matchLine, firstNonWhitespace + 1, lang);
            }
            if (lang == "csharp" && i == csharpSuppressedContinuationResumeLine)
            {
                patternStartOffset = Math.Max(
                    patternStartOffset,
                    TranslateCSharpRawColumnToCollapsed(
                        csharpMatchColumnToRaw,
                        i,
                        csharpSuppressedContinuationResumeRawColumn,
                        matchLine.Length,
                        line.Length));
            }
            while (patternStartOffset >= 0 && patternStartOffset < matchLine.Length)
            {
                var stopAfterFirstPatternMatch = false;
                var restartPatternScanOffset = -1;
                CSharpPropertyMatchCandidate? csharpPropertyCandidateForLine = null;
                foreach (var pattern in patterns)
                {
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

                        // Gate the C# plain-field pattern (kind `property`, BodyStyle.None) to
                        // lines that sit directly inside a type body. Without this gate, local
                        // variable declarations inside method / property / accessor / lambda
                        // bodies match the same shape and leak into `symbols`, `definition`,
                        // `outline`, `inspect`, and `unused` as phantom property symbols.
                        // Closes #298 follow-up (codex review blocker).
                        // C# の通常フィールド用パターン（kind `property` かつ BodyStyle.None）は
                        // 型本体（class / struct / interface / record / enum の直下）でしか
                        // 許可しない。このゲートを入れないと、メソッド・プロパティ・アクセサ・
                        // ラムダの内部にあるローカル変数宣言が同じ形でマッチしてしまい、
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

                        var csharpSingleLineCollapsedMatch = lang == "csharp"
                            && csharpMatchLines != null
                            && ReferenceEquals(patternMatchLine, csharpMatchLines[i]);
                        var csharpSignatureRawStartColumn = csharpGateRawStartColumn;
                        var csharpSameLineBraceStartColumn = csharpSingleLineCollapsedMatch
                            ? absoluteStartColumn
                            : csharpSignatureRawStartColumn;
                        var sameLineEndColumn = pattern.BodyStyle == BodyStyle.Brace
                            && bodyEndLine == startLine
                            ? (lang == "csharp" && csharpSingleLineCollapsedMatch
                                ? FindCSharpSameLineBraceEndColumnFromSanitized(patternMatchLine, csharpSameLineBraceStartColumn)
                                : FindSameLineBraceEndColumn(line, csharpSameLineBraceStartColumn, lang, kind))
                            : -1;
                        var sameLineEndUsesRawColumns = pattern.BodyStyle == BodyStyle.Brace
                            && bodyEndLine == startLine
                            && !(lang == "csharp" && csharpSingleLineCollapsedMatch);
                        if (lang == "csharp"
                            && csharpSingleLineCollapsedMatch
                            && CanUseCSharpSameLineSemicolonEndColumn(kind))
                        {
                            var semicolonEndColumn = FindCSharpSameLineSemicolonEndColumn(patternMatchLine, absoluteStartColumn);
                            if (semicolonEndColumn >= absoluteStartColumn
                                && (sameLineEndColumn < absoluteStartColumn || semicolonEndColumn < sameLineEndColumn))
                            {
                                sameLineEndColumn = semicolonEndColumn;
                                sameLineEndUsesRawColumns = false;
                            }
                        }
                        if (lang == "csharp"
                            && kind == "event"
                            && pattern.BodyStyle == BodyStyle.None
                            && HasCSharpEventAccessorStart(patternMatchLine[absoluteStartColumn..]))
                        {
                            // Same-line accessor events (`event E { add {} remove {} }`) share the
                            // sibling-stream requirement with semicolon-bodied members: their
                            // signature must stop at the accessor block so later same-line siblings
                            // can restart the full pattern scan. Without this brace clamp, the
                            // stored event signature swallows the following declaration and the
                            // later sibling never reaches earlier patterns such as property.
                            // Closes #520.
                            // 同一行 accessor event (`event E { add {} remove {} }`) も semicolon 系
                            // member と同様に sibling stream として扱う必要がある。そのため
                            // accessor block の閉じ `}` で signature を切り、後続の same-line
                            // sibling が property など先頭側 pattern へ再到達できるようにする。
                            // これが無いと event signature が後続宣言を飲み込み、後続 sibling が
                            // earlier pattern に届かない。Closes #520.
                            var braceEndColumn = csharpSingleLineCollapsedMatch
                                ? FindCSharpSameLineBraceEndColumnFromSanitized(patternMatchLine, csharpSameLineBraceStartColumn)
                                : FindSameLineBraceEndColumn(line, csharpSameLineBraceStartColumn, lang, kind);
                            if (braceEndColumn >= absoluteStartColumn
                                && (sameLineEndColumn < absoluteStartColumn || braceEndColumn < sameLineEndColumn))
                            {
                                sameLineEndColumn = braceEndColumn;
                                sameLineEndUsesRawColumns = !(lang == "csharp" && csharpSingleLineCollapsedMatch);
                            }
                        }
                        if (sameLineEndColumn < absoluteStartColumn
                            && lang == "csharp"
                            && kind == "enum"
                            && pattern.BodyStyle == BodyStyle.None)
                        {
                            sameLineEndColumn = FindCSharpSameLineEnumMemberEndColumn(patternMatchLine, absoluteStartColumn);
                            sameLineEndUsesRawColumns = false;
                        }
                        string signature;
                        if (csharpWrappedModifierPrefix != null)
                        {
                            // Wrapped ctor signature: prepend the modifier prefix recovered from
                            // preceding modifier-only lines so the stored signature reflects the
                            // full declaration (`static Foo() { ... }`) rather than only the name
                            // line. Honor the same-line brace body truncation when present so the
                            // signature does not absorb the entire ctor body. Closes #348.
                            // ラップされたコンストラクタのシグネチャ: 直前のモディファイアのみ行から
                            // 復元した prefix を付与し、識別子行だけでなく宣言全体
                            // (`static Foo() { ... }`) を保存する。同一行に brace 本体が閉じる
                            // ケースではその末尾で切り詰め、シグネチャが本体全体を飲み込まない
                            // ようにする。Closes #348.
                            var nameLineStartColumn = csharpSingleLineCollapsedMatch
                                ? (sameLineEndUsesRawColumns
                                    ? csharpSignatureRawStartColumn
                                    : csharpSignatureRawStartColumn)
                                : absoluteStartColumn;
                            var nameLineEndExclusive = sameLineEndColumn >= absoluteStartColumn
                                ? (sameLineEndUsesRawColumns
                                    ? Math.Min(sameLineEndColumn + 1, line.Length)
                                    : Math.Min(
                                        TranslateCSharpCollapsedColumnToRaw(
                                            csharpMatchColumnToRaw,
                                            i,
                                            sameLineEndColumn,
                                            line.Length) + 1,
                                        line.Length))
                                : line.Length;
                            var nameLineContent = sameLineEndColumn >= absoluteStartColumn
                                ? line.AsSpan(nameLineStartColumn, nameLineEndExclusive - nameLineStartColumn)
                                : line.AsSpan(nameLineStartColumn);
                            var signatureBuilder = new StringBuilder(csharpWrappedModifierPrefix.Length + 1 + nameLineContent.Length);
                            signatureBuilder.Append(csharpWrappedModifierPrefix);
                            signatureBuilder.Append(' ');
                            signatureBuilder.Append(nameLineContent.TrimStart());
                            signature = signatureBuilder.ToString().Trim();
                        }
                        else if (lang == "csharp"
                            && pattern.Kind == "function"
                            && pattern.BodyStyle == BodyStyle.Brace
                            && bodyStartLine.HasValue
                            && bodyEndLine != startLine
                            && !IsCSharpMultilineExpressionBodiedMember(
                                lines,
                                i,
                                csharpSignatureRawStartColumn)
                            && TryFindCSharpBraceBodyHeaderExtent(
                                lines,
                                i,
                                Math.Min(csharpSignatureRawStartColumn, line.Length),
                                out var csharpBraceHeaderLastLineIndex,
                                out var csharpBraceHeaderLastLineExclusiveEndColumn))
                        {
                            signature = BuildCSharpMultilineSignature(
                                lines,
                                i,
                                Math.Min(csharpSignatureRawStartColumn, line.Length),
                                csharpBraceHeaderLastLineIndex,
                                csharpBraceHeaderLastLineExclusiveEndColumn);
                        }
                        else if (sameLineEndColumn >= absoluteStartColumn)
                        {
                            if (lang == "csharp"
                                && csharpSingleLineCollapsedMatch)
                            {
                                var rawStart = csharpSignatureRawStartColumn;
                                var rawEndInclusive = sameLineEndUsesRawColumns
                                    ? sameLineEndColumn
                                    : TranslateCSharpCollapsedColumnToRaw(
                                        csharpMatchColumnToRaw,
                                        i,
                                        sameLineEndColumn,
                                        line.Length);
                                var rawEndExclusive = Math.Min(rawEndInclusive + 1, line.Length);
                                if (rawStart > line.Length)
                                    rawStart = line.Length;
                                if (rawEndExclusive <= rawStart)
                                    rawEndExclusive = Math.Min(rawStart + Math.Max(1, match.Length), line.Length);
                                signature = line[rawStart..rawEndExclusive].Trim();
                            }
                            else
                            {
                                var signatureStartColumn = csharpSingleLineCollapsedMatch && sameLineEndUsesRawColumns
                                    ? csharpSignatureRawStartColumn
                                    : absoluteStartColumn;
                                var signatureEndExclusive = Math.Min(sameLineEndColumn + 1, line.Length);
                                if (signatureEndExclusive <= signatureStartColumn)
                                    signatureEndExclusive = Math.Min(signatureStartColumn + Math.Max(1, match.Length), line.Length);
                                signature = line[signatureStartColumn..signatureEndExclusive].Trim();
                            }
                        }
                        else if (lang == "csharp"
                            && pattern.BodyStyle == BodyStyle.None
                            && TryFindCSharpSemicolonTerminatedSignatureExtent(
                                lines,
                                i,
                                csharpGateRawStartColumn,
                                out var csharpFieldSignatureLastLineIndex,
                                out var csharpFieldSignatureLastLineExclusiveEndColumn)
                            && csharpFieldSignatureLastLineIndex > i)
                        {
                            signature = BuildCSharpMultilineSignature(
                                lines,
                                i,
                                csharpGateRawStartColumn,
                                csharpFieldSignatureLastLineIndex,
                                csharpFieldSignatureLastLineExclusiveEndColumn);
                        }
                        else if (lang == "csharp"
                            && pattern.BodyStyle == BodyStyle.Brace
                            && IsCSharpMultilineExpressionBodiedMember(
                                lines,
                                i,
                                csharpSignatureRawStartColumn)
                            && TryFindCSharpSemicolonTerminatedSignatureExtent(
                                lines,
                                i,
                                csharpSignatureRawStartColumn,
                                out var csharpSemicolonSignatureLastLineIndex,
                                out var csharpSemicolonSignatureLastLineExclusiveEndColumn)
                            && csharpSemicolonSignatureLastLineIndex > i)
                        {
                            signature = BuildCSharpMultilineSignature(
                                lines,
                                i,
                                csharpSignatureRawStartColumn,
                                csharpSemicolonSignatureLastLineIndex,
                                csharpSemicolonSignatureLastLineExclusiveEndColumn);
                        }
                        else if (lang == "csharp" && csharpPropertyCandidate.LastConsumedLineIndex > i)
                        {
                            signature = BuildCSharpMultilineSignature(
                                lines,
                                i,
                                csharpSignatureRawStartColumn,
                                csharpPropertyCandidate.SignatureLastLineIndex,
                                csharpPropertyCandidate.SignatureLastLineExclusiveEndColumn);
                        }
                        else if (lang == "csharp"
                            && pattern.Kind is "class" or "struct" or "interface" or "enum"
                            && TryFindCSharpTypeHeaderExtent(
                                lines,
                                i,
                                csharpSignatureRawStartColumn,
                                out var csharpTypeHeaderLastLineIndex,
                                out var csharpTypeHeaderLastLineExclusiveEndColumn)
                            && csharpTypeHeaderLastLineIndex > i)
                        {
                            // Wrapped C# type header: base list and `where` clauses often continue
                            // onto following lines before the body-opening `{` or primary-ctor `;`.
                            // Join them so consumers like ReferenceExtractor can resolve the base
                            // type from the stored signature instead of silently treating the class
                            // as having no base. Uses the comment-stripping variant so trailing or
                            // interleaved `//` / `/* */` comments do not leak into the signature.
                            // Closes #382.
                            // 折り返された C# 型ヘッダ: base リストや `where` 句は本体開きの `{`
                            // または primary-ctor 終端の `;` までに複数行へまたがることが多い。
                            // 継続行を連結して保存し、ReferenceExtractor などが保存済み
                            // シグネチャから base 型を解決できるようにする。末尾や途中に混じる
                            // `//` / `/* */` コメントを signature から除去する variant を使う。
                            // Closes #382.
                            signature = BuildCSharpTypeHeaderSignature(
                                lines,
                                i,
                                csharpSignatureRawStartColumn,
                                csharpTypeHeaderLastLineIndex,
                                csharpTypeHeaderLastLineExclusiveEndColumn);
                        }
                        else if (lang == "csharp"
                            && pattern.Kind is "event" or "delegate"
                            && pattern.BodyStyle == BodyStyle.None)
                        {
                            // Same-line C# semicolon-style declarations such as
                            // `event EventHandler E; }` or `delegate void D(); }` must stop at the
                            // declaration terminator instead of absorbing the enclosing type's
                            // closing brace into the stored signature. Reuse the same statement-end
                            // scanner as plain fields so nested `{}` inside accessor-style events
                            // still stay balanced while the outer `}` remains excluded.
                            // Closes #473 follow-up.
                            // `event EventHandler E; }` や `delegate void D(); }` のような
                            // 同一行 C# のセミコロン終端宣言は、囲む型本体の `}` を signature に
                            // 含めてはならない。plain field と同じ statement-end scanner を再利用し、
                            // アクセサ式 event 内部の `{}` は釣り合いを保ったまま、外側 `}` だけを
                            // 除外する。Closes #473 follow-up.
                            var statementEnd = FindCSharpSameLineStatementEnd(patternMatchLine, absoluteStartColumn);
                            if (statementEnd > line.Length)
                                statementEnd = line.Length;
                            if (statementEnd <= absoluteStartColumn)
                                statementEnd = Math.Min(absoluteStartColumn + Math.Max(1, match.Length), line.Length);
                            signature = line[absoluteStartColumn..statementEnd].Trim();
                        }
                        else if (lang == "java"
                            && pattern.BodyStyle == BodyStyle.Brace
                            && bodyStartLine == null)
                        {
                            var statementEnd = FindJavaSameLineStatementEnd(line, absoluteStartColumn);
                            if (statementEnd > line.Length)
                                statementEnd = line.Length;
                            if (statementEnd <= absoluteStartColumn)
                                statementEnd = Math.Min(absoluteStartColumn + Math.Max(1, match.Length), line.Length);
                            signature = line[absoluteStartColumn..statementEnd].Trim();
                        }
                        else if (lang == "csharp"
                            && pattern.Kind == "property"
                            && pattern.BodyStyle == BodyStyle.None)
                        {
                            // For a plain C# field (kind `property`, BodyStyle.None), clamp the
                            // signature to the end of the field's declaration statement (the
                            // terminating `;`, or — if an unbalanced `}` from a same-line
                            // enclosing type body is hit first — the position of that `}`).
                            // This keeps initializer-backed fields such as
                            // `private int _x = 42;` carrying a full `private int _x = 42;`
                            // signature instead of being truncated at `=`, and still prevents
                            // `public int X; } }` inside a same-line nested type from leaking
                            // the trailing `} }` into X's signature (which would break the
                            // same-line `ContainsSymbol` check in `AssignContainers` and make
                            // X attach to `Outer` instead of `Inner`). Closes #400.
                            // C# の通常フィールド（kind `property`、BodyStyle.None）では、signature を
                            // 宣言文の終端（`;` まで、または同一行の囲む型本体の閉じ `}` が先に
                            // 来ればその位置）までで clamp する。`private int _x = 42;` のような
                            // 初期化子付きフィールドでも signature が `=` で切れず完全に残り、かつ
                            // `public int X; } }` のような同一行ネスト型内のフィールドでも
                            // trailing `} }` が signature に混入せず、AssignContainers の
                            // ContainsSymbol 判定が正しく動いて X が Inner ではなく Outer に
                            // ぶら下がる事故が起きない。Closes #400.
                            var statementEnd = FindCSharpSameLineStatementEnd(patternMatchLine, absoluteStartColumn);
                            if (csharpMatchLines != null
                                && ReferenceEquals(patternMatchLine, csharpMatchLines[i]))
                            {
                                // Single-line candidate: translate both endpoints through the
                                // per-line collapsed→raw column map so the raw slice keeps the
                                // `;` terminator and does not absorb a phantom leading `;` from
                                // the next declarator on the same line. Without this, a line like
                                // `public Dictionary<string, int> Map = new(); public int B;`
                                // returned `Map` without `;` and `B` with a leading `;` because
                                // the collapsed-space endpoints no longer lined up with raw
                                // character positions. Closes #400.
                                // 単一行候補では、per-line collapsed→raw map で両端点を raw 列に
                                // 戻してから slice する。こうしないと、
                                // `public Dictionary<string, int> Map = new(); public int B;` のような行で
                                // `Map` の終端 `;` が欠け、後続の `B` の先頭に `;` が混入する。Closes #400.
                                var rawStart = TranslateCSharpCollapsedColumnToRaw(
                                    csharpMatchColumnToRaw,
                                    i,
                                    absoluteStartColumn,
                                    line.Length);
                                var rawEnd = TranslateCSharpCollapsedColumnToRaw(
                                    csharpMatchColumnToRaw,
                                    i,
                                    statementEnd,
                                    line.Length);
                                if (rawEnd > line.Length)
                                    rawEnd = line.Length;
                                if (rawStart > line.Length)
                                    rawStart = line.Length;
                                if (rawEnd <= rawStart)
                                    rawEnd = Math.Min(rawStart + Math.Max(1, match.Length), line.Length);
                                signature = line[rawStart..rawEnd].Trim();
                            }
                            else
                            {
                                if (statementEnd > line.Length)
                                    statementEnd = line.Length;
                                if (statementEnd <= absoluteStartColumn)
                                    statementEnd = Math.Min(absoluteStartColumn + Math.Max(1, match.Length), line.Length);
                                signature = line[absoluteStartColumn..statementEnd].Trim();
                            }
                        }
                        else
                        {
                            signature = lang == "fortran"
                                ? patternMatchLine[absoluteStartColumn..].Trim()
                                : line[absoluteStartColumn..].Trim();
                        }
                        if (lang == "python" && pattern.Kind is "function" or "class")
                            signature = BuildPythonLogicalHeaderSignature(lines, i, absoluteStartColumn);

                        if (kind == "function"
                            && lang == "csharp"
                            && pattern.BodyStyle == BodyStyle.None
                            && IsCSharpConstOrStaticReadonlyField(signature))
                        {
                            kind = "field";
                        }

                        if (lang == "csharp"
                            && pattern.BodyStyle == BodyStyle.None
                            && (pattern.Kind == "property" || kind == "field"))
                        {
                            signature = BoundCSharpFieldInitializerSignature(signature);
                        }

                        List<string>? fortranProcedureNames = null;
                        if (lang == "fortran"
                            && pattern.Kind == "function"
                            && name.Contains(',')
                            && signature.Contains("procedure", StringComparison.OrdinalIgnoreCase))
                        {
                            var names = name.AsSpan();
                            var nameStart = 0;
                            while (nameStart <= names.Length)
                            {
                                var separator = names[nameStart..].IndexOf(',');
                                var nameEnd = separator >= 0 ? nameStart + separator : names.Length;
                                var candidate = names[nameStart..nameEnd].Trim();
                                if (candidate.Length > 0)
                                {
                                    fortranProcedureNames ??= new List<string>();
                                    fortranProcedureNames.Add(candidate.ToString());
                                }

                                if (separator < 0)
                                    break;
                                nameStart = nameEnd + 1;
                            }
                        }

                        if (lang == "cpp"
                            && IsCppTemplateSpecializationSymbol(kind, name, signature, lines, i))
                        {
                            kind = "specialization";
                        }

                        var suppressJavaStatementSymbol = false;
                        if (lang == "java" && pattern.Kind == "function")
                        {
                            var trimmedSignature = signature.TrimStart();
                            suppressJavaStatementSymbol = name == "switch"
                                || trimmedSignature.StartsWith("return ", StringComparison.Ordinal)
                                || trimmedSignature.StartsWith("switch ", StringComparison.Ordinal)
                                || trimmedSignature.StartsWith("case ", StringComparison.Ordinal);
                        }

                        if (!suppressJavaStatementSymbol)
                        {
                            if (lang == "csharp"
                                && pattern.Kind == "function"
                                && IsCSharpTestMethod(lines, i))
                            {
                                kind = "test.method";
                            }

                            var pythonImportEntries = lang == "python" && pattern.Kind == "import"
                                ? TryExpandPythonImportSymbols(lines, i, absoluteStartColumn, pythonModulePrefix)
                                : null;
                            var declaratorEntries = lang == "csharp"
                                && pattern.Kind == "property"
                                && pattern.BodyStyle == BodyStyle.None
                                ? TryExpandCSharpFieldDeclaratorList(patternMatchLine, absoluteStartColumn, match, pattern.ReturnTypeGroup, name)
                                : null;
                            var swiftEnumCaseEntries = lang == "swift"
                                && pattern.Kind == "property"
                                && pattern.BodyStyle == BodyStyle.None
                                ? TryExpandSwiftEnumCaseDeclaratorList(patternMatchLine, absoluteStartColumn, match)
                                : null;
                            var fortranEnumeratorEntries = lang == "fortran"
                                && pattern.Kind == "property"
                                && pattern.BodyStyle == BodyStyle.None
                                ? TryExpandFortranEnumeratorDeclaratorList(patternMatchLine, match)
                                : null;
                            var fortranParameterEntries = lang == "fortran"
                                && pattern.Kind == "property"
                                && pattern.BodyStyle == BodyStyle.None
                                ? TryExpandFortranParameterDeclaratorList(patternMatchLine, match)
                                : null;
                            var csharpMetadataTarget = TryClassifyCSharpExtractorMetadataTarget(lang, pattern.Kind, signature);

                            if (pythonImportEntries != null)
                            {
                                foreach (var entry in pythonImportEntries)
                                {
                                    AddSymbolRecord(
                                        symbols,
                                        extractionState,
                                        cssSeenSymbols,
                                        startLine,
                                        new SymbolRecord
                                        {
                                            FileId = fileId,
                                            Kind = kind,
                                            Name = entry.Name,
                                            Line = startLine,
                                            StartLine = startLine,
                                            StartColumn = entry.StartColumn,
                                            EndLine = Math.Max(startLine, endLine),
                                            BodyStartLine = bodyStartLine,
                                            BodyEndLine = bodyEndLine,
                                            Signature = signature,
                                            Visibility = TryGetGroup(match, pattern.VisibilityGroup),
                                            ReturnType = NormalizeMetadata(rawReturnType),
                                        },
                                        line);
                                }
                            }
                            else if (declaratorEntries != null)
                            {
                                foreach (var entry in declaratorEntries)
                                {
                                    AddSymbolRecord(
                                        symbols,
                                        extractionState,
                                        cssSeenSymbols,
                                        startLine,
                                        new SymbolRecord
                                        {
                                            FileId = fileId,
                                            Kind = kind,
                                            Name = entry.Name,
                                            Line = startLine,
                                            StartLine = startLine,
                                            StartColumn = csharpSingleLineCollapsedMatch
                                                ? csharpSignatureRawStartColumn
                                                : absoluteStartColumn,
                                            EndLine = Math.Max(startLine, endLine),
                                            BodyStartLine = bodyStartLine,
                                            BodyEndLine = bodyEndLine,
                                            Signature = signature,
                                            Visibility = TryGetGroup(match, pattern.VisibilityGroup),
                                            ReturnType = NormalizeMetadata(entry.ReturnType),
                                        },
                                        line);
                                }
                            }
                            else if (swiftEnumCaseEntries != null)
                            {
                                foreach (var entry in swiftEnumCaseEntries)
                                {
                                    AddSymbolRecord(
                                        symbols,
                                        extractionState,
                                        cssSeenSymbols,
                                        startLine,
                                        new SymbolRecord
                                        {
                                            FileId = fileId,
                                            Kind = kind,
                                            Name = entry.Name,
                                            Line = startLine,
                                            StartLine = startLine,
                                            StartColumn = entry.StartColumn,
                                            EndLine = Math.Max(startLine, endLine),
                                            BodyStartLine = bodyStartLine,
                                            BodyEndLine = bodyEndLine,
                                            Signature = signature,
                                            Visibility = TryGetGroup(match, pattern.VisibilityGroup),
                                            ReturnType = NormalizeMetadata(entry.ReturnType),
                                        },
                                        line);
                                }
                            }
                            else if (fortranEnumeratorEntries != null)
                            {
                                foreach (var entry in fortranEnumeratorEntries)
                                {
                                    AddSymbolRecord(
                                        symbols,
                                        extractionState,
                                        cssSeenSymbols,
                                        startLine,
                                        new SymbolRecord
                                        {
                                            FileId = fileId,
                                            Kind = kind,
                                            Name = entry.Name,
                                            Line = startLine,
                                            StartLine = startLine,
                                            StartColumn = entry.StartColumn,
                                            EndLine = Math.Max(startLine, endLine),
                                            BodyStartLine = bodyStartLine,
                                            BodyEndLine = bodyEndLine,
                                            Signature = signature,
                                            Visibility = TryGetGroup(match, pattern.VisibilityGroup),
                                            ReturnType = NormalizeMetadata(rawReturnType),
                                        },
                                        line);
                                }
                            }
                            else if (fortranParameterEntries != null)
                            {
                                foreach (var entry in fortranParameterEntries)
                                {
                                    AddSymbolRecord(
                                        symbols,
                                        extractionState,
                                        cssSeenSymbols,
                                        startLine,
                                        new SymbolRecord
                                        {
                                            FileId = fileId,
                                            Kind = kind,
                                            Name = entry.Name,
                                            Line = startLine,
                                            StartLine = startLine,
                                            StartColumn = entry.StartColumn,
                                            EndLine = Math.Max(startLine, endLine),
                                            BodyStartLine = bodyStartLine,
                                            BodyEndLine = bodyEndLine,
                                            Signature = signature,
                                            Visibility = TryGetGroup(match, pattern.VisibilityGroup),
                                            ReturnType = NormalizeMetadata(rawReturnType),
                                        },
                                        line);
                                }
                            }
                            else if (fortranProcedureNames != null)
                            {
                                foreach (var procedureName in fortranProcedureNames)
                                {
                                    AddSymbolRecord(
                                        symbols,
                                        extractionState,
                                        cssSeenSymbols,
                                        startLine,
                                        new SymbolRecord
                                        {
                                            FileId = fileId,
                                            Kind = kind,
                                            Name = procedureName,
                                            Line = startLine,
                                            StartLine = startLine,
                                            StartColumn = csharpSingleLineCollapsedMatch
                                                ? csharpSignatureRawStartColumn
                                                : absoluteStartColumn,
                                            EndLine = Math.Max(startLine, endLine),
                                            BodyStartLine = bodyStartLine,
                                            BodyEndLine = bodyEndLine,
                                            Signature = signature,
                                            Visibility = TryGetGroup(match, pattern.VisibilityGroup),
                                            ReturnType = NormalizeMetadata(rawReturnType),
                                        },
                                        line);
                                }
                            }
                            else if (rubyAttrNames != null)
                            {
                                var rubyAttrSearchStart = absoluteStartColumn;
                                foreach (var rubyAttrName in rubyAttrNames)
                                {
                                    var rubyAttrStartColumn = rubyAttrSearchStart;
                                    if (!string.Equals(rubyAttrName, name, StringComparison.Ordinal))
                                    {
                                        var foundRubyAttrStart = patternMatchLine.IndexOf(rubyAttrName, rubyAttrSearchStart, StringComparison.Ordinal);
                                        if (foundRubyAttrStart >= 0)
                                            rubyAttrStartColumn = foundRubyAttrStart;
                                    }

                                    AddSymbolRecord(
                                        symbols,
                                        extractionState,
                                        cssSeenSymbols,
                                        startLine,
                                        new SymbolRecord
                                        {
                                            FileId = fileId,
                                            Kind = kind,
                                            Name = rubyAttrName,
                                            Line = startLine,
                                            StartLine = startLine,
                                            StartColumn = rubyAttrStartColumn,
                                            EndLine = Math.Max(startLine, endLine),
                                            BodyStartLine = bodyStartLine,
                                            BodyEndLine = bodyEndLine,
                                            Signature = signature,
                                            Visibility = TryGetGroup(match, pattern.VisibilityGroup),
                                            ReturnType = NormalizeMetadata(rawReturnType),
                                        },
                                        line);

                                    rubyAttrSearchStart = rubyAttrStartColumn + Math.Max(1, rubyAttrName.Length);
                                }
                            }
                            else
                            {
                                AddSymbolRecord(
                                    symbols,
                                    extractionState,
                                    cssSeenSymbols,
                                    startLine,
                                        new SymbolRecord
                                        {
                                            FileId = fileId,
                                            Kind = kind,
                                            Name = name,
                                            Line = startLine,
                                            StartLine = startLine,
                                            StartColumn = lang == "rust" && pattern.Kind == "function"
                                                ? match.Groups["name"].Index
                                                : (csharpSingleLineCollapsedMatch
                                                    ? csharpSignatureRawStartColumn
                                                    : absoluteStartColumn),
                                            EndLine = Math.Max(startLine, endLine),
                                            BodyStartLine = bodyStartLine,
                                            BodyEndLine = bodyEndLine,
                                            Signature = signature,
                                            FamilyKey = lang == "cpp" && kind == "specialization" ? name : null,
                                            SubKind = pythonSubKind ?? ResolveLanguageSubKind(lang, kind, signature, patternMatchLine),
                                            Visibility = TryGetGroup(match, pattern.VisibilityGroup),
                                            ReturnType = NormalizeMetadata(rawReturnType),
                                            IsMetadataTarget = csharpMetadataTarget,
                                            MetadataTargetSource = csharpMetadataTarget == true
                                                ? SymbolRecord.MetadataTargetSourceExtractor
                                                : null,
                                        },
                                    line);

                                if (dockerfileStageNames != null && kind == "stage")
                                    dockerfileStageNames.Add(name);

                                if (lang == "objc"
                                    && pattern.Kind == "class"
                                    && TryGetObjCCategoryDisplayName(patternMatchLine[absoluteStartColumn..], name, out var categoryDisplayName))
                                {
                                    AddSymbolRecord(
                                        symbols,
                                        extractionState,
                                        cssSeenSymbols,
                                        startLine,
                                        new SymbolRecord
                                        {
                                            FileId = fileId,
                                            Kind = "class",
                                            Name = categoryDisplayName,
                                            Line = startLine,
                                            StartLine = startLine,
                                            StartColumn = csharpSingleLineCollapsedMatch
                                                ? csharpSignatureRawStartColumn
                                                : absoluteStartColumn,
                                            EndLine = Math.Max(startLine, endLine),
                                            BodyStartLine = bodyStartLine,
                                            BodyEndLine = bodyEndLine,
                                            Signature = signature,
                                            Visibility = TryGetGroup(match, pattern.VisibilityGroup),
                                            ReturnType = NormalizeMetadata(rawReturnType),
                                        },
                                        line);
                                }
                            }
                        }

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
                                csharpSuppressedContinuationUntil = Math.Max(
                                    csharpSuppressedContinuationUntil,
                                    expressionEndLineIndex - 1);
                                csharpSuppressedContinuationResumeLine = expressionEndLineIndex;
                                csharpSuppressedContinuationResumeRawColumn =
                                    csharpPropertyCandidate.ExpressionBodyEndLineExclusiveEndColumn.Value;
                            }
                            else
                            {
                                csharpSuppressedContinuationUntil = Math.Max(
                                    csharpSuppressedContinuationUntil,
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

                        // C# plain-field (kind `property`, BodyStyle.None) matches need their own
                        // advance path. The generic `sameLineEndColumn`-based advance below resolves
                        // to -1 for BodyStyle.None and would set `stopAfterFirstPatternMatch`, which
                        // prevents structural siblings on the same line (e.g. the enclosing
                        // `public class C` in `public class C { public int X; }`) from being
                        // captured by later patterns. Instead, advance past the field terminator
                        // and continue the same-pattern scan so multiple same-line fields are
                        // still collected, and skip the stop flag so later patterns can still run.
                        // Closes #400.
                        // C# 通常フィールド（kind `property`、BodyStyle.None）は専用の前進経路を使う。
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
                    foreach (Match match in CssInlineCustomPropertyRegex.Matches(cssScannerLine))
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

        if (lang == "javascript")
            ExtractJavaScriptBareMethods(fileId, lines, symbols, getPrivateScopeColumns!, GetJavaScriptTypeScriptSanitizedLines);
        else if (lang == "typescript")
            ExtractTypeScriptBareMethods(fileId, lines, symbols, getPrivateScopeColumns!, GetJavaScriptTypeScriptSanitizedLines);
        else if (lang == "csharp")
            ExtractCSharpEnumMembers(fileId, lines, structuralLines, csharpMatchLines!, symbols);
        else if (lang == "java")
        {
            ExtractJavaEnumMembers(fileId, lines, symbols);
            ExtractJavaCompactConstructors(fileId, lines, symbols);
            ExtractJavaModuleDirectiveSymbols(fileId, lines, structuralLines, symbols, extractionState);
        }
        else if (lang == "vb")
            ExtractVisualBasicEnumMembers(fileId, lines, symbols);
        if (lang == "cobol")
            ExtractCobolParagraphSymbols(fileId, lines, symbols, extractionState);

        if (string.Equals(originalLang, "svelte", StringComparison.Ordinal))
            ExtractSvelteReactiveSymbols(fileId, lines, symbols);
        if (lang == "rust")
            ExtractRustUseSymbols(fileId, lines, symbols, extractionState);
        if (lang == "rust")
            ExtractRustMultilineImplSymbols(fileId, lines, symbols, extractionState);
        if (lang == "rust")
            ExtractRustAssociatedTypeDefaultSymbols(fileId, lines, structuralLines, symbols);
        if (lang == "go")
            ExtractGoGroupedDeclarations(fileId, lines, symbols, extractionState);
        if (lang == "cpp")
            ExtractCppSameLineClassBodyMembers(fileId, lines, symbols);
        if (lang == "cpp")
            ExtractCppBalancedCallableSymbols(fileId, lines, structuralLines, symbols, extractionState);
        if (lang == "cpp")
            ExtractCppFriendDeclarationSymbols(fileId, lines, symbols, extractionState);
        if (lang is "verilog" or "systemverilog")
            ExtractHdlInlineParameterSymbols(fileId, lines, symbols, extractionState);
        if (string.Equals(NormalizePluginLanguage(originalLang), "cuda", StringComparison.Ordinal))
            ClassifyCudaFunctionSubKinds(symbols);
        if (lang == "python")
            ExtractPythonAllExportSymbols(fileId, lines, symbols, pythonModulePrefix);
        if (lang == "python")
            ExtractPythonClassAttributeSymbols(fileId, lines, symbols);
        if (lang == "python")
            ExtractPythonWalrusSymbols(fileId, lines, symbols);
        if (lang == "perl")
            ExtractPerlHashConstantSymbols(fileId, lines, symbols, extractionState);
        if (lang == "php")
            ExtractPhpAdditionalPropertySymbols(fileId, lines, symbols);
        if (lang == "php")
            ExtractPhpPromotedConstructorProperties(fileId, lines, symbols);
        if (lang == "php")
            ExtractPhpDocblockMethodSymbols(fileId, lines, symbols);
        if (lang == "php")
            ExtractPhpDocblockPropertySymbols(fileId, lines, symbols);
        if (lang == "php")
            ExtractPhpTraitAliasSymbols(fileId, lines, symbols);
        if (lang == "php")
            ExtractPhpDocblockTypeAliasSymbols(fileId, lines, symbols);
        if (lang == "php")
            ExtractPhpDocblockImportTypeSymbols(fileId, lines, symbols);
        if (lang == "php")
            ExtractPhpPropertyHookSupplementalSymbols(fileId, lines, structuralLines, symbols);
        if (lang == "swift")
            ExtractSwiftPropertySupplementalSymbols(fileId, lines, structuralLines, symbols);
        if (lang == "sql")
        {
            var sqlSyntheticSymbolLines = MaskSqlSyntheticSymbolLines(lines);
            ExtractSqlCteSymbols(fileId, content, lines, symbols, extractionState);
            ExtractSqlDefinerSymbols(fileId, lines, sqlSyntheticSymbolLines, symbols, extractionState);
            ExtractSqlRoutineResultColumnSymbols(fileId, lines, sqlSyntheticSymbolLines, symbols, extractionState);
            ExtractSqlGeneratedColumnSymbols(fileId, lines, sqlSyntheticSymbolLines, symbols, extractionState);
        }
        if (lang == "graphql")
            ExtractGraphQLMemberSymbols(fileId, content, lines, symbols);
        if (lang is "csharp" or "python" or "javascript" or "typescript")
            ExtractSectionHeadingSymbols(fileId, lang, lines, symbols);
        if (IsRazorLanguage(originalLang) || IsRazorFilePath(filePath))
            ExtractRazorDirectiveSymbols(fileId, lines, symbols);
        AssignContainers(symbols, lines, getCSharpLineStartStates);
        if (lang is "shell" or "powershell")
            AddScriptScopeSymbol(fileId, lines, symbols);
        if (lang == "csharp")
            NormalizeCSharpImplicitPartialConstructorReturnTypes(symbols);
        if (lang == "go")
            AssignGoMethodReceiverContainers(symbols);
        if (lang == "go")
            ClassifyGoFunctionRoles(symbols, filePath);
        MaterializeRecordPrimaryComponentSymbols(symbols, pendingRecordPrimaryComponents);
        if (lang is "javascript" or "typescript")
            ClassifyJavaScriptTypeScriptReactHooks(symbols);
        if (lang == "scala")
            ClassifyScalaCompanions(symbols);
        KotlinSymbolNameNormalizer.NormalizeSecondaryConstructorNames(symbols);
        if (lang == "shell")
            ExpandShellAliasSymbols(fileId, lines, symbols, extractionState);
        PopulateDeclaredContainerQualifiedNames(symbols);
        return symbols;
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
        // signatures are collapsed to one line. Keep a deterministic declaration prefix and an
        // explicit terminator while bounding the value persisted in the symbol database. #4445
        // field signature は body ではなくメタデータである。複数行を1行へ畳み込んだ巨大な
        // object/collection initializer が CLI / JSON / MCP / LSP の応答予算を使い切らないよう、
        // DB に保存する値を制限しつつ宣言prefixと明示的な終端を維持する。#4445
        return string.Concat(signature.AsSpan(0, CSharpFieldInitializerSignatureLimit - 2), "…;");
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
