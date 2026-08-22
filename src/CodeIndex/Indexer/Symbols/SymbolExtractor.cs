using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

/// <summary>
/// Extracts symbols (functions, classes, imports) using regex patterns.
/// 正規表現を使ってシンボル（関数、クラス、インポート）を抽出する。
/// </summary>
public static partial class SymbolExtractor
{
    private const int SymbolListInitialCapacityLineThreshold = 128;
    private const int SymbolListInitialCapacityMax = 1024;

    private static string[] SplitContentLines(string content) =>
        SourceLineSplitter.Split(content);

    private static List<SymbolRecord> CreateSymbolListForLines(int lineCount)
    {
        var initialCapacity = EstimateSymbolListInitialCapacity(lineCount);
        return initialCapacity == 0
            ? []
            : new List<SymbolRecord>(initialCapacity);
    }

    private static int EstimateSymbolListInitialCapacity(int lineCount)
    {
        if (lineCount < SymbolListInitialCapacityLineThreshold)
            return 0;

        return Math.Min(SymbolListInitialCapacityMax, Math.Max(16, lineCount / 8));
    }

    private static IReadOnlyList<SymbolRecord> BuildEnumDeclarationSnapshot(IReadOnlyList<SymbolRecord> symbols, long? fileId = null)
    {
        List<(SymbolRecord Symbol, int OriginalIndex)>? candidates = null;
        for (var index = 0; index < symbols.Count; index++)
        {
            var symbol = symbols[index];
            if (fileId is { } requestedFileId && symbol.FileId != requestedFileId)
                continue;

            if (symbol.Kind == "enum" && symbol.BodyStartLine != null && symbol.BodyEndLine != null)
                (candidates ??= []).Add((symbol, index));
        }

        if (candidates is null)
            return Array.Empty<SymbolRecord>();

        if (candidates.Count == 1)
            return [candidates[0].Symbol];

        candidates.Sort(static (left, right) =>
        {
            var comparison = left.Symbol.StartLine.CompareTo(right.Symbol.StartLine);
            if (comparison != 0)
                return comparison;

            comparison = right.Symbol.EndLine.CompareTo(left.Symbol.EndLine);
            if (comparison != 0)
                return comparison;

            return left.OriginalIndex.CompareTo(right.OriginalIndex);
        });

        var snapshot = new SymbolRecord[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
            snapshot[i] = candidates[i].Symbol;
        return snapshot;
    }


    /// <summary>
    /// Return the set of languages that have symbol-extraction patterns.
    /// シンボル抽出パターンを持つ言語のセットを返す。
    /// </summary>
    public static IReadOnlyCollection<string> GetSupportedLanguages()
        => GetSupportedLanguages(workspaceRoot: null);

    internal static IReadOnlyCollection<string> GetSupportedLanguages(string? workspaceRoot)
    {
        var pluginLanguages = ExtractorPluginRegistry.GetSymbolLanguages(workspaceRoot);
        var capacity = BuiltInSymbolLanguages.Length + AdditionalSymbolLanguages.Length + pluginLanguages.Count;
        var languages = new List<string>(capacity);
        var seen = new HashSet<string>(capacity, StringComparer.Ordinal);

        AddSupportedLanguages(BuiltInSymbolLanguages, languages, seen);
        AddSupportedLanguages(AdditionalSymbolLanguages, languages, seen);
        AddSupportedLanguages(pluginLanguages, languages, seen);
        return languages.ToArray();
    }

    private static void AddSupportedLanguages(
        IEnumerable<string> candidates,
        List<string> languages,
        HashSet<string> seen)
    {
        foreach (var language in candidates)
        {
            if (seen.Add(language))
                languages.Add(language);
        }
    }

    private static string? NormalizeLanguage(string? lang)
    {
        if (lang is null)
            return null;

        var trimmed = lang.AsSpan().Trim();
        if (trimmed.IsEmpty)
            return null;

        if (trimmed.Equals("vue", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("svelte", StringComparison.OrdinalIgnoreCase))
        {
            return "typescript";
        }

        if (trimmed.Equals("razor", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("blazor", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("cshtml", StringComparison.OrdinalIgnoreCase))
        {
            return "csharp";
        }

        return trimmed.Equals("cuda", StringComparison.OrdinalIgnoreCase)
            ? "cpp"
            : NormalizeLanguageKey(lang, trimmed);
    }

    private static string? NormalizePluginLanguage(string? lang)
    {
        if (lang is null)
            return null;

        var trimmed = lang.AsSpan().Trim();
        return trimmed.IsEmpty ? null : NormalizeLanguageKey(lang, trimmed);
    }

    private static string NormalizeLanguageKey(string original, ReadOnlySpan<char> trimmed)
    {
        for (var i = 0; i < trimmed.Length; i++)
        {
            if (char.ToLowerInvariant(trimmed[i]) != trimmed[i])
                return trimmed.ToString().ToLowerInvariant();
        }

        return trimmed.Length == original.Length && trimmed.SequenceEqual(original.AsSpan())
            ? original
            : trimmed.ToString();
    }


    private static readonly HashSet<string> ContainerKinds =
    [
        "class", "struct", "interface", "protocol", "protocol_impl", "namespace", "enum", "object", "heading", "specialization", "class_hook"
    ];

    private static bool[] FindPowerShellEnumBodyLines(string[] structuralLines)
    {
        var result = new bool[structuralLines.Length];
        var waitingForOpeningBrace = false;
        var enumBraceDepth = 0;

        for (var i = 0; i < structuralLines.Length; i++)
        {
            var line = structuralLines[i];
            if (enumBraceDepth > 0)
                result[i] = true;

            if (enumBraceDepth == 0 && !waitingForOpeningBrace)
            {
                var trimmed = line.AsSpan().TrimStart();
                if (trimmed.StartsWith("enum", StringComparison.OrdinalIgnoreCase)
                    && trimmed.Length > 4
                    && char.IsWhiteSpace(trimmed[4]))
                {
                    waitingForOpeningBrace = true;
                }
            }

            if (!waitingForOpeningBrace && enumBraceDepth == 0)
                continue;

            foreach (var character in line)
            {
                if (character == '{')
                    enumBraceDepth++;
                else if (character == '}')
                    enumBraceDepth--;
            }

            if (enumBraceDepth > 0)
            {
                waitingForOpeningBrace = false;
            }
            else if (!waitingForOpeningBrace || line.Contains('}'))
            {
                enumBraceDepth = 0;
                waitingForOpeningBrace = false;
            }
        }

        return result;
    }

    private static bool IsRustDirectTraitBodyMember(List<SymbolRecord> symbols, int candidateLine)
    {
        SymbolRecord? innermostContainer = null;
        foreach (var symbol in symbols)
        {
            if (!symbol.BodyStartLine.HasValue || !symbol.BodyEndLine.HasValue)
                continue;
            if (candidateLine < symbol.BodyStartLine.Value || candidateLine > symbol.BodyEndLine.Value)
                continue;
            if (innermostContainer == null || symbol.StartLine >= innermostContainer.StartLine)
                innermostContainer = symbol;
        }

        return innermostContainer?.Kind == "protocol";
    }

    private static bool TryPrepareSymbolExtraction(
        long fileId,
        string? originalLang,
        string content,
        bool contentIsNormalized,
        bool? hasOversizeLine,
        int? conflictMarkerLine,
        string? filePath,
        string? projectRoot,
        bool patternConfigsAlreadyLoaded,
        CancellationToken cancellationToken,
        out string? lang,
        out string preparedContent,
        out List<SymbolRecord>? symbols)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lang = NormalizeLanguage(originalLang);
        var pluginLanguage = NormalizePluginLanguage(originalLang);
        preparedContent = content;
        symbols = null;

        if (lang == null && pluginLanguage == null)
        {
            symbols = [];
            return true;
        }

        // Null / empty fast path — keep the direct-call null-safe contract that
        // FileIndexer.StripLineLeadingInvisibles' IsNullOrEmpty check used to provide
        // before the CRLF normalization step was added in front of it. Closes #183.
        // null / 空入力は早期 return。CRLF 正規化を StripLineLeadingInvisibles の前に
        // 入れたことで helper 側の IsNullOrEmpty による null 許容が効かなくなる
        // ため、direct call の null セーフ契約をここで復元する。Closes #183.
        if (string.IsNullOrEmpty(content))
        {
            symbols = [];
            return true;
        }

        // Oversize-line skip: bail out for files that pack a multi-MB payload
        // into a single physical line (minified bundles, base64 blobs). The
        // matching guard in ChunkSplitter / ReferenceExtractor / ValidateContent
        // keeps the indexer from stalling on regex backtracking and surfaces
        // the skip as a `line_too_long` FileIssue. Closes #1542.
        // 1 行に複数 MB のペイロードを詰めたファイル (minified bundle や base64
        // ペイロード等) は早期に抜ける。ChunkSplitter / ReferenceExtractor /
        // ValidateContent の同等ガードと合わせて、正規表現のバックトラックで
        // インデクサが止まることを防ぎ、スキップは `line_too_long` FileIssue
        // として表面化させる。Closes #1542.
        if (hasOversizeLine ?? ChunkSplitter.HasOversizeLine(content))
        {
            symbols = [];
            return true;
        }

        if ((conflictMarkerLine ?? FileIndexer.GetConflictMarkerLine(content)) > 0)
        {
            symbols = [];
            return true;
        }

        if (!contentIsNormalized)
        {
            content = FileIndexer.NormalizeContentForPrepass(content);
        }
        preparedContent = content;
        cancellationToken.ThrowIfCancellationRequested();
        if (!patternConfigsAlreadyLoaded)
            ExtractorPluginRegistry.LoadPatternConfigsForProjectRoot(projectRoot);

        if (pluginLanguage != null
            && !PatternCache.ContainsKey(pluginLanguage)
            && ExtractorPluginRegistry.TryGetSymbolExtractor(pluginLanguage, projectRoot, out var pluginExtractor))
        {
            var pluginSymbols = pluginExtractor.Extract(
                    fileId,
                    preparedContent,
                    new ExtractionContext(pluginLanguage, filePath));
            symbols = CopyPluginSymbols(pluginSymbols);
            return true;
        }

        return false;
    }

    private static List<SymbolRecord> CopyPluginSymbols(IReadOnlyList<SymbolRecord> symbols)
    {
        var copiedSymbols = new List<SymbolRecord>(symbols.Count);
        for (var i = 0; i < symbols.Count; i++)
            copiedSymbols.Add(symbols[i]);
        return copiedSymbols;
    }

    /// <summary>
    /// Extract symbols from the given source content.
    /// 指定されたソース内容からシンボルを抽出する。
    /// </summary>
    /// <param name="fileId">The file ID in the database / データベース上のファイルID</param>
    /// <param name="lang">Detected language / 検出された言語</param>
    /// <param name="content">Full file content / ファイル全体の内容</param>
    /// <param name="filePath">Relative file path when available / 利用可能なら相対ファイルパス</param>
    /// <returns>List of extracted symbols / 抽出されたシンボルのリスト</returns>
    public static List<SymbolRecord> Extract(long fileId, string? lang, string content, string? filePath = null, string? projectRoot = null, CancellationToken cancellationToken = default)
        => ExtractCore(
            fileId,
            lang,
            content,
            contentIsNormalized: false,
            hasOversizeLine: null,
            conflictMarkerLine: null,
            filePath,
            projectRoot,
            patternConfigsAlreadyLoaded: false,
            cancellationToken: cancellationToken);

    private sealed class RequiredLiteralGateCounts
    {
        public int PatternCount { get; set; }
        public int ApplicablePatternCount { get; set; }
        public int RegexAttemptCount { get; set; }
        public int MatchInputLiteralSkipCount { get; set; }
    }

    private sealed class CSharpRegexProbeCounts
    {
        public int PropertyPrefixSuffixSkipCount { get; set; }
        public int PropertyHeaderRegexAttemptCount { get; set; }
        public int MethodHeaderRegexAttemptCount { get; set; }
        public int PlainFieldTerminatorSkipCount { get; set; }
        public int PlainFieldRegexAttemptCount { get; set; }
        public int RecoverablePlainFieldTerminatorSkipCount { get; set; }
        public int WrappedModifierLookupCount { get; set; }
        public int WrappedModifierAsciiShapeSkipCount { get; set; }
        public int WrappedModifierLineRegexAttemptCount { get; set; }
        public int WrappedModifierPrefixMaterializationCount { get; set; }
        public int WrappedModifierMatchInputMaterializationCount { get; set; }
        public int DeclarationPatternRegexAttemptCount { get; set; }
        public int PhysicalInputNegativePrefixCacheHitCount { get; set; }
    }

    internal readonly record struct CSharpRegexProbeMetrics(
        int PropertyPrefixSuffixSkipCount,
        int PropertyHeaderRegexAttemptCount,
        int MethodHeaderRegexAttemptCount,
        int PlainFieldTerminatorSkipCount,
        int PlainFieldRegexAttemptCount,
        int RecoverablePlainFieldTerminatorSkipCount,
        int WrappedModifierLookupCount,
        int WrappedModifierAsciiShapeSkipCount,
        int WrappedModifierLineRegexAttemptCount,
        int WrappedModifierPrefixMaterializationCount,
        int WrappedModifierMatchInputMaterializationCount,
        int DeclarationPatternRegexAttemptCount,
        int PhysicalInputNegativePrefixCacheHitCount);

    internal static List<SymbolRecord> ExtractForRequiredLiteralGateTesting(
        long fileId,
        string lang,
        string content,
        bool applyRequiredLiteralFileGate,
        bool applyRequiredLiteralMatchInputGate,
        out int patternCount,
        out int applicablePatternCount,
        out int regexAttemptCount,
        out int matchInputLiteralSkipCount,
        string? filePath = null,
        string? projectRoot = null,
        CancellationToken cancellationToken = default)
    {
        var counts = new RequiredLiteralGateCounts();
        var symbols = ExtractCore(
            fileId,
            lang,
            content,
            contentIsNormalized: false,
            hasOversizeLine: null,
            conflictMarkerLine: null,
            filePath,
            projectRoot,
            patternConfigsAlreadyLoaded: false,
            cancellationToken: cancellationToken,
            maxSymbols: null,
            applyRequiredLiteralFileGate: applyRequiredLiteralFileGate,
            applyRequiredLiteralMatchInputGate: applyRequiredLiteralMatchInputGate,
            requiredLiteralGateCounts: counts);
        patternCount = counts.PatternCount;
        applicablePatternCount = counts.ApplicablePatternCount;
        regexAttemptCount = counts.RegexAttemptCount;
        matchInputLiteralSkipCount = counts.MatchInputLiteralSkipCount;
        return symbols;
    }

    internal static List<SymbolRecord> ExtractForCSharpRegexProbeTesting(
        long fileId,
        string content,
        bool applyCSharpRegexProbeOptimizations,
        out CSharpRegexProbeMetrics metrics,
        string? filePath = null,
        string? projectRoot = null,
        CancellationToken cancellationToken = default)
    {
        var counts = new CSharpRegexProbeCounts();
        var symbols = ExtractCore(
            fileId,
            "csharp",
            content,
            contentIsNormalized: false,
            hasOversizeLine: null,
            conflictMarkerLine: null,
            filePath,
            projectRoot,
            patternConfigsAlreadyLoaded: false,
            cancellationToken: cancellationToken,
            maxSymbols: null,
            applyRequiredLiteralFileGate: true,
            applyRequiredLiteralMatchInputGate: true,
            requiredLiteralGateCounts: null,
            applyCSharpRegexProbeOptimizations: applyCSharpRegexProbeOptimizations,
            csharpRegexProbeCounts: counts);
        metrics = new CSharpRegexProbeMetrics(
            counts.PropertyPrefixSuffixSkipCount,
            counts.PropertyHeaderRegexAttemptCount,
            counts.MethodHeaderRegexAttemptCount,
            counts.PlainFieldTerminatorSkipCount,
            counts.PlainFieldRegexAttemptCount,
            counts.RecoverablePlainFieldTerminatorSkipCount,
            counts.WrappedModifierLookupCount,
            counts.WrappedModifierAsciiShapeSkipCount,
            counts.WrappedModifierLineRegexAttemptCount,
            counts.WrappedModifierPrefixMaterializationCount,
            counts.WrappedModifierMatchInputMaterializationCount,
            counts.DeclarationPatternRegexAttemptCount,
            counts.PhysicalInputNegativePrefixCacheHitCount);
        return symbols;
    }

    internal static bool TryExtractBounded(
        long fileId,
        string? lang,
        string content,
        int maxSymbols,
        string? filePath,
        string? projectRoot,
        CancellationToken cancellationToken,
        out List<SymbolRecord> symbols)
    {
        if (maxSymbols <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxSymbols));

        ExtractorPluginRegistry.LoadPatternConfigsForProjectRoot(projectRoot);
        var normalizedLanguage = NormalizeLanguage(lang);
        if (normalizedLanguage == null
            || normalizedLanguage is "commonlisp" or "racket" or "solidity" or "html" or "assembly"
            || !PatternCache.ContainsKey(normalizedLanguage))
        {
            symbols = [];
            return false;
        }

        symbols = ExtractCore(
            fileId,
            lang,
            content,
            contentIsNormalized: false,
            hasOversizeLine: null,
            conflictMarkerLine: null,
            filePath,
            projectRoot,
            patternConfigsAlreadyLoaded: true,
            cancellationToken: cancellationToken,
            maxSymbols: maxSymbols);
        return true;
    }

    internal static List<SymbolRecord> ExtractWithPatternConfigsLoaded(
        long fileId,
        string? lang,
        string content,
        string? filePath = null,
        string? projectRoot = null,
        CancellationToken cancellationToken = default)
        => ExtractCore(
            fileId,
            lang,
            content,
            contentIsNormalized: false,
            hasOversizeLine: null,
            conflictMarkerLine: null,
            filePath,
            projectRoot,
            patternConfigsAlreadyLoaded: true,
            cancellationToken: cancellationToken);

    internal static List<SymbolRecord> ExtractNormalized(
        long fileId,
        string? lang,
        string content,
        bool hasOversizeLine,
        string? filePath = null,
        string? projectRoot = null,
        CancellationToken cancellationToken = default,
        int? conflictMarkerLine = null,
        bool patternConfigsAlreadyLoaded = false)
        => ExtractCore(
            fileId,
            lang,
            content,
            contentIsNormalized: true,
            hasOversizeLine,
            conflictMarkerLine,
            filePath,
            projectRoot,
            patternConfigsAlreadyLoaded,
            cancellationToken);

    private static void ExtractHdlInlineParameterSymbols(
        long fileId,
        string[] lines,
        List<SymbolRecord> symbols,
        SymbolExtractionState extractionState)
    {
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (!line.Contains("parameter", StringComparison.Ordinal))
                continue;

            foreach (Match match in Regex.EnumerateMatches(HdlInlineParameterRegex, line))
            {
                var name = match.Groups["name"].ValueSpan.Trim().ToString();
                if (name.Length == 0)
                    continue;

                var lineNumber = index + 1;
                if (HasSymbolLineIdentity(extractionState, symbols, fileId, lineNumber, "property", name))
                    continue;

                symbols.Add(new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "property",
                    Name = name,
                    Line = lineNumber,
                    StartLine = lineNumber,
                    StartColumn = match.Groups["name"].Index,
                    EndLine = lineNumber,
                    Signature = line.Trim(),
                });
            }
        }
    }

