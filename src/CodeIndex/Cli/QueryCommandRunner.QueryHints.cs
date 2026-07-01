using CodeIndex.Database;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    /// <summary>
    /// Show available languages when --lang produces zero results.
    /// --lang で 0 件のとき、有効な言語を表示する。
    /// </summary>
    private static void WriteLangHint(string? lang, DbReader reader)
    {
        if (lang == null) return;
        var status = reader.GetStatus();
        if (status.Languages.Count > 0 && status.Languages.ContainsKey(lang))
            return;

        if (status.Languages.Count > 0)
            CommandErrorWriter.WriteStderr($"Hint: '{lang}' not found in index. Available: {string.Join(", ", status.Languages.Keys.OrderBy(l => l))}");

        // Recover from `--lang pythno` / `--lang csarp` typos by suggesting the
        // closest indexed language first; if the typo does not match anything currently
        // in the DB (or the DB has no languages yet) fall back to the full supported set
        // exposed by `ReferenceExtractor.GetSupportedLanguages()` so the suggester is still
        // useful against an empty/fresh index (#1582).
        // `--lang pythno` / `--lang csarp` のようなタイプミスから回復させるため、
        // インデックスに存在する言語の中から最も近いものを優先的に提案する。
        // インデックスに無い、もしくは languages が空の場合は
        // `ReferenceExtractor.GetSupportedLanguages()` 全体から候補を探し、
        // 空のインデックスでも did-you-mean が機能するようにする (#1582)。
        // Skip the suggestion entirely if the closest candidate is the exact value the user
        // already supplied (case-insensitive). FindClosestMatch returns the input verbatim when
        // it is a member of the candidate set — e.g. `--lang java` against a Java-supported but
        // unindexed repo would otherwise self-suggest "Did you mean: --lang java?".
        // 提案候補がユーザー指定値そのものと一致する場合は提案を出さない。
        // FindClosestMatch は候補集合に同名がいれば入力をそのまま返すため、例えば Java は
        // サポート対象だが index 済みでない場合の `--lang java` で自己提案を出してしまう。
        var suggestion = ConsoleUi.FindClosestMatch(lang, status.Languages.Keys)
                         ?? ConsoleUi.FindClosestMatch(lang, ReferenceExtractor.GetSupportedLanguages());
        if (suggestion != null && !string.Equals(suggestion, lang, StringComparison.OrdinalIgnoreCase))
            CommandErrorWriter.WriteStderr($"Did you mean: --lang {suggestion}?");
    }

    private static void WriteSymbolExtractionCapabilityHint(string? lang, DbReader reader)
    {
        if (string.IsNullOrWhiteSpace(lang))
            return;
        if (SymbolExtractor.GetSupportedLanguages().Contains(lang, StringComparer.Ordinal))
            return;

        var status = reader.GetStatus();
        if (status.Languages.Count == 0 || !status.Languages.ContainsKey(lang))
            return;

        CommandErrorWriter.WriteStderr($"Hint: '{lang}' is indexed for full-text search, but symbol extraction is not available for that language. Use `cdidx search <query> --lang {lang}` for text matches or `cdidx languages --capability missing-symbols` to audit capability gaps.");
    }

    // All valid symbol kinds emitted by SymbolExtractor / SymbolExtractor が出力する全有効シンボル種別
    private static readonly HashSet<string> KnownSymbolKindFilters = new(StringComparer.Ordinal)
    {
        "accessor",
        "associatedtype",
        "attribute",
        "class",
        "class_hook",
        "constant",
        "constructor",
        "delegate",
        "enum",
        "event",
        "field",
        "function",
        "heading",
        "hook",
        "impl",
        "implements",
        "import",
        "interface",
        "label",
        "lambda",
        "layout",
        "method",
        "module",
        "namespace",
        "object",
        "operator",
        "package",
        "procedure",
        "property",
        "protocol",
        "record",
        "reference",
        "route",
        "specialization",
        "struct",
        "test.method",
        "trait",
        "type",
        "typealias",
        "union",
        "variable",
    };

    private static readonly string[] AllValidKinds =
        KnownSymbolKindFilters.OrderBy(kind => kind, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Show available symbol kinds when --kind produces zero results.
    /// --kind で 0 件のとき、有効なシンボル種別を表示する。
    /// </summary>
    private static void WriteKindHint(string? kind, DbReader reader)
    {
        if (kind == null) return;
        if (!AllValidKinds.Contains(kind))
        {
            CommandErrorWriter.WriteStderr($"Hint: '{kind}' is not a known kind. Available: {string.Join(", ", AllValidKinds)}");
            var suggestion = ConsoleUi.FindClosestMatch(kind, AllValidKinds);
            if (suggestion != null)
                CommandErrorWriter.WriteStderr($"Did you mean: --kind {suggestion}?");
            return;
        }
        // Kind is valid but not found in this index — hint that no symbols of this kind exist
        // 種別は有効だがインデックスに存在しない場合のヒント
        var existingKinds = reader.GetDistinctKinds();
        if (!existingKinds.Contains(kind))
            CommandErrorWriter.WriteStderr($"Hint: no '{kind}' symbols in the index. Indexed kinds: {string.Join(", ", existingKinds)}");
    }

    private static void WriteValidateKindHint(string? kind)
    {
        if (string.IsNullOrEmpty(kind)) return;
        if (AllValidValidateKinds.Contains(kind, StringComparer.Ordinal))
            return;

        // `validate --kind` accepts only the file-issue kinds emitted by FileIndexer. A typo
        // like `--kind replacement_chra` filters to zero rows, which previously printed the
        // same "No encoding issues found." message as a genuinely clean repo and silently
        // hid the typo. Surface a hint + suggester for the closest known kind (#1582).
        // `validate --kind` は FileIndexer が出す file_issues kind のみ受理する。
        // `--kind replacement_chra` のようなタイプミスは 0 行となり、クリーンな状態と区別が
        // つかないまま暗黙に握り潰されていた。ヒントと did-you-mean を出すよう改修 (#1582)。
        CommandErrorWriter.WriteStderr($"Hint: '{kind}' is not a known validate kind. Available: {string.Join(", ", AllValidValidateKinds)}");
        var suggestion = ConsoleUi.FindClosestMatch(kind, AllValidValidateKinds);
        if (suggestion != null)
            CommandErrorWriter.WriteStderr($"Did you mean: --kind {suggestion}?");
    }
}
