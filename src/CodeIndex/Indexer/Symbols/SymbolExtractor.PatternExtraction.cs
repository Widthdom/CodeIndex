using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private readonly record struct PatternExtractionPreparation(
        PatternExtractionContext? Context,
        List<SymbolRecord>? ImmediateSymbols);

    private sealed class PatternExtractionContext
    {
        public PatternExtractionContext(
            long fileId,
            string? originalLang,
            string lang,
            string content,
            string? filePath,
            string? projectRoot,
            string[] lines,
            IReadOnlyList<SymbolPattern> applicablePatterns,
            bool applyRequiredLiteralMatchInputGate,
            RequiredLiteralGateCounts? requiredLiteralGateCounts,
            bool applyCSharpRegexProbeOptimizations,
            CSharpRegexProbeCounts? csharpRegexProbeCounts,
            CancellationToken cancellationToken,
            int? maxSymbols)
        {
            FileId = fileId;
            OriginalLang = originalLang;
            Lang = lang;
            Content = content;
            FilePath = filePath;
            ProjectRoot = projectRoot;
            Lines = lines;
            ApplicablePatterns = applicablePatterns;
            ApplyRequiredLiteralMatchInputGate = applyRequiredLiteralMatchInputGate;
            RequiredLiteralGateCounts = requiredLiteralGateCounts;
            ApplyCSharpRegexProbeOptimizations = applyCSharpRegexProbeOptimizations;
            CSharpRegexProbeCounts = csharpRegexProbeCounts;
            CancellationToken = cancellationToken;

            ScanInputs = new PatternScanInputs(
                lang,
                filePath,
                lines,
                applicablePatterns,
                applyRequiredLiteralMatchInputGate,
                requiredLiteralGateCounts,
                applyCSharpRegexProbeOptimizations,
                csharpRegexProbeCounts);
            GetJavaScriptTypeScriptSanitizedLines = ScanInputs.GetJavaScriptTypeScriptSanitizedLines;
            GetDartInsideClassBody = ScanInputs.GetDartInsideClassBody;
            GetCSharpInsideTypeBody = ScanInputs.GetCSharpInsideTypeBody;
            GetCSharpCallableParameterScope = ScanInputs.GetCSharpCallableParameterScope;
            GetCSharpDeclarationStartScope = ScanInputs.GetCSharpDeclarationStartScope;
            Symbols = new SymbolExtractionList(
                EstimateSymbolListInitialCapacity(lines.Length),
                maxSymbols);
            ExtractionState = Symbols.ExtractionState;
            ScanState = new PatternScanState();
            CssSeenSymbols = lang == "css"
                ? new HashSet<SymbolLineIdentity>()
                : null;
            DockerfileStageNames = lang == "dockerfile"
                ? new HashSet<string>(StringComparer.Ordinal)
                : null;
        }

        public long FileId { get; }
        public string? OriginalLang { get; }
        public string Lang { get; }
        public string Content { get; }
        public string? FilePath { get; }
        public string? ProjectRoot { get; }
        public string[] Lines { get; }
        public IReadOnlyList<SymbolPattern> ApplicablePatterns { get; }
        public bool ApplyRequiredLiteralMatchInputGate { get; }
        public RequiredLiteralGateCounts? RequiredLiteralGateCounts { get; }
        public bool ApplyCSharpRegexProbeOptimizations { get; }
        public CSharpRegexProbeCounts? CSharpRegexProbeCounts { get; }
        public CancellationToken CancellationToken { get; }
        public PatternScanInputs ScanInputs { get; }
        public Func<string[]> GetJavaScriptTypeScriptSanitizedLines { get; }
        public Func<DartClassBodyScope> GetDartInsideClassBody { get; }
        public Func<CSharpTypeBodyScope> GetCSharpInsideTypeBody { get; }
        public Func<CSharpCallableParameterScope> GetCSharpCallableParameterScope { get; }
        public Func<CSharpDeclarationStartScope> GetCSharpDeclarationStartScope { get; }
        public SymbolExtractionList Symbols { get; }
        public SymbolExtractionState ExtractionState { get; }
        public PatternScanState ScanState;
        public HashSet<SymbolLineIdentity>? CssSeenSymbols { get; }
        public HashSet<string>? DockerfileStageNames { get; }
        public List<PendingRecordPrimaryComponents>? PendingRecordPrimaryComponents;
        public RecordPrimaryComponentParentIndex? RecordPrimaryComponentParentIndex;
    }

    private static PatternExtractionPreparation PreparePatternExtraction(
        long fileId,
        string? originalLang,
        string? lang,
        string content,
        string? filePath,
        string? projectRoot,
        CancellationToken cancellationToken,
        int? maxSymbols,
        bool applyRequiredLiteralFileGate,
        bool applyRequiredLiteralMatchInputGate,
        RequiredLiteralGateCounts? requiredLiteralGateCounts,
        bool applyCSharpRegexProbeOptimizations,
        CSharpRegexProbeCounts? csharpRegexProbeCounts)
    {
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
            return new(null, []);

        var lines = SplitContentLines(content);
        cancellationToken.ThrowIfCancellationRequested();
        if (TryExtractDedicatedLineBasedSymbols(
            fileId,
            lang,
            content,
            lines,
            out var dedicatedSymbols))
        {
            return new(null, dedicatedSymbols);
        }

        if (patterns == null || lang == null)
            return new(null, []);

        var applicablePatterns = SelectApplicablePatterns(
            patterns,
            content,
            applyRequiredLiteralFileGate);
        if (requiredLiteralGateCounts != null)
        {
            requiredLiteralGateCounts.PatternCount = patterns.Count;
            requiredLiteralGateCounts.ApplicablePatternCount = applicablePatterns.Count;
        }

        return new(
            new PatternExtractionContext(
                fileId,
                originalLang,
                lang,
                content,
                filePath,
                projectRoot,
                lines,
                applicablePatterns,
                applyRequiredLiteralMatchInputGate,
                requiredLiteralGateCounts,
                applyCSharpRegexProbeOptimizations,
                csharpRegexProbeCounts,
                cancellationToken,
                maxSymbols),
            null);
    }

    private static bool TryExtractDedicatedLineBasedSymbols(
        long fileId,
        string? lang,
        string content,
        string[] lines,
        out List<SymbolRecord> symbols)
    {
        if (lang is "commonlisp" or "racket")
        {
            symbols = ExtractLispSymbols(fileId, lang, lines);
            return true;
        }

        if (lang == "solidity")
        {
            symbols = ExtractSoliditySymbols(fileId, lines);
            return true;
        }

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
        {
            symbols = ExtractHtmlSymbols(fileId, content, lines);
            return true;
        }

        if (lang == "assembly")
        {
            symbols = ExtractAssemblySymbols(fileId, lines);
            return true;
        }

        symbols = null!;
        return false;
    }

    private static void CompletePatternExtraction(PatternExtractionContext context)
    {
        var fileId = context.FileId;
        var originalLang = context.OriginalLang;
        var lang = context.Lang;
        var content = context.Content;
        var filePath = context.FilePath;
        var projectRoot = context.ProjectRoot;
        var lines = context.Lines;
        var applicablePatterns = context.ApplicablePatterns;
        var applyRequiredLiteralMatchInputGate = context.ApplyRequiredLiteralMatchInputGate;
        var requiredLiteralGateCounts = context.RequiredLiteralGateCounts;
        var scanInputs = context.ScanInputs;
        var structuralLines = scanInputs.StructuralLines;
        var pythonModulePrefix = scanInputs.PythonModulePrefix;
        var prologMultilineHeads = scanInputs.PrologMultilineHeads;
        var csharpMatchLines = scanInputs.CSharpMatchLines;
        var getCSharpLineStartStates = scanInputs.GetCSharpLineStartStates;
        var getPrivateScopeColumns = scanInputs.GetPrivateScopeColumns;
        var GetJavaScriptTypeScriptSanitizedLines = context.GetJavaScriptTypeScriptSanitizedLines;
        var symbols = context.Symbols;
        var extractionState = context.ExtractionState;
        var pendingRecordPrimaryComponents = context.PendingRecordPrimaryComponents;
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
                prologMultilineHeads,
                applicablePatterns,
                applyRequiredLiteralMatchInputGate,
                requiredLiteralGateCounts);
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
            projectRoot,
            lines,
            symbols,
            extractionState,
            getCSharpLineStartStates,
            pendingRecordPrimaryComponents);
    }
}