    private static bool? TryClassifyCSharpExtractorMetadataTarget(string? lang, string kind, string? signature)
    {
        if (!string.Equals(lang, "csharp", StringComparison.Ordinal) || kind != "class")
            return null;

        foreach (var baseIdentifier in ParseCSharpExtractorBaseIdentifiers(signature))
        {
            var normalized = StripCSharpVerbatimIdentifierPrefixes(baseIdentifier);
            if (string.Equals(normalized, "Attribute", StringComparison.Ordinal)
                || string.Equals(normalized, "System.Attribute", StringComparison.Ordinal)
                || string.Equals(normalized, "global::System.Attribute", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return null;
    }

    private static List<string> ParseCSharpExtractorBaseIdentifiers(string? signature)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(signature))
            return result;

        int colonIdx = FindCSharpExtractorBaseListColon(signature);
        if (colonIdx < 0)
            return result;

        int genericDepth = 0;
        var current = new StringBuilder();
        for (int i = colonIdx + 1; i < signature.Length; i++)
        {
            char c = signature[i];
            if (c == '<')
            {
                genericDepth++;
                current.Append(c);
                continue;
            }
            if (c == '>')
            {
                if (genericDepth > 0)
                    genericDepth--;
                current.Append(c);
                continue;
            }
            if (c == '{')
                break;
            if (genericDepth == 0 && c == ',')
            {
                AddCSharpExtractorBaseIdentifier(result, current.ToString());
                current.Clear();
                continue;
            }
            if (genericDepth == 0 && (c == 'w' || c == 'W') && LooksLikeCSharpWhereKeyword(signature, i))
            {
                AddCSharpExtractorBaseIdentifier(result, current.ToString());
                return result;
            }
            current.Append(c);
        }

        AddCSharpExtractorBaseIdentifier(result, current.ToString());
        return result;
    }

    private static int FindCSharpExtractorBaseListColon(string signature)
    {
        int genericDepth = 0;
        int parenDepth = 0;
        for (int i = 0; i < signature.Length; i++)
        {
            char c = signature[i];
            if (c == '<') { genericDepth++; continue; }
            if (c == '>') { if (genericDepth > 0) genericDepth--; continue; }
            if (c == '(') { parenDepth++; continue; }
            if (c == ')') { if (parenDepth > 0) parenDepth--; continue; }
            if (c == '{')
                return -1;
            if (genericDepth == 0 && parenDepth == 0 && (c == 'w' || c == 'W')
                && LooksLikeCSharpWhereKeyword(signature, i))
            {
                return -1;
            }
            if (c == ':' && genericDepth == 0 && parenDepth == 0)
            {
                if (i + 1 < signature.Length && signature[i + 1] == ':')
                {
                    i++;
                    continue;
                }
                if (i > 0 && signature[i - 1] == ':')
                    continue;
                return i;
            }
        }
        return -1;
    }

    private static bool LooksLikeCSharpWhereKeyword(string signature, int i)
    {
        if (i + 5 > signature.Length)
            return false;
        if (string.Compare(signature, i, "where", 0, 5, StringComparison.OrdinalIgnoreCase) != 0)
            return false;
        if (i > 0)
        {
            char prev = signature[i - 1];
            if (char.IsLetterOrDigit(prev) || prev == '_')
                return false;
        }
        if (i + 5 < signature.Length)
        {
            char next = signature[i + 5];
            if (char.IsLetterOrDigit(next) || next == '_')
                return false;
        }
        return true;
    }

    private static void AddCSharpExtractorBaseIdentifier(List<string> result, string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
            return;

        int cut = trimmed.Length;
        for (int i = 0; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if (c == '<' || char.IsWhiteSpace(c))
            {
                cut = i;
                break;
            }
        }
        var head = trimmed[..cut];
        if (head.Length > 0)
            result.Add(head);
    }

    private static string StripCSharpVerbatimIdentifierPrefixes(string value)
    {
        if (value.IndexOf('@', StringComparison.Ordinal) < 0)
            return value;
        return value.Replace(".@", ".", StringComparison.Ordinal)
            .Replace("::@", "::", StringComparison.Ordinal)
            .TrimStart('@');
    }

    private static void ClassifyScalaCompanions(List<SymbolRecord> symbols)
    {
        Dictionary<string, List<SymbolRecord>>? topLevelClasses = null;
        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "class"
                || !string.IsNullOrWhiteSpace(symbol.ContainerKind)
                || string.IsNullOrWhiteSpace(symbol.Name))
            {
                continue;
            }

            topLevelClasses ??= new Dictionary<string, List<SymbolRecord>>(StringComparer.Ordinal);
            if (!topLevelClasses.TryGetValue(symbol.Name, out var companionClasses))
            {
                companionClasses = new List<SymbolRecord>();
                topLevelClasses.Add(symbol.Name, companionClasses);
            }

            companionClasses.Add(symbol);
        }

        if (topLevelClasses is not { Count: > 0 })
            return;

        foreach (var scalaObject in symbols)
        {
            if (scalaObject.Kind != "object"
                || !string.IsNullOrWhiteSpace(scalaObject.ContainerKind)
                || string.IsNullOrWhiteSpace(scalaObject.Name))
            {
                continue;
            }

            if (!topLevelClasses.TryGetValue(scalaObject.Name, out var companionClasses))
                continue;

            scalaObject.SubKind ??= "companion_object";
            foreach (var companionClass in companionClasses)
                companionClass.SubKind ??= "has_companion_object";
        }
    }

    private static void ClassifyJavaScriptTypeScriptReactHooks(List<SymbolRecord> symbols)
    {
        foreach (var symbol in symbols)
        {
            if (symbol.Kind is "function" or "lambda" && IsJavaScriptTypeScriptReactHookName(symbol.Name))
                symbol.Kind = "hook";
        }
    }


    internal static bool IsJavaScriptTypeScriptReactHookName(string name)
        => name.Length >= 4
           && name.StartsWith("use", StringComparison.Ordinal)
           && IsJavaScriptTypeScriptIdentifierStart(name[3])
           && char.IsUpper(name[3]);




    private static int FindFirstNonWhitespaceColumn(string text)
    {
        var column = 0;
        while (column < text.Length && char.IsWhiteSpace(text[column]))
            column++;

        return column;
    }


    // Java identifier start: Unicode letter / letter-number / underscore / dollar. Continue chars also
    // allow digits, connector punctuation, and combining marks so enum members like `RÉSUMÉ` survive intact.
    // Java 識別子の先頭: Unicode の letter / letter-number / underscore / dollar。
    // 継続文字は数字・connector punctuation・結合文字も許可し、`RÉSUMÉ` のような enum member を切らない。
    public static void ApplyFamilyScope(IEnumerable<SymbolRecord> symbols, string scopeKey)
        => ApplyFamilyScope(symbols, scopeKey, lang: null);

    public static void ApplyFamilyScope(
        IEnumerable<SymbolRecord> symbols,
        string scopeKey,
        string? lang)
    {
        // The current C# family contract owns encoded scopes. Other languages retain their v2 raw
        // family keys so incremental updates cannot mix two key formats under one ready stamp.
        // encoded scope は current C# family contract でのみ使用する。他言語は v2 の raw family key を
        // 維持し、増分更新で異なる形式が ready 状態に混在することを防ぐ。
        var persistedScopeKey = string.Equals(lang, "csharp", StringComparison.Ordinal)
            ? EncodeFamilyScopeKey(scopeKey)
            : scopeKey;
        foreach (var symbol in symbols)
        {
            if (string.IsNullOrWhiteSpace(symbol.FamilyKey))
                continue;

            symbol.FamilyKey = $"{persistedScopeKey}|{symbol.FamilyKey}";
        }
    }

    internal static string EncodeFamilyScopeKey(string scopeKey)
        // `%` is escaped first so a literal `%7C` path cannot collide with an encoded pipe.
        // `%` を先に escape し、literal な `%7C` path と encoded pipe の衝突を防ぐ。
        => scopeKey
            .Replace("%", "%25", StringComparison.Ordinal)
            .Replace("|", "%7C", StringComparison.Ordinal)
            .Replace("\u001f", "%1F", StringComparison.Ordinal);

    private static bool TryAddRPacmanPackageLoaderSymbols(
        long fileId,
        string line,
        int lineNumber,
        List<SymbolRecord> symbols,
        SymbolExtractionState extractionState)
    {
        var codeLine = StripRCommentForPackageLoader(line);
        var startMatch = RPacmanPackageLoaderStartRegex.Match(codeLine);
        if (!startMatch.Success)
            return false;

        var argsStart = startMatch.Index + startMatch.Length;
        var args = codeLine[argsStart..];
        var added = false;
        foreach (Match match in Regex.EnumerateMatches(RPacmanPackageLoaderArgumentRegex, args))
        {
            var quotedNameGroup = match.Groups["quotedName"];
            var nameGroup = quotedNameGroup.Success ? quotedNameGroup : match.Groups["name"];
            if (!nameGroup.Success)
                continue;

            AddSymbolRecord(
                symbols,
                extractionState,
                cssSeenSymbols: null,
                lineNumber,
                new SymbolRecord
                {
                    FileId = fileId,
                    Kind = "import",
                    Name = nameGroup.Value,
                    Line = lineNumber,
                    StartLine = lineNumber,
                    StartColumn = argsStart + nameGroup.Index,
                    EndLine = lineNumber,
                    Signature = line.Trim(),
                },
                line);
            added = true;
        }

        return added;
    }

    private static string StripRCommentForPackageLoader(string line)
    {
        var inBacktickIdentifier = false;
        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quote != '\0')
            {
                if (ch == '\\' && i + 1 < line.Length)
                {
                    i++;
                    continue;
                }

                if (ch == quote)
                    quote = '\0';
                continue;
            }

            if (inBacktickIdentifier)
            {
                if (ch == '`')
                    inBacktickIdentifier = false;
                continue;
            }

            if (ch == '`')
            {
                inBacktickIdentifier = true;
                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                continue;
            }

            if (ch == '#')
                return line[..i];
        }

        return line;
    }

    private static bool TryGetSameLineSignatureKey(SymbolRecord symbol, out SameLineSignatureKey key)
    {
        if (symbol.Signature != null
            && symbol.StartLine == symbol.EndLine
            && symbol.Line == symbol.StartLine)
        {
            key = new SameLineSignatureKey(symbol.Line, symbol.StartLine, symbol.Signature);
            return true;
        }

        key = default;
        return false;
    }

    private static SymbolExtractionState ResolveExtractionState(List<SymbolRecord> symbols) =>
        symbols is SymbolExtractionList extractionSymbols
            ? extractionSymbols.ExtractionState
            : SymbolExtractionState.FromSymbols(symbols);

    private static void AddSymbolRecord(
        List<SymbolRecord> symbols,
        HashSet<SymbolLineIdentity>? cssSeenSymbols,
        int lineNumber,
        SymbolRecord symbol,
        string? rawLine = null) =>
        AddSymbolRecord(symbols, ResolveExtractionState(symbols), cssSeenSymbols, lineNumber, symbol, rawLine);

    private static void AddSymbolRecord(
        List<SymbolRecord> symbols,
        SymbolExtractionState extractionState,
        HashSet<SymbolLineIdentity>? cssSeenSymbols,
        int lineNumber,
        SymbolRecord symbol,
        string? rawLine = null)
    {
        if (symbols is SymbolExtractionList extractionSymbols && extractionSymbols.IsAtCapacity)
            return;

        if (string.IsNullOrWhiteSpace(symbol.Name))
            return;

        if (cssSeenSymbols != null)
        {
            var identity = new SymbolLineIdentity(symbol.FileId, lineNumber, symbol.Kind, symbol.Name);
            if (!cssSeenSymbols.Add(identity))
                return;
        }

        if (symbol.Kind == "function"
            && (symbol.BodyStartLine != null || symbol.BodyEndLine != null))
        {
            RemoveTrailingSameNameDeclarationOnlyFunctions(symbols, extractionState, symbol);
        }

        symbol.SameLineSignatureOccurrenceIndex = extractionState.GetSameLineSignatureOccurrenceIndex(symbol);

        // Same-line restart paths can legitimately revisit the same declaration from a
        // different regex row or restart offset. Suppress only exact duplicate symbol
        // records so mixed-kind recovery does not emit the same declaration twice while
        // still allowing legitimate overloads / siblings with the same short name but
        // different ranges or signatures. Closes #472 / #473 follow-up.
        // same-line の restart 経路では、別 regex 行や別 restart offset から同じ宣言を
        // 再訪しうる。ここでは exact duplicate の `SymbolRecord` だけを抑止し、
        // mixed-kind 回復で同じ宣言が二重出力されるのを防ぎつつ、範囲や signature が
        // 異なる正当な overload / sibling はそのまま残す。Closes #472 / #473 follow-up.
        var duplicateCount = extractionState.GetExactDuplicateCount(symbol);
        if (duplicateCount > 0
            && !HasRemainingSameLineSignatureOccurrence(symbol, rawLine, duplicateCount))
        {
            return;
        }

        extractionState.Record(symbol);
        symbols.Add(symbol);
    }


    private static void RemoveTrailingSameNameDeclarationOnlyFunctions(
        List<SymbolRecord> symbols,
        SymbolExtractionState extractionState,
        SymbolRecord symbol)
    {
        for (var index = symbols.Count - 1; index >= 0; index--)
        {
            var prior = symbols[index];
            if (prior.FileId != symbol.FileId
                || prior.Kind != symbol.Kind
                || !string.Equals(prior.Name, symbol.Name, StringComparison.Ordinal)
                || !string.Equals(prior.ContainerKind, symbol.ContainerKind, StringComparison.Ordinal)
                || !string.Equals(prior.ContainerName, symbol.ContainerName, StringComparison.Ordinal)
                || !string.Equals(prior.ContainerQualifiedName, symbol.ContainerQualifiedName, StringComparison.Ordinal))
            {
                break;
            }

            if (prior.BodyStartLine != null || prior.BodyEndLine != null)
                break;

            var signature = prior.Signature?.TrimStart();
            if (signature != null
                && (signature.StartsWith("declare ", StringComparison.Ordinal)
                    || CSharpPartialFunctionDeclarationSignatureRegex.IsMatch(signature)
                    || IsAdaForwardDeclarationPair(signature, symbol.Signature)))
            {
                break;
            }

            extractionState.Remove(prior);
            symbols.RemoveAt(index);
        }
    }

    private static bool IsAdaForwardDeclarationPair(
        string declarationSignature,
        string? implementationSignature)
    {
        if (implementationSignature == null
            || !AdaRoutineBodySignatureRegex.IsMatch(implementationSignature))
        {
            return false;
        }

        var trimmed = declarationSignature.Trim();
        if (!trimmed.EndsWith(';'))
            return false;

        return trimmed.StartsWith("procedure ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("function ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("overriding procedure ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("overriding function ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("not overriding procedure ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("not overriding function ", StringComparison.OrdinalIgnoreCase);
    }

    // Some compact same-line C# fixtures can legitimately contain two distinct siblings with
    // the same short signature on the same physical line
    // (`Child { } } public partial class Child { }`). Allow as many identical rows as the raw
    // line actually contains, and suppress only the true restart duplicates beyond that. Closes #552.
    // compact な同一行 C# fixture では、同じ短い signature を持つ別 sibling が同じ物理行に
    // 実在しうる (`Child { } } public partial class Child { }`)。raw 行に実在する出現回数までは
    // 許容し、それを超える restart 由来の真の duplicate だけを抑止する。Closes #552.
    private static bool HasRemainingSameLineSignatureOccurrence(SymbolRecord symbol, string? rawLine, int duplicateCount)
    {
        if (rawLine == null
            || symbol.Signature == null
            || symbol.StartLine != symbol.EndLine
            || symbol.Line != symbol.StartLine)
        {
            return false;
        }

        return CountNonOverlappingOccurrences(rawLine, symbol.Signature) > duplicateCount;
    }


    private static (int EndLine, int? BodyStartLine, int? BodyEndLine) ResolveRange(string[] lines, int startIndex, BodyStyle bodyStyle) =>
        ResolveRange(lines, startIndex, bodyStyle, null, 0, null, null);

    private static (int EndLine, int? BodyStartLine, int? BodyEndLine) ResolveRange(
        string[] lines,
        int startIndex,
        BodyStyle bodyStyle,
        string? lang = null,
        int startColumn = 0,
        string[]? scientificBodyScannerLines = null,
        bool[]? matlabExplicitOuterClosureByLine = null)
    {
        return bodyStyle switch
        {
            BodyStyle.Brace when lang is "javascript" or "typescript" => FindJavaScriptBraceRange(lines, startIndex, lang, startColumn),
            BodyStyle.Brace when lang == "csharp" => FindCSharpBraceRange(lines, startIndex, startColumn),
            BodyStyle.Brace when lang == "java" => FindJavaBraceRange(lines, startIndex, startColumn),
            BodyStyle.Brace when lang == "shell" => FindShellFunctionRange(lines, startIndex, startColumn),
            BodyStyle.Brace => FindBraceRange(lines, startIndex, startColumn, lang),
            BodyStyle.Indent => FindIndentRange(lines, startIndex),
            BodyStyle.RubyEnd => FindRubyRange(lines, startIndex),
            BodyStyle.FortranEnd => FindFortranRange(lines, startIndex),
            BodyStyle.ElixirEnd => FindElixirRange(lines, startIndex),
            BodyStyle.ScientificEnd when lang is "julia" or "matlab" => FindScientificEndRange(
                  scientificBodyScannerLines ?? PrepareScientificBodyScannerLines(lines, lang),
                  startIndex,
                  lang,
                  matlabExplicitOuterClosureByLine: matlabExplicitOuterClosureByLine),
            BodyStyle.JuliaShortFunction when lang == "julia" => FindJuliaShortFunctionRange(
                scientificBodyScannerLines ?? PrepareScientificBodyScannerLines(lines, lang),
                startIndex),
            BodyStyle.VisualBasicEnd => FindVisualBasicRange(lines, startIndex),
            BodyStyle.PascalEnd => FindPascalRange(lines, startIndex),
            BodyStyle.AdaEnd => FindAdaRange(lines, startIndex),
            BodyStyle.SmalltalkMethod => FindSmalltalkMethodRange(lines, startIndex),
            BodyStyle.SqlProcBody => FindSqlProcBodyRange(lines, startIndex),
            _ => (startIndex + 1, null, null),
        };
    }
    private static bool[] FindCSharpSwitchExpressionLines(string[] structuralLines)
    {
        var switchExpressionLines = new bool[structuralLines.Length];
        Stack<bool>? braceKinds = null;
        var activeSwitchExpressionDepth = 0;
        var pendingSwitchExpression = 0;
        var pendingSwitchKeyword = false;
        var insideBlockComment = false;

        for (int lineIndex = 0; lineIndex < structuralLines.Length; lineIndex++)
        {
            if (activeSwitchExpressionDepth > 0)
                switchExpressionLines[lineIndex] = true;

            var line = structuralLines[lineIndex];
            for (int cursor = 0; cursor < line.Length; cursor++)
            {
                if (insideBlockComment)
                {
                    if (cursor + 1 < line.Length && line[cursor] == '*' && line[cursor + 1] == '/')
                    {
                        insideBlockComment = false;
                        cursor++;
                    }

                    continue;
                }

                if (cursor + 1 < line.Length && line[cursor] == '/' && line[cursor + 1] == '/')
                    break;

                if (cursor + 1 < line.Length && line[cursor] == '/' && line[cursor + 1] == '*')
                {
                    insideBlockComment = true;
                    cursor++;
                    continue;
                }

                if (char.IsWhiteSpace(line[cursor]))
                    continue;

                if (pendingSwitchKeyword)
                {
                    if (line[cursor] == '(')
                    {
                        pendingSwitchKeyword = false;
                    }
                    else if (line[cursor] == '{')
                    {
                        pendingSwitchExpression++;
                        pendingSwitchKeyword = false;
                    }
                    else
                    {
                        pendingSwitchKeyword = false;
                    }
                }

                if (IsCSharpKeywordAt(line, cursor, "switch"))
                {
                    pendingSwitchKeyword = true;
                    cursor += "switch".Length - 1;
                    continue;
                }

                if (line[cursor] == '{')
                {
                    var startsSwitchExpression = pendingSwitchExpression > 0;
                    (braceKinds ??= new Stack<bool>()).Push(startsSwitchExpression);
                    if (startsSwitchExpression)
                    {
                        pendingSwitchExpression--;
                        activeSwitchExpressionDepth++;
                    }

                    continue;
                }

                if (line[cursor] == '}' && braceKinds != null && braceKinds.Count > 0)
                {
                    if (braceKinds.Pop())
                        activeSwitchExpressionDepth--;
                }
            }
        }

        return switchExpressionLines;
    }

    private static bool IsCSharpKeywordAt(string line, int index, string keyword)
    {
        if (index < 0 || index + keyword.Length > line.Length)
            return false;

        if (!line.AsSpan(index, keyword.Length).SequenceEqual(keyword))
            return false;

        var previous = index > 0 ? line[index - 1] : '\0';
        if (previous == '@' || previous == '_' || char.IsLetterOrDigit(previous))
            return false;

        var nextIndex = index + keyword.Length;
        if (nextIndex >= line.Length)
            return true;

        var next = line[nextIndex];
        return next != '_' && !char.IsLetterOrDigit(next);
    }




    private static readonly Regex ComplexityRegex = new(
        @"\b(?:if|else\s+if|elif|elsif|elseif|case|catch|except|when|while|for|foreach|guard)\b|(?:\?\?|&&|\|\||[?:](?!=))",
        RegexOptions.Compiled);
    /// <summary>
    /// Estimate cyclomatic complexity of a code body using keyword counting.
    /// This is a heuristic — not a true control-flow-graph analysis.
    /// Baseline is 1 (a straight-line function has complexity 1).
    /// コードボディのサイクロマティック複雑度をキーワードカウントで推定する。
    /// 真の制御フローグラフ解析ではなくヒューリスティック。基準値は1（直線的関数の複雑度）。
    /// </summary>
    public static int EstimateComplexity(string bodyContent)
    {
        if (string.IsNullOrWhiteSpace(bodyContent))
            return 1;
        return 1 + Regex.CountMatches(ComplexityRegex, bodyContent);
    }
}
