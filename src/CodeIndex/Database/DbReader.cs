using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeIndex.Database;

public readonly record struct ExactQuerySignal(
    bool ExactIndexAvailable,
    bool HasMissingIndex,
    bool HasMissingTable,
    string? DegradedReason);

public readonly record struct HotspotFamilySignal(
    bool Ready,
    bool Relevant,
    string? DegradedReason);

public readonly record struct SqlGraphContractSignal(
    bool Ready,
    bool Relevant,
    string? DegradedReason);

/// <summary>
/// Handles read/query operations against the database for search, symbols, and files.
/// 検索・シンボル・ファイル一覧などのDB読み取り操作を担当する。
/// </summary>
public partial class DbReader
{
    private static readonly Regex ImpactSignatureIdentifierRegex = new(@"[\p{L}_][\p{L}\p{Nd}_]*", RegexOptions.Compiled);
    private static readonly Regex CSharpUsingStaticImportRegex = new(@"^\s*(?:global\s+)?using\s+static\s+(?<target>[^;]+)", RegexOptions.Compiled);
    private static readonly Regex CSharpUsingAliasImportRegex = new(@"^\s*(?:global\s+)?using\s+(?!static\b)(?<alias>[^\s=;]+)\s*=\s*(?<target>[^;]+)", RegexOptions.Compiled);
    private static readonly Regex CSharpUsingNamespaceImportRegex = new(@"^\s*(?:global\s+)?using\s+(?!static\b)(?<target>[^;=]+)", RegexOptions.Compiled);
    private static readonly IReadOnlyDictionary<string, string> QueryLanguageAliases = BuildQueryLanguageAliases();
    private readonly SqliteConnection _conn;
    private readonly bool _isReadOnly;
    private readonly HashSet<string> _fileColumns;
    private readonly HashSet<string> _symbolColumns;
    private readonly HashSet<string> _referenceColumns;
    private readonly HashSet<string> _symbolIndexes;
    private readonly HashSet<string> _referenceIndexes;
    private readonly HashSet<string> _indexedHotspotFamilyLanguages;
    private readonly Dictionary<string, List<CSharpUsingStaticScope>> _csharpUsingStaticScopesByPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<CSharpNamespaceScope>> _csharpNamespaceScopesByPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<CSharpContainingTypeScope>> _csharpContainingTypeScopesByPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<CSharpUsingNamespaceScope>> _csharpUsingNamespaceScopesByPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<CSharpUsingAliasScope>> _csharpUsingAliasScopesByPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _csharpConstantPatternContainersByMemberName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<CSharpTypeNamespaceCandidate>> _csharpTypeNamespacesByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<CSharpContainingTypeCandidate>> _csharpTypeContainingTypesByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _csharpInheritedContainingTypesByQualifiedName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CSharpContainingTypeScope?> _csharpContainingTypeScopeByQualifiedName = new(StringComparer.Ordinal);
    private HashSet<string>? _csharpGlobalUsingStaticTargets;
    private HashSet<string>? _csharpGlobalUsingNamespaces;
    private Dictionary<string, CSharpUsingAliasScope>? _csharpGlobalUsingAliasesByName;
    internal readonly bool _hasReferencesTable;
    internal readonly bool _hasIssuesTable;
    internal readonly bool _hasChunksTable;
    internal readonly bool _hasReferenceLinesTable;
    internal readonly bool _canUseReferenceLines;
    // #86: True when every symbols / symbol_references row has name_folded populated and
    // the Unicode fold path is safe to use for `--exact`. Legacy / partial-backfill DBs
    // read this as false and fall back to the ASCII-only `COLLATE NOCASE` path.
    // #86: name_folded 列が全行埋まっているか（fold 経路を使えるか）。
    internal readonly bool _foldReady;
    internal readonly bool _csharpSymbolNameContractCurrent;
    // #435: True when `symbols.is_metadata_target` has been populated for every C# class-like
    // row by the writer's resolver and the stamp in `codeindex_meta` matches the current
    // version. Readers that enforce metadata-target eligibility prefer this column over the
    // legacy `signature LIKE '%: %'` heuristic when the flag is true; otherwise they continue
    // to fall back to the heuristic so legacy / partial DBs do not silently miss edges.
    // #435: C# の authoritative `is_metadata_target` が全行 populate されて stamp 一致したときのみ
    // true。true なら reader は legacy ヒューリスティックではなく列を使う。false の DB では
    // 従来どおり `signature LIKE '%: %'` にフォールバックする。
    internal readonly bool _csharpMetadataTargetReady;
    internal readonly bool _sqlGraphContractCurrent;
    // Tracks which languages have authoritative cross-file hotspot family semantics.
    // Mixed legacy/update states can therefore degrade only the affected language instead of
    // globally disabling families for unrelated marker types.
    // authoritative な hotspot family semantics を保持する言語集合。
    internal readonly HashSet<string> _hotspotFamilyReadyLanguages;
    internal const string TestPathCondition = @"
        (
            lower(f.path) LIKE 'tests/%' OR
            lower(f.path) LIKE '%/tests/%' OR
            lower(f.path) LIKE 'test/%' OR
            lower(f.path) LIKE '%/test/%' OR
            lower(f.path) LIKE 'tests.%' OR
            lower(f.path) LIKE '%/tests.%' OR
            lower(f.path) LIKE 'test.%' OR
            lower(f.path) LIKE '%/test.%' OR
            lower(f.path) LIKE 'tests\_%' ESCAPE '\' OR
            lower(f.path) LIKE '%/tests\_%' ESCAPE '\' OR
            lower(f.path) LIKE 'test\_%' ESCAPE '\' OR
            lower(f.path) LIKE '%/test\_%' ESCAPE '\' OR
            lower(f.path) LIKE '%\_tests.%' ESCAPE '\' OR
            lower(f.path) LIKE '%\_test.%' ESCAPE '\' OR
            lower(f.path) LIKE 'conftest.py' OR
            lower(f.path) LIKE '%/conftest.py' OR
            lower(f.path) LIKE '%.spec.%' OR
            lower(f.path) LIKE '%.test.%'
        )";
    private const string PathBucketOrder = @"
        CASE
            WHEN " + TestPathCondition + @" THEN 1
            WHEN lower(f.path) LIKE 'docs/%' OR lower(f.path) LIKE '%/docs/%' OR lower(f.path) LIKE 'readme%' OR lower(f.path) LIKE 'changelog%' OR lower(f.path) LIKE '%.md'
                THEN 2
            ELSE 0
        END";
    private const string InvokeReferenceKindsSql = "('call', 'instantiate')";
    // Reference kinds that participate in the call-graph (callers/callees/hotspots). Metadata
    // kinds such as `attribute` / `annotation` are excluded so they do not inflate the graph
    // with non-call edges (issue #293).
    // call-graph (callers/callees/hotspots) に参加する reference kind。`attribute` / `annotation`
    // のようなメタデータ kind は非呼び出しエッジなのでここから除外する (issue #293)。
    internal const string CallGraphReferenceKindsSql = "('call', 'instantiate', 'subscribe')";
    private const string SyntheticTopLevelCallerName = "<top-level>";
    private const string SyntheticTopLevelCallerKind = "function";

    // Reference kinds that represent compile-time type/member references (e.g. C# `nameof(X)`,
    // `typeof(T)`, Java `T.class`). They are intentionally excluded from default `callers` /
    // `callees` results because they are not invocation edges, but they remain discoverable
    // via `references` and explicit `--kind type_reference` queries. See issue #253.
    // コンパイル時の型・メンバ参照（C# の nameof/typeof、Java の `.class` 等）。
    // 呼び出しエッジではないため既定の callers/callees からは除外するが、
    // references や `--kind type_reference` 経由では引き続き参照できる。issue #253 参照。
    private const string NonInvocationReferenceKindsExclusion =
        " AND r.reference_kind != 'type_reference'";
    private const int CSharpUsingStaticReferenceFilterChunkSize = 64;
    private const int CSharpUsingStaticReferenceFilterMaxRawLimit = 65536;
    private sealed record CSharpNamespaceScope(string QualifiedName, int ScopeStartLine, int ScopeEndLine);
    private sealed record CSharpContainingTypeScope(string Path, string Kind, string QualifiedName, string? Visibility, string? Signature, int DeclarationLine, int ScopeStartLine, int ScopeEndLine);
    private sealed record CSharpUsingStaticScope(string TargetQualifiedName, int Line, int ScopeStartLine, int ScopeEndLine);
    private sealed record CSharpUsingNamespaceScope(string TargetQualifiedName, int Line, int ScopeStartLine, int ScopeEndLine);
    private sealed record CSharpUsingAliasScope(string AliasName, string TargetQualifiedName, int Line, int ScopeStartLine, int ScopeEndLine, bool TargetsType);
    private sealed record CSharpTypeNamespaceCandidate(string QualifiedName, string Path, bool IsFileLocal);
    private sealed record CSharpContainingTypeCandidate(string QualifiedName, bool AccessibleFromDerivedType);
    private sealed record SearchReferenceRawRow(string Path, string? Lang, string SymbolName, string ReferenceKind, int Line, int Column, string Context, string? ContainerKind, string? ContainerName);

    /// <summary>
    /// Visibility ranking: public symbols first, then protected, internal, private, unknown last.
    /// 可視性ランキング: public を最優先、次に protected、internal、private、不明は最後。
    /// </summary>
    internal string VisibilityOrder => $@"
        CASE lower({GetSymbolColumnSql("visibility")})
            WHEN 'public' THEN 0
            WHEN 'open' THEN 0
            WHEN 'pub' THEN 0
            WHEN 'export' THEN 0
            WHEN 'protected' THEN 1
            WHEN 'protected internal' THEN 1
            WHEN 'internal' THEN 2
            WHEN 'private protected' THEN 2
            WHEN 'private' THEN 3
            WHEN 'fileprivate' THEN 3
            ELSE 4
        END";
    private const string ExactSymbolMatchOrder = @"
        CASE
            WHEN EXISTS (
                SELECT 1
                FROM symbols sx
                WHERE sx.file_id = f.id
                  AND lower(sx.name) = lower(@rankingQuery)
            ) THEN 0
            ELSE 1
        END";
    private const string PrefixSymbolMatchOrder = @"
        CASE
            WHEN EXISTS (
                SELECT 1
                FROM symbols sx
                WHERE sx.file_id = f.id
                  AND lower(sx.name) LIKE lower(@rankingQueryPrefix) ESCAPE '\'
            ) THEN 0
            ELSE 1
        END";
    private const string PathTextMatchOrder = @"
        CASE
            WHEN instr(lower(f.path), lower(@rankingQuery)) > 0 THEN 0
            ELSE 1
        END";
    private const string ChunkTextMatchOrder = @"
        CASE
            WHEN instr(lower(c.content), lower(@rankingQuery)) > 0 THEN 0
            ELSE 1
        END";
    private static string GetLogicalReferenceKindSql(string referenceKindSql)
        => $"CASE WHEN {referenceKindSql} IN {InvokeReferenceKindsSql} THEN 'invoke' ELSE {referenceKindSql} END";

    private static string GetPreferredReferenceKindSql(string referenceKindSql)
        => $"CASE WHEN SUM(CASE WHEN {referenceKindSql} = 'instantiate' THEN 1 ELSE 0 END) > 0 THEN 'instantiate' ELSE MIN({referenceKindSql}) END";

    private static string GetGroupedCallerReferenceKindSql(string referenceKindSql)
        => $"CASE WHEN SUM(CASE WHEN {referenceKindSql} = 'instantiate' THEN 1 ELSE 0 END) > 0 THEN 'instantiate' " +
           $"WHEN SUM(CASE WHEN {referenceKindSql} = 'subscribe' THEN 1 ELSE 0 END) > 0 THEN 'subscribe' " +
           $"ELSE MIN({referenceKindSql}) END";

    private static string GetPathBucketOrderSql(string pathSql)
        => PathBucketOrder.Replace("f.path", pathSql, StringComparison.Ordinal);

    // Parse a GROUP_CONCAT(DISTINCT reference_kind) string into a sorted, deduplicated
    // list. Falls back to the primary/summary kind if the aggregate column is null or
    // empty so downstream consumers always see at least one entry (issue #501).
    // GROUP_CONCAT(DISTINCT reference_kind) を安定ソート済みの重複排除済みリストに
    // パースする。aggregate 列が null / 空の場合は代表 kind をフォールバックとして
    // 返し、消費側が常に 1 要素以上を得られるようにする (issue #501)。
    private static IReadOnlyList<string> ParseDistinctReferenceKinds(string? aggregate, string primaryKind)
    {
        if (string.IsNullOrEmpty(aggregate))
            return string.IsNullOrEmpty(primaryKind) ? Array.Empty<string>() : new[] { primaryKind };
        // Fast path: a single-kind row (the overwhelming common case) has no
        // comma in the aggregate, so skip the SortedSet / split allocation.
        // Fast path: 単一 kind の行（大多数）では aggregate にカンマが無いため、
        // SortedSet / split のアロケーションを省略する。
        if (aggregate.IndexOf(',') < 0)
        {
            var only = aggregate.Trim();
            if (only.Length > 0)
                return new[] { only };
            return string.IsNullOrEmpty(primaryKind) ? Array.Empty<string>() : new[] { primaryKind };
        }
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var raw in aggregate.Split(','))
        {
            var trimmed = raw.Trim();
            if (trimmed.Length > 0)
                set.Add(trimmed);
        }
        if (set.Count == 0)
            return string.IsNullOrEmpty(primaryKind) ? Array.Empty<string>() : new[] { primaryKind };
        return set.ToArray();
    }

    public DbReader(SqliteConnection connection, bool isReadOnly = false)
    {
        _conn = connection;
        DbContext.RegisterConnectionFunctions(_conn);
        _isReadOnly = isReadOnly;
        _fileColumns = LoadColumns("files");
        _symbolColumns = LoadColumns("symbols");
        _referenceColumns = LoadColumns("symbol_references");
        _symbolIndexes = LoadIndexes("symbols");
        int userVersion;
        using (var v = _conn.CreateCommand())
        {
            v.CommandText = "PRAGMA user_version";
            var raw = v.ExecuteScalar();
            userVersion = raw is long l ? (int)l : (raw is int i ? i : 0);
        }
        _hasChunksTable = HasTable("chunks");
        _hasReferencesTable = HasTable("symbol_references") && (userVersion & DbContext.GraphReadyFlag) != 0;
        _hasIssuesTable = HasTable("file_issues") && (userVersion & DbContext.IssuesReadyFlag) != 0;
        _hasReferenceLinesTable = HasTable("reference_lines");
        _canUseReferenceLines = _hasReferencesTable && _hasReferenceLinesTable && _referenceColumns.Contains("reference_line_id");
        _referenceIndexes = LoadIndexes("symbol_references");
        _indexedHotspotFamilyLanguages = LoadIndexedHotspotFamilyLanguages();
        // #86/#97: require the FoldReady bit plus matching fold metadata before trusting
        // folded columns. version guards intentional NameFold changes; fingerprint guards
        // runtime ICU / invariant-casing drift across .NET upgrades. Missing metadata on
        // legacy / read-only DBs degrades safely to NOCASE until rebuild/backfill restamps current.
        // #86/#97: FoldReadyFlag に加え fold metadata 一致時のみ fold 経路を trusted とみなす。
        // version mismatch や fingerprint mismatch、未記録は NOCASE fallback に降格させる。
        var foldBitSet = (userVersion & DbContext.FoldReadyFlag) != 0
                         && _symbolColumns.Contains("name_folded");
        var storedFoldVersion = foldBitSet ? ParseFoldVersion(connection) : -1;
        var storedFoldFingerprint = foldBitSet ? ParseFoldFingerprint(connection) : null;
        _foldReady = foldBitSet
            && storedFoldVersion == NameFold.Version
            && string.Equals(storedFoldFingerprint, NameFold.Fingerprint(), StringComparison.Ordinal);
        _csharpSymbolNameContractCurrent = string.Equals(
            TryGetMetaString(_conn, DbContext.CSharpSymbolNameContractVersionMetaKey),
            DbContext.CSharpSymbolNameContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
        _csharpMetadataTargetReady = _symbolColumns.Contains("is_metadata_target")
            && string.Equals(
                TryGetMetaString(_conn, DbContext.GetMetadataTargetVersionMetaKey("csharp")),
                DbContext.MetadataTargetVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
        _sqlGraphContractCurrent = string.Equals(
            TryGetMetaString(_conn, DbContext.SqlGraphContractVersionMetaKey),
            DbContext.SqlGraphContractVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
        _hotspotFamilyReadyLanguages = LoadHotspotFamilyReadyLanguages(connection);
        // NOTE: row presence is intentionally NOT used as a fallback. A legacy DB or an
        // interrupted first-time / partial backfill can have one row while the rest of the
        // repo is untouched, which would flip trust on prematurely. Only an explicit
        // end-of-run readiness bit counts. Pre-upgrade DBs need a `cdidx index` re-run to
        // get stamped — degradation is safer than silent false-clean zeroes.
        // 行存在のフォールバックは意図的に採用しない。途中までのデータでも trusted に見えてしまうため。
    }

    internal HotspotFamilySignal GetHotspotFamilySignal(string? lang)
    {
        if (lang != null)
        {
            if (!FileIndexer.SupportsHotspotFamilyMarkerLanguage(lang) || !_indexedHotspotFamilyLanguages.Contains(lang))
                return new HotspotFamilySignal(Ready: true, Relevant: false, DegradedReason: null);

            var ready = _hotspotFamilyReadyLanguages.Contains(lang);
            return new HotspotFamilySignal(
                Ready: ready,
                Relevant: true,
                DegradedReason: ready
                    ? null
                    : $"cross-file hotspot family grouping for '{lang}' is degraded; run `cdidx index <projectPath>` to restamp authoritative hotspot families.");
        }

        var relevantLanguages = _indexedHotspotFamilyLanguages
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        if (relevantLanguages.Count == 0)
            return new HotspotFamilySignal(Ready: true, Relevant: false, DegradedReason: null);

        var unreadyLanguages = relevantLanguages
            .Where(language => !_hotspotFamilyReadyLanguages.Contains(language))
            .ToList();
        if (unreadyLanguages.Count == 0)
            return new HotspotFamilySignal(Ready: true, Relevant: true, DegradedReason: null);

        return new HotspotFamilySignal(
            Ready: false,
            Relevant: true,
            DegradedReason: $"cross-file hotspot family grouping is degraded for: {string.Join(", ", unreadyLanguages)}; run `cdidx index <projectPath>` to restamp authoritative hotspot families.");
    }

    private HashSet<string> LoadIndexes(string tableName)
    {
        var indexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!HasTable(tableName))
            return indexes;

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"PRAGMA index_list('{tableName.Replace("'", "''")}')";
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            if (!reader.IsDBNull(1))
                indexes.Add(reader.GetString(1));
        }
        return indexes;
    }

    private static int ParseFoldVersion(SqliteConnection conn)
    {
        var raw = TryGetMetaString(conn, "fold_key_version");
        if (raw is string s && int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v))
            return v;
        return -1;
    }

    private static string? ParseFoldFingerprint(SqliteConnection conn)
    {
        return TryGetMetaString(conn, "fold_key_fingerprint");
    }

    private string? ResolveFoldReadyReason()
    {
        if (_foldReady)
            return null;

        var storedVersion = ParseFoldVersion(_conn);
        var storedFingerprint = ParseFoldFingerprint(_conn);
        if (storedVersion < 0 || string.IsNullOrWhiteSpace(storedFingerprint))
            return "missing_fold_backfill";
        if (storedVersion != NameFold.Version)
            return "stale_fold_key_version";
        if (!string.Equals(storedFingerprint, NameFold.Fingerprint(), StringComparison.Ordinal))
            return "stale_fold_key_fingerprint";
        return "fold_rows_not_restamped";
    }

    private HashSet<string> LoadIndexedHotspotFamilyLanguages()
    {
        var langs = new HashSet<string>(StringComparer.Ordinal);
        var hotspotFamilyLangs = FileIndexer.GetHotspotFamilyMarkerLanguages();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT DISTINCT lang
            FROM files
            WHERE lang IN ({string.Join(",", hotspotFamilyLangs.Select((_, i) => $"@hfl{i}"))})";
        for (int i = 0; i < hotspotFamilyLangs.Count; i++)
            cmd.Parameters.AddWithValue($"@hfl{i}", hotspotFamilyLangs[i]);
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            if (!reader.IsDBNull(0))
                langs.Add(reader.GetString(0));
        }
        return langs;
    }

    private HashSet<string> LoadHotspotFamilyReadyLanguages(SqliteConnection conn)
    {
        var readyLangs = new HashSet<string>(StringComparer.Ordinal);
        if (!_symbolColumns.Contains("family_key") || !_symbolColumns.Contains("container_qualified_name"))
            return readyLangs;

        foreach (var lang in FileIndexer.GetHotspotFamilyMarkerLanguages())
        {
            var raw = TryGetMetaString(conn, DbContext.GetHotspotFamilyVersionMetaKey(lang));
            var fingerprint = TryGetMetaString(conn, DbContext.GetHotspotFamilyMarkerFingerprintMetaKey(lang));
            if (raw is string s
                && int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var version)
                && version == DbContext.HotspotFamilyVersion
                && !string.IsNullOrWhiteSpace(fingerprint))
            {
                readyLangs.Add(lang);
            }
        }

        return readyLangs;
    }

    private static string? TryGetMetaString(SqliteConnection conn, string key)
    {
        // Inline the codeindex_meta lookup to avoid creating a DbContext here.
        // codeindex_meta を直接引く（DbContext を new しない）。
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM codeindex_meta WHERE key = @key";
            cmd.Parameters.AddWithValue("@key", key);
            return cmd.ExecuteScalar() as string;
        }
        catch (SqliteException)
        {
            // codeindex_meta missing (legacy DB / read-only where migration skipped)
            // → treat as missing metadata so the reader falls back to NOCASE.
            return null;
        }
    }

    // Reference-count subquery that gracefully degrades to 0 when symbol_references is absent
    // (legacy read-only DBs where TryMigrateForRead could not create the table).
    // symbol_references が無いレガシー read-only DB では 0 にフォールバックする。
    private string ReferenceCountByFileSubquery =>
        _hasReferencesTable
            ? "(SELECT COUNT(*) FROM symbol_references WHERE file_id = f.id)"
            : "0";

    // C# top-level statements emit reference rows without a container symbol.
    // Graph readers should surface those rows as a synthetic `<top-level>` caller.
    // C# の top-level statements は container symbol なしの参照行を出すため、
    // graph reader では合成 `<top-level>` caller として扱う。
    private static string BuildCallerContainerPredicate(string fileAlias, string referenceAlias) =>
        $"({referenceAlias}.container_name IS NOT NULL OR ({fileAlias}.lang = 'csharp' AND {referenceAlias}.container_name IS NULL))";

    private static string BuildCallerKindProjectionSql(string referenceAlias) =>
        $"CASE WHEN {referenceAlias}.container_name IS NULL THEN '{SyntheticTopLevelCallerKind}' ELSE {referenceAlias}.container_kind END";

    private static string BuildCallerNameProjectionSql(string referenceAlias) =>
        $"CASE WHEN {referenceAlias}.container_name IS NULL THEN '{SyntheticTopLevelCallerName}' ELSE {referenceAlias}.container_name END";

    private bool HasTable(string tableName)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=@name";
        cmd.Parameters.AddWithValue("@name", tableName);
        return cmd.ExecuteScalar() != null;
    }

    private bool HasSymbolIndex(string indexName) => _symbolIndexes.Contains(indexName);
    private bool HasReferenceIndex(string indexName) => _referenceIndexes.Contains(indexName);

    private bool SymbolNameExactIndexAvailable =>
        _foldReady
            ? HasSymbolIndex("idx_symbols_name_folded")
            : HasSymbolIndex("idx_symbols_name_nocase");

    private bool SymbolNameExactGraphIndexAvailable =>
        _foldReady
            ? HasReferenceIndex("idx_symbol_refs_symbol_name_folded")
            : HasReferenceIndex("idx_symbol_refs_name_nocase");

    private bool ContainerNameExactGraphIndexAvailable =>
        _foldReady
            ? HasReferenceIndex("idx_symbol_refs_container_name_folded")
            : HasReferenceIndex("idx_symbol_refs_container_nocase");

    private string BuildExactGraphIndexReason(IEnumerable<string> missingIndexes)
    {
        var missing = missingIndexes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count == 0)
            return string.Empty;

        var scope = _isReadOnly ? "read-only legacy index" : "legacy index";
        return missing.Count == 1
            ? $"{missing[0]} missing on {scope}"
            : $"{string.Join(", ", missing)} missing on {scope}";
    }

    private ExactQuerySignal BuildExactGraphSignal(bool available, params string[] missingIndexes)
    {
        if (!_hasReferencesTable)
            return new(false, HasMissingIndex: false, HasMissingTable: true, "symbol_references table missing in this index");
        if (available)
            return new(true, HasMissingIndex: false, HasMissingTable: false, null);
        return new(false, HasMissingIndex: true, HasMissingTable: false, BuildExactGraphIndexReason(missingIndexes));
    }

    private ExactQuerySignal BuildExactSymbolSignal(bool available, params string[] missingIndexes)
    {
        if (available)
            return new(true, HasMissingIndex: false, HasMissingTable: false, null);
        return new(false, HasMissingIndex: true, HasMissingTable: false, BuildExactGraphIndexReason(missingIndexes));
    }

    private ExactQuerySignal? GetCSharpCanonicalNameExactQuerySignal(
        string? lang = null,
        IReadOnlyList<string>? pathPatterns = null,
        IReadOnlyList<string>? excludePathPatterns = null,
        bool excludeTests = false,
        DateTime? since = null)
    {
        if (_csharpSymbolNameContractCurrent)
            return null;

        if (!ScopeMayIncludeCSharpFiles(lang, pathPatterns, excludePathPatterns, excludeTests, since))
            return null;

        return new(
            ExactIndexAvailable: false,
            HasMissingIndex: false,
            HasMissingTable: false,
            DegradedReason: "csharp_symbol_name_ready=false (canonical C# operator / conversion operator / indexer / verbatim identifier names are stale in this DB)");
    }

    private ExactQuerySignal? GetSqlGraphContractExactQuerySignal(
        string? lang = null,
        IReadOnlyList<string>? pathPatterns = null,
        IReadOnlyList<string>? excludePathPatterns = null,
        bool excludeTests = false)
    {
        var signal = GetSqlGraphContractSignal(lang, pathPatterns, excludePathPatterns, excludeTests);
        if (!signal.Relevant || signal.Ready)
            return null;

        return new(
            ExactIndexAvailable: false,
            HasMissingIndex: false,
            HasMissingTable: false,
            DegradedReason: signal.DegradedReason);
    }

    private bool ScopeMayIncludeCSharpFiles(
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests,
        DateTime? since)
    {
        if (lang != null && !string.Equals(lang, "csharp", StringComparison.OrdinalIgnoreCase))
            return false;

        using var cmd = _conn.CreateCommand();
        var sql = "SELECT 1 FROM files f WHERE f.lang = 'csharp'";
        if (since != null && _fileColumns.Contains("modified"))
            sql += " AND f.modified >= @since";
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        sql += " LIMIT 1";

        cmd.CommandText = sql;
        if (since != null && _fileColumns.Contains("modified"))
            cmd.Parameters.AddWithValue("@since", since.Value);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        return cmd.ExecuteScalar() != null;
    }

    private bool ScopeMayIncludeSqlFiles(
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests)
    {
        if (lang != null && !IsSqlLanguage(lang))
            return false;

        using var cmd = _conn.CreateCommand();
        var sql = "SELECT 1 FROM files f WHERE f.lang = 'sql'";
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        sql += " LIMIT 1";

        cmd.CommandText = sql;
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        return cmd.ExecuteScalar() != null;
    }

    internal SqlGraphContractSignal GetSqlGraphContractSignal(
        string? lang = null,
        IReadOnlyList<string>? pathPatterns = null,
        IReadOnlyList<string>? excludePathPatterns = null,
        bool excludeTests = false)
    {
        if (!ScopeMayIncludeSqlFiles(lang, pathPatterns, excludePathPatterns, excludeTests))
            return new SqlGraphContractSignal(Ready: true, Relevant: false, DegradedReason: null);

        if (_sqlGraphContractCurrent)
            return new SqlGraphContractSignal(Ready: true, Relevant: true, DegradedReason: null);

        return new SqlGraphContractSignal(
            Ready: false,
            Relevant: true,
            DegradedReason: "sql_graph_contract_ready=false (SQL graph rows may still use a stale call-column / qualified-name contract; rerun `cdidx index <projectPath>` before trusting SQL graph/dependency results)");
    }

    private static ExactQuerySignal CombineExactSignals(params ExactQuerySignal?[] signals)
    {
        var participating = signals.Where(signal => signal.HasValue).Select(signal => signal!.Value).ToList();
        if (participating.Count == 0)
            return new(true, HasMissingIndex: false, HasMissingTable: false, null);

        if (participating.All(signal => signal.ExactIndexAvailable))
            return new(true, HasMissingIndex: false, HasMissingTable: false, null);

        var reasons = participating
            .Where(signal => !signal.ExactIndexAvailable && !string.IsNullOrWhiteSpace(signal.DegradedReason))
            .Select(signal => signal.DegradedReason!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new(
            ExactIndexAvailable: false,
            HasMissingIndex: participating.Any(signal => signal.HasMissingIndex),
            HasMissingTable: participating.Any(signal => signal.HasMissingTable),
            DegradedReason: reasons.Count == 0 ? null : string.Join("; ", reasons));
    }

    public ExactQuerySignal GetSymbolsExactQuerySignal(
        string? lang = null,
        IReadOnlyList<string>? pathPatterns = null,
        IReadOnlyList<string>? excludePathPatterns = null,
        bool excludeTests = false,
        DateTime? since = null)
        => CombineExactSignals(
            BuildExactSymbolSignal(SymbolNameExactIndexAvailable,
                _foldReady ? "idx_symbols_name_folded" : "idx_symbols_name_nocase"),
            GetCSharpCanonicalNameExactQuerySignal(lang, pathPatterns, excludePathPatterns, excludeTests, since));

    public ExactQuerySignal GetDefinitionExactQuerySignal(
        string? lang = null,
        IReadOnlyList<string>? pathPatterns = null,
        IReadOnlyList<string>? excludePathPatterns = null,
        bool excludeTests = false,
        DateTime? since = null)
        => GetSymbolsExactQuerySignal(lang, pathPatterns, excludePathPatterns, excludeTests, since);

    public ExactQuerySignal GetReferencesExactQuerySignal(
        string? lang = null,
        IReadOnlyList<string>? pathPatterns = null,
        IReadOnlyList<string>? excludePathPatterns = null,
        bool excludeTests = false,
        bool includeSqlGraphContractSignal = true)
        => CombineExactSignals(
            BuildExactGraphSignal(SymbolNameExactGraphIndexAvailable,
                _foldReady ? "idx_symbol_refs_symbol_name_folded" : "idx_symbol_refs_name_nocase"),
            GetCSharpCanonicalNameExactQuerySignal(lang, pathPatterns, excludePathPatterns, excludeTests),
            includeSqlGraphContractSignal ? GetSqlGraphContractExactQuerySignal(lang, pathPatterns, excludePathPatterns, excludeTests) : null);

    public ExactQuerySignal GetCallersExactQuerySignal(
        string? lang = null,
        IReadOnlyList<string>? pathPatterns = null,
        IReadOnlyList<string>? excludePathPatterns = null,
        bool excludeTests = false,
        bool includeSqlGraphContractSignal = true)
        => CombineExactSignals(
            BuildExactGraphSignal(SymbolNameExactGraphIndexAvailable,
                _foldReady ? "idx_symbol_refs_symbol_name_folded" : "idx_symbol_refs_name_nocase"),
            GetCSharpCanonicalNameExactQuerySignal(lang, pathPatterns, excludePathPatterns, excludeTests),
            includeSqlGraphContractSignal ? GetSqlGraphContractExactQuerySignal(lang, pathPatterns, excludePathPatterns, excludeTests) : null);

    public ExactQuerySignal GetCalleesExactQuerySignal(
        string? lang = null,
        IReadOnlyList<string>? pathPatterns = null,
        IReadOnlyList<string>? excludePathPatterns = null,
        bool excludeTests = false,
        bool includeSqlGraphContractSignal = true)
        => CombineExactSignals(
            BuildExactGraphSignal(ContainerNameExactGraphIndexAvailable,
                _foldReady ? "idx_symbol_refs_container_name_folded" : "idx_symbol_refs_container_nocase"),
            GetCSharpCanonicalNameExactQuerySignal(lang, pathPatterns, excludePathPatterns, excludeTests),
            includeSqlGraphContractSignal ? GetSqlGraphContractExactQuerySignal(lang, pathPatterns, excludePathPatterns, excludeTests) : null);

    public ExactQuerySignal GetAnalyzeSymbolExactQuerySignal(
        bool includeGraphSignal = true,
        bool includeSqlGraphContractSignal = true,
        string? lang = null,
        IReadOnlyList<string>? pathPatterns = null,
        IReadOnlyList<string>? excludePathPatterns = null,
        bool excludeTests = false)
    {
        return CombineExactSignals(
            GetDefinitionExactQuerySignal(lang, pathPatterns, excludePathPatterns, excludeTests),
            includeGraphSignal ? BuildAnalyzeGraphExactQuerySignal() : null,
            includeGraphSignal && includeSqlGraphContractSignal ? GetSqlGraphContractExactQuerySignal(lang, pathPatterns, excludePathPatterns, excludeTests) : null);
    }

    internal bool HasGraphApplicableFiles(string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false)
    {
        using var cmd = _conn.CreateCommand();

        var sql = @"
            SELECT 1
            FROM files f
            WHERE 1=1";
        sql += $" AND {BuildGraphSupportedLanguagePredicate(cmd, "f", "graphApplicableLang")}";
        if (lang != null)
            sql += " AND f.lang = @lang";
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        sql += " LIMIT 1";

        cmd.CommandText = sql;
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", NormalizeQueryLanguage(lang));
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);

        return cmd.ExecuteScalar() != null;
    }

    private ExactQuerySignal BuildAnalyzeGraphExactQuerySignal()
    {
        if (!_hasReferencesTable)
            return new(false, HasMissingIndex: false, HasMissingTable: true, "symbol_references table missing in this index");

        var missing = new List<string>();
        if (!SymbolNameExactGraphIndexAvailable)
            missing.Add(_foldReady ? "idx_symbol_refs_symbol_name_folded" : "idx_symbol_refs_name_nocase");
        if (!ContainerNameExactGraphIndexAvailable)
            missing.Add(_foldReady ? "idx_symbol_refs_container_name_folded" : "idx_symbol_refs_container_nocase");

        return missing.Count == 0
            ? new(true, HasMissingIndex: false, HasMissingTable: false, null)
            : new(false, HasMissingIndex: true, HasMissingTable: false, BuildExactGraphIndexReason(missing));
    }

    internal static string EscapeLikeQuery(string input)
    {
        return input.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    }

    internal static string BuildPathLikePattern(string input)
    {
        var hasWildcard = false;
        var builder = new StringBuilder(input.Length + 2);

        foreach (var ch in input)
        {
            switch (ch)
            {
                case '*':
                    builder.Append('%');
                    hasWildcard = true;
                    break;
                case '?':
                    builder.Append('_');
                    hasWildcard = true;
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '%':
                    builder.Append("\\%");
                    break;
                case '_':
                    builder.Append("\\_");
                    break;
                default:
                    builder.Append(ch);
                    break;
            }
        }

        var pattern = builder.ToString();
        return hasWildcard ? pattern : $"%{pattern}%";
    }

    internal static bool IsSqlLanguage(string? lang)
        => string.Equals(NormalizeQueryLanguage(lang), "sql", StringComparison.OrdinalIgnoreCase);

    internal static string? NormalizeQueryLanguage(string? lang)
    {
        if (lang == null)
            return null;

        var normalized = NormalizeQueryLanguageKey(lang);
        return QueryLanguageAliases.TryGetValue(normalized, out var canonical)
            ? canonical
            : normalized;
    }

    internal static bool ContainsSqlLanguage(IEnumerable<string?> langs)
        => langs.Any(IsSqlLanguage);

    private static IReadOnlyDictionary<string, string> BuildQueryLanguageAliases()
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (pattern, lang) in FileIndexer.GetLanguageExtensions())
            AddQueryLanguageAlias(aliases, pattern, lang);

        AddQueryLanguageAlias(aliases, "c#", "csharp");
        AddQueryLanguageAlias(aliases, "c++", "cpp");
        AddQueryLanguageAlias(aliases, "cplusplus", "cpp");
        AddQueryLanguageAlias(aliases, "f#", "fsharp");
        AddQueryLanguageAlias(aliases, "jav", "java");
        AddQueryLanguageAlias(aliases, "python3", "python");
        AddQueryLanguageAlias(aliases, "py3", "python");
        AddQueryLanguageAlias(aliases, "vb.net", "vb");
        AddQueryLanguageAlias(aliases, "vbnet", "vb");
        AddQueryLanguageAlias(aliases, "visual basic", "vb");
        AddQueryLanguageAlias(aliases, "visualbasic", "vb");
        AddQueryLanguageAlias(aliases, "sqlserver", "sql");
        AddQueryLanguageAlias(aliases, "mssql", "sql");
        AddQueryLanguageAlias(aliases, "transactsql", "sql");

        return aliases;
    }

    private static void AddQueryLanguageAlias(IDictionary<string, string> aliases, string alias, string canonical)
        => aliases.TryAdd(NormalizeQueryLanguageKey(alias), canonical);

    private static string NormalizeQueryLanguageKey(string lang)
    {
        var builder = new StringBuilder(lang.Length);
        foreach (var ch in lang.Trim())
        {
            if (char.IsWhiteSpace(ch) || ch is '-' or '_' or '.')
                continue;

            builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }

    private static bool AllowSqlLeafFallbackForQuery(string query)
        => !SqlNameResolver.HasQualifier(query);

    private static string BuildGraphSupportedLanguagePredicate(SqliteCommand cmd, string fileAlias, string parameterPrefix)
    {
        var supportedLanguages = ReferenceExtractor.GetSupportedLanguages()
            .OrderBy(lang => lang, StringComparer.Ordinal)
            .ToList();
        if (supportedLanguages.Count == 0)
            return "1 = 0";

        var parameterNames = new List<string>(supportedLanguages.Count);
        for (int i = 0; i < supportedLanguages.Count; i++)
        {
            var parameterName = $"@{parameterPrefix}{i}";
            parameterNames.Add(parameterName);
            cmd.Parameters.AddWithValue(parameterName, supportedLanguages[i]);
        }

        return $"{fileAlias}.lang IN ({string.Join(", ", parameterNames)})";
    }

    private static QueryCountResult ExecuteCountSummary(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteTrackedReader();
        return reader.TrackedRead()
            ? new QueryCountResult(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.FieldCount > 2 && !reader.IsDBNull(2) && Convert.ToInt32(reader.GetValue(2)) != 0)
            : new QueryCountResult(0, 0);
    }

    internal bool AnyFilePathHasLanguage(IEnumerable<string> paths, string lang)
    {
        var distinctPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .Take(256)
            .ToList();
        if (distinctPaths.Count == 0)
            return false;

        using var cmd = _conn.CreateCommand();
        var placeholders = new List<string>(distinctPaths.Count);
        for (int i = 0; i < distinctPaths.Count; i++)
        {
            var parameterName = $"@sqlPath{i}";
            placeholders.Add(parameterName);
            cmd.Parameters.AddWithValue(parameterName, distinctPaths[i]);
        }

        cmd.CommandText = $"""
            SELECT 1
            FROM files
            WHERE lang = @sqlPathLang
              AND path IN ({string.Join(", ", placeholders)})
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@sqlPathLang", lang);
        return cmd.ExecuteScalar() != null;
    }

    /// <summary>
    /// List indexed files, optionally filtered by name pattern and language.
    /// インデックス済みファイルを一覧（名前パターン・言語でフィルタ可能）。
    /// </summary>
    public List<FileResult> ListFiles(string? query = null, int limit = 20, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, DateTime? since = null)
    {
        lang = NormalizeQueryLanguage(lang);
        using var cmd = _conn.CreateCommand();

        var sql = $@"
            SELECT f.path, f.lang, f.size, f.lines,
                   (SELECT COUNT(*) FROM symbols WHERE file_id = f.id) AS symbol_count,
                   {ReferenceCountByFileSubquery} AS reference_count,
                   {GetFileColumnSql("checksum")} AS checksum,
                   {GetFileColumnSql("modified")} AS modified,
                   {GetFileColumnSql("indexed_at")} AS indexed_at
            FROM files f
            WHERE 1=1";

        if (query != null)
            sql += " AND f.path LIKE @query ESCAPE '\\'";
        if (lang != null)
            sql += " AND f.lang = @lang";
        if (since != null && _fileColumns.Contains("modified"))
            sql += " AND f.modified >= @since";
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        sql += $" ORDER BY {PathBucketOrder}, f.path LIMIT @limit";

        cmd.CommandText = sql;
        if (query != null)
            cmd.Parameters.AddWithValue("@query", $"%{EscapeLikeQuery(query)}%");
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", lang);
        if (since != null && _fileColumns.Contains("modified"))
            cmd.Parameters.AddWithValue("@since", since.Value);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<FileResult>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            results.Add(new FileResult
            {
                Path = reader.GetString(0),
                Lang = GetNullableString(reader, 1),
                Size = reader.GetInt64(2),
                Lines = reader.GetInt32(3),
                SymbolCount = reader.GetInt32(4),
                ReferenceCount = reader.GetInt32(5),
                Checksum = GetNullableString(reader, 6),
                Modified = GetNullableDateTime(reader, 7),
                IndexedAt = GetNullableDateTime(reader, 8),
            });
        }
        return results;
    }

    public QueryCountResult CountListFiles(string? query = null, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, DateTime? since = null)
    {
        lang = NormalizeQueryLanguage(lang);
        using var cmd = _conn.CreateCommand();

        var sql = @"
            SELECT COUNT(*), COUNT(DISTINCT path)
            FROM (
                SELECT f.path AS path
                FROM files f
                WHERE 1=1";

        if (query != null)
            sql += " AND f.path LIKE @query ESCAPE '\\'";
        if (lang != null)
            sql += " AND f.lang = @lang";
        if (since != null && _fileColumns.Contains("modified"))
            sql += " AND f.modified >= @since";
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        sql += ")";

        cmd.CommandText = sql;
        if (query != null)
            cmd.Parameters.AddWithValue("@query", $"%{EscapeLikeQuery(query)}%");
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", lang);
        if (since != null && _fileColumns.Contains("modified"))
            cmd.Parameters.AddWithValue("@since", since.Value);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);

        return ExecuteCountSummary(cmd);
    }

    /// <summary>
    /// Search indexed references such as call sites.
    /// 呼び出し箇所などのインデックス済み参照を検索する。
    /// </summary>
    public List<ReferenceResult> SearchReferences(string? query = null, int limit = 20, string? lang = null, string? referenceKind = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false, int maxLineWidth = LineWidthFormatter.DefaultMaxLineWidth)
    {
        maxLineWidth = LineWidthFormatter.ClampMaxLineWidth(maxLineWidth);
        lang = NormalizeQueryLanguage(lang);
        query = NormalizeSymbolSearchQuery(query, lang, exact) ?? query ?? string.Empty;
        if (!_hasReferencesTable)
            return new List<ReferenceResult>();

        if (!ShouldApplyCSharpUsingStaticConstantPatternReferenceFilter(lang, referenceKind, exact))
            return SearchReferencesCore(query, limit, lang, referenceKind, pathPatterns, excludePathPatterns, excludeTests, exact, 0, maxLineWidth);

        var rawLimit = Math.Max(limit, CSharpUsingStaticReferenceFilterChunkSize);
        var rawOffset = 0;
        var filtered = new List<ReferenceResult>();
        while (filtered.Count < limit)
        {
            var rawResults = SearchReferencesCore(query, rawLimit, lang, referenceKind, pathPatterns, excludePathPatterns, excludeTests, exact, rawOffset, maxLineWidth);
            if (rawResults.Count == 0)
                break;

            foreach (var result in rawResults)
            {
                if (ShouldSuppressCSharpUsingStaticConstantPatternReference(result))
                    continue;

                filtered.Add(result);
                if (filtered.Count >= limit)
                    break;
            }

            if (rawResults.Count < rawLimit || filtered.Count >= limit)
                break;

            rawOffset += rawResults.Count;
            rawLimit = Math.Min(rawLimit * 2, CSharpUsingStaticReferenceFilterMaxRawLimit);
        }

        return filtered.Count <= limit ? filtered : filtered.Take(limit).ToList();
    }

    private List<ReferenceResult> SearchReferencesCore(string? query, int limit, string? lang, string? referenceKind, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, bool exact, int offset, int maxLineWidth)
    {
        using var cmd = CreateSearchReferencesCommand(query, limit, lang, referenceKind, pathPatterns, excludePathPatterns, excludeTests, exact, offset);
        var results = new List<ReferenceResult>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            var row = ReadSearchReferenceRawRow(reader);
            var clampedContext = LineWidthFormatter.ClampLine(row.Context, maxLineWidth, row.Column, query?.Length ?? 1);
            results.Add(new ReferenceResult
            {
                Path = row.Path,
                Lang = row.Lang,
                SymbolName = row.SymbolName,
                ReferenceKind = row.ReferenceKind,
                Line = row.Line,
                Column = row.Column,
                RawContext = row.Context,
                Context = clampedContext.Text,
                ContextTruncated = clampedContext.Truncated,
                ContainerKind = row.ContainerKind,
                ContainerName = row.ContainerName,
            });
        }
        return results;
    }

    private SqliteCommand CreateSearchReferencesCommand(string? query, int limit, string? lang, string? referenceKind, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, bool exact, int offset = 0, bool includeOrdering = true)
    {
        var cmd = _conn.CreateCommand();
        var referenceLineJoin = ReferenceLineJoinSql("r");
        var contextSql = ReferenceContextSql("r");
        var sql = referenceKind == null
            ? $@"
            WITH logical_references AS (
                SELECT f.path, f.lang, r.symbol_name,
                       {GetPreferredReferenceKindSql("r.reference_kind")} AS reference_kind,
                       r.line, r.column_number,
                       MIN({contextSql}) AS context,
                       CASE WHEN COUNT(DISTINCT COALESCE(r.container_kind, '')) = 1 THEN MIN(r.container_kind) ELSE NULL END AS container_kind,
                       CASE WHEN COUNT(DISTINCT COALESCE(r.container_name, '')) = 1 THEN MIN(r.container_name) ELSE NULL END AS container_name
                FROM symbol_references r
                JOIN files f ON r.file_id = f.id
                {referenceLineJoin}
                WHERE 1=1
                  AND {BuildGraphSupportedLanguagePredicate(cmd, "f", "graphLang")}"
            : @"
            SELECT f.path, f.lang, r.symbol_name, r.reference_kind, r.line, r.column_number,
                   " + contextSql + @", r.container_kind, r.container_name
            FROM symbol_references r
            JOIN files f ON r.file_id = f.id
            " + referenceLineJoin + @"
            WHERE 1=1";

        if (referenceKind != null)
            sql += $" AND {BuildGraphSupportedLanguagePredicate(cmd, "f", "graphLang")}";

        var referencesSuffixAlias = ComputeCSharpAttributeSuffixAlias(query, lang, referenceKind);
        var referencesAliasScope = referencesSuffixAlias != null
            ? " AND f.lang = 'csharp' AND r.reference_kind = 'attribute'"
            : string.Empty;
        var referencesCssScssVariableAlias = ComputeCssScssVariableAlias(query);
        var referencesCssScssVariableAliasScope = referencesCssScssVariableAlias != null
            ? " AND f.lang = 'css'"
            : string.Empty;
        const string sqlLeafReferenceScope = " AND f.lang = 'sql'";
        if (query != null)
        {
            var allowSqlLeafFallback = AllowSqlLeafFallbackForQuery(query);
            var useSqlQualifiedContextMatch = SqlNameResolver.HasQualifier(query);
            // --exact: Unicode-aware equality when FoldReady (#86), else ASCII COLLATE NOCASE.
            // Fold path: r.symbol_name_folded = @qFolded (indexed), query pre-folded in .NET.
            // Fallback: r.symbol_name = @q COLLATE NOCASE (indexed by idx_symbol_refs_name_nocase).
            // When the query ends with C# attribute suffix `Attribute`, also OR against the
            // suffix-stripped alias so `references FooAttribute --exact` reaches the idiomatic
            // `[Foo]` reference site stored with `symbol_name = "Foo"`. In substring mode we
            // still LIKE-match `%FooAttribute%` and add only the exact stripped alias to avoid
            // overmatching unrelated names (e.g. `FooAuditLog`) that share the stripped prefix.
            // The alias disjunct is scoped to C# attribute rows to avoid false positives.
            // --exact: FoldReady なら Unicode 折り畳み経路、未 ready なら ASCII NOCASE へ fallback。
            // C# の `Attribute` suffix が付いたクエリは、suffix を外した別名とも照合する。
            // 部分一致モードでは `%FooAttribute%` をそのまま使い、別名側は exact 照合だけを OR
            // することで `FooAuditLog` など無関係な名前を巻き込まないようにする。
            // 別名節は C# の attribute 行に限定し、誤一致を避ける。
            if (useSqlQualifiedContextMatch && exact && _foldReady)
                sql += referencesSuffixAlias != null
                    ? $" AND (((f.lang = 'sql') AND sql_context_has_name_folded_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND (r.symbol_name_folded = @query OR (r.symbol_name_folded = @queryAttributeAlias{referencesAliasScope}))))"
                    : $" AND (((f.lang = 'sql') AND sql_context_has_name_folded_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name_folded = @query))";
            else if (useSqlQualifiedContextMatch && exact)
                sql += referencesSuffixAlias != null
                    ? $" AND (((f.lang = 'sql') AND sql_context_has_name_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND (r.symbol_name = @query COLLATE NOCASE OR (r.symbol_name = @queryAttributeAlias COLLATE NOCASE{referencesAliasScope}))))"
                    : $" AND (((f.lang = 'sql') AND sql_context_has_name_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name = @query COLLATE NOCASE))";
            else if (useSqlQualifiedContextMatch && _foldReady)
                sql += referencesSuffixAlias != null
                    ? $" AND (((f.lang = 'sql') AND sql_context_like_name_folded_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND (r.symbol_name LIKE @query ESCAPE '\\' OR (r.symbol_name = @queryAttributeAlias COLLATE NOCASE{referencesAliasScope}))))"
                    : $" AND (((f.lang = 'sql') AND sql_context_like_name_folded_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name LIKE @query ESCAPE '\\'))";
            else if (useSqlQualifiedContextMatch)
                sql += referencesSuffixAlias != null
                    ? $" AND (((f.lang = 'sql') AND sql_context_like_name_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND (r.symbol_name LIKE @query ESCAPE '\\' OR (r.symbol_name = @queryAttributeAlias COLLATE NOCASE{referencesAliasScope}))))"
                    : $" AND (((f.lang = 'sql') AND sql_context_like_name_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name LIKE @query ESCAPE '\\'))";
            else if (exact && _foldReady)
                sql += referencesSuffixAlias != null
                    ? $" AND (r.symbol_name_folded = @query OR (r.symbol_name_folded = @queryAttributeAlias{referencesAliasScope}){(allowSqlLeafFallback ? $" OR (r.symbol_name_folded = @aliasQueryLeafFolded{sqlLeafReferenceScope})" : string.Empty)})"
                    : referencesCssScssVariableAlias != null
                        ? $" AND (r.symbol_name_folded = @query OR (r.symbol_name_folded = @queryCssScssVariableAlias{referencesCssScssVariableAliasScope}){(allowSqlLeafFallback ? $" OR (r.symbol_name_folded = @aliasQueryLeafFolded{sqlLeafReferenceScope})" : string.Empty)})"
                        : allowSqlLeafFallback
                        ? $" AND (r.symbol_name_folded = @query OR (r.symbol_name_folded = @aliasQueryLeafFolded{sqlLeafReferenceScope}))"
                        : " AND r.symbol_name_folded = @query";
            else if (exact)
                sql += referencesSuffixAlias != null
                    ? $" AND (r.symbol_name = @query COLLATE NOCASE OR (r.symbol_name = @queryAttributeAlias COLLATE NOCASE{referencesAliasScope}){(allowSqlLeafFallback ? $" OR (r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE{sqlLeafReferenceScope})" : string.Empty)})"
                    : referencesCssScssVariableAlias != null
                        ? $" AND (r.symbol_name = @query COLLATE NOCASE OR (r.symbol_name = @queryCssScssVariableAlias COLLATE NOCASE{referencesCssScssVariableAliasScope}){(allowSqlLeafFallback ? $" OR (r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE{sqlLeafReferenceScope})" : string.Empty)})"
                        : allowSqlLeafFallback
                        ? $" AND (r.symbol_name = @query COLLATE NOCASE OR (r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE{sqlLeafReferenceScope}))"
                        : " AND r.symbol_name = @query COLLATE NOCASE";
            else
                sql += referencesSuffixAlias != null
                    ? $" AND (r.symbol_name LIKE @query ESCAPE '\\' OR (r.symbol_name = @queryAttributeAlias COLLATE NOCASE{referencesAliasScope}) OR (r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE{sqlLeafReferenceScope}))"
                    : referencesCssScssVariableAlias != null
                        ? $" AND (r.symbol_name LIKE @query ESCAPE '\\' OR (r.symbol_name = @queryCssScssVariableAlias COLLATE NOCASE{referencesCssScssVariableAliasScope}) OR (r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE{sqlLeafReferenceScope}))"
                        : $" AND (r.symbol_name LIKE @query ESCAPE '\\' OR (r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE{sqlLeafReferenceScope}))";
        }
        if (referenceKind != null)
            sql += " AND r.reference_kind = @referenceKind";
        if (lang != null)
            sql += " AND f.lang = @lang";
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        if (referenceKind == null)
        {
            sql += @"
                GROUP BY f.path, f.lang, r.file_id, r.symbol_name, r.line, r.column_number, " + GetLogicalReferenceKindSql("r.reference_kind") + @"
            )
            SELECT path, lang, symbol_name, reference_kind, line, column_number,
                   context, container_kind, container_name
            FROM logical_references r";
        }
        if (includeOrdering)
            sql += $" ORDER BY CASE WHEN @preferExactCase = 1 AND r.symbol_name = @rawQuery THEN 0 ELSE 1 END, {(referenceKind == null ? GetPathBucketOrderSql("r.path") : PathBucketOrder)}, CASE WHEN lower(r.symbol_name) = lower(@rankingQuery) THEN 0 ELSE 1 END, CASE WHEN lower(r.symbol_name) LIKE lower(@rankingQueryPrefix) ESCAPE '\\' THEN 0 ELSE 1 END, {(referenceKind == null ? "r.path" : "f.path")}, r.line, r.column_number, r.reference_kind, r.symbol_name LIMIT @limit OFFSET @offset";

        cmd.CommandText = sql;
        if (query != null)
        {
            string queryParam;
            if (!exact)
                queryParam = $"%{EscapeLikeQuery(query)}%";
            else if (_foldReady)
                queryParam = NameFold.Fold(query) ?? query;
            else
                queryParam = query;
            cmd.Parameters.AddWithValue("@query", queryParam);
            cmd.Parameters.AddWithValue("@aliasQuery", query);
            cmd.Parameters.AddWithValue("@aliasQueryLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(query)) ?? SqlNameResolver.GetLeafName(query));
            if (referencesSuffixAlias != null)
            {
                var aliasParam = exact && _foldReady
                    ? NameFold.Fold(referencesSuffixAlias) ?? referencesSuffixAlias
                    : referencesSuffixAlias;
                cmd.Parameters.AddWithValue("@queryAttributeAlias", aliasParam);
            }
            if (referencesCssScssVariableAlias != null)
            {
                var aliasParam = exact && _foldReady
                    ? NameFold.Fold(referencesCssScssVariableAlias) ?? referencesCssScssVariableAlias
                    : referencesCssScssVariableAlias;
                cmd.Parameters.AddWithValue("@queryCssScssVariableAlias", aliasParam);
            }
            cmd.Parameters.AddWithValue("@rankingQuery", query.Trim());
            cmd.Parameters.AddWithValue("@rankingQueryPrefix", $"{EscapeLikeQuery(query.Trim())}%");
        }
        else
        {
            cmd.Parameters.AddWithValue("@rankingQuery", "");
            cmd.Parameters.AddWithValue("@rankingQueryPrefix", "%");
        }
        cmd.Parameters.AddWithValue("@preferExactCase", exact && query != null ? 1 : 0);
        cmd.Parameters.AddWithValue("@rawQuery", exact && query != null ? query : string.Empty);
        if (referenceKind != null)
            cmd.Parameters.AddWithValue("@referenceKind", referenceKind);
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", NormalizeQueryLanguage(lang));
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        if (includeOrdering)
        {
            cmd.Parameters.AddWithValue("@limit", limit);
            cmd.Parameters.AddWithValue("@offset", offset);
        }
        return cmd;
    }

    private static SearchReferenceRawRow ReadSearchReferenceRawRow(SqliteDataReader reader)
    {
        return new SearchReferenceRawRow(
            reader.GetString(0),
            GetNullableString(reader, 1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetString(6),
            GetNullableString(reader, 7),
            GetNullableString(reader, 8));
    }

    private static bool ShouldApplyCSharpUsingStaticConstantPatternReferenceFilter(string? lang, string? referenceKind, bool exact) =>
        exact
        &&
        (lang == null || string.Equals(lang, "csharp", StringComparison.Ordinal))
        && (referenceKind == null
            || string.Equals(referenceKind, "type_reference", StringComparison.Ordinal)
            || string.Equals(referenceKind, "call", StringComparison.Ordinal));

    private bool ShouldSuppressCSharpUsingStaticConstantPatternReference(ReferenceResult result)
    {
        var contextForFilter = string.IsNullOrWhiteSpace(result.RawContext)
            ? result.Context
            : result.RawContext;
        return ShouldSuppressCSharpUsingStaticConstantPatternReference(
            result.Path,
            result.Lang,
            result.SymbolName,
            result.ReferenceKind,
            result.Line,
            result.Column,
            contextForFilter);
    }

    private bool ShouldSuppressCSharpUsingStaticConstantPatternReference(SearchReferenceRawRow row)
    {
        return ShouldSuppressCSharpUsingStaticConstantPatternReference(
            row.Path,
            row.Lang,
            row.SymbolName,
            row.ReferenceKind,
            row.Line,
            row.Column,
            row.Context);
    }

    private bool ShouldSuppressCSharpUsingStaticConstantPatternReference(string path, string? lang, string symbolName, string referenceKind, int lineNumber, int columnNumber, string contextForFilter)
    {
        if (!string.Equals(lang, "csharp", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(symbolName)
            || string.IsNullOrWhiteSpace(contextForFilter)
            || symbolName.IndexOf('.') >= 0
            || symbolName.IndexOf(':') >= 0
            || symbolName.IndexOf('<') >= 0
            || symbolName.IndexOf('[') >= 0
            || symbolName.IndexOf(' ') >= 0)
        {
            return false;
        }

        if (HasActiveCSharpUsingTypeAlias(path, lineNumber, symbolName))
            return false;

        var patternContext = contextForFilter;
        var patternColumn = columnNumber;
        if (!TryBuildCSharpUsingStaticPatternContextWindow(
                path,
                lineNumber,
                contextForFilter,
                columnNumber,
                symbolName,
                out patternContext,
                out patternColumn))
        {
            return false;
        }

        if (ShouldSuppressCSharpQualifiedConstantPatternReference(path, lineNumber, symbolName, patternContext, patternColumn, referenceKind))
            return true;

        if (!string.Equals(referenceKind, "type_reference", StringComparison.Ordinal))
            return false;

        var activeTargets = GetActiveCSharpUsingStaticTargets(path, lineNumber);
        if (activeTargets.Count == 0)
            return false;

        var matchingContainers = GetCSharpConstantPatternContainersByMemberName(symbolName);
        if (matchingContainers.Count == 0)
            return false;

        if (HasScopedCSharpTypeCandidate(path, lineNumber, symbolName))
            return false;

        foreach (var target in activeTargets)
        {
            if (matchingContainers.Contains(target))
                return true;
        }

        return false;
    }

    private bool ShouldSuppressCSharpQualifiedConstantPatternReference(string path, int lineNumber, string symbolName, string patternContext, int patternColumn, string referenceKind)
    {
        if (!TryExtractQualifiedCSharpPatternQualifier(patternContext, symbolName, patternColumn, out var qualifier, out var anchorKind))
            return false;

        // Exact `call` suppression only applies to `case` constant patterns; `is` patterns
        // keep their preserved call row so qualified `is` expressions remain visible.
        // exact の `call` 抑制は `case` 定数パターンのみに限定する。`is` パターンは
        // preserved call row を維持し、qualified な `is` 式を可視のまま残す。
        if (string.Equals(referenceKind, "call", StringComparison.Ordinal)
            && !string.Equals(anchorKind, "case", StringComparison.Ordinal))
        {
            return false;
        }

        var matchingContainers = GetCSharpConstantPatternContainersByMemberName(symbolName);
        if (matchingContainers.Count == 0)
            return false;

        foreach (var candidate in GetScopedCSharpQualifiedPatternQualifierCandidates(path, lineNumber, qualifier))
        {
            if (matchingContainers.Contains(candidate))
                return true;
        }

        return false;
    }

    private bool HasScopedCSharpTypeCandidate(string path, int lineNumber, string symbolName)
    {
        if (HasActiveCSharpUsingTypeAlias(path, lineNumber, symbolName))
            return true;

        var activeAliasReference = ResolveActiveCSharpUsingAliasReference(path, lineNumber, symbolName);
        if (!string.Equals(activeAliasReference, symbolName, StringComparison.Ordinal)
            && IsKnownCSharpTypeQualifiedName(activeAliasReference))
        {
            return true;
        }

        var candidateNamespaces = GetCSharpTypeNamespacesByName(symbolName);
        var candidateContainingTypes = GetCSharpTypeContainingTypesByName(symbolName);
        if (candidateNamespaces.Count == 0 && candidateContainingTypes.Count == 0)
            return false;

        var activeNamespaces = GetActiveCSharpTypeNamespaces(path, lineNumber);
        foreach (var activeNamespace in activeNamespaces)
        {
            foreach (var candidateNamespace in candidateNamespaces)
            {
                if (!string.Equals(candidateNamespace.QualifiedName, activeNamespace, StringComparison.Ordinal))
                    continue;
                if (candidateNamespace.IsFileLocal && !string.Equals(candidateNamespace.Path, path, StringComparison.Ordinal))
                    continue;
                return true;
            }
        }

        var activeContainingTypeScopes = GetActiveCSharpContainingTypeScopes(path, lineNumber);
        foreach (var activeContainingTypeScope in activeContainingTypeScopes)
        {
            if (candidateContainingTypes.Any(candidate => string.Equals(candidate.QualifiedName, activeContainingTypeScope.QualifiedName, StringComparison.Ordinal)))
                return true;

            if (!candidateContainingTypes.Any(candidate => candidate.AccessibleFromDerivedType))
                continue;

            var inheritedContainingTypes = GetInheritedCSharpContainingTypes(activeContainingTypeScope);
            foreach (var candidate in candidateContainingTypes)
            {
                if (!candidate.AccessibleFromDerivedType)
                    continue;
                if (!inheritedContainingTypes.Contains(candidate.QualifiedName))
                    continue;
                return true;
            }
        }

        return false;
    }

    private HashSet<string> GetActiveCSharpTypeNamespaces(string path, int lineNumber)
    {
        if (!_csharpNamespaceScopesByPath.TryGetValue(path, out var namespaceScopes))
        {
            namespaceScopes = LoadCSharpNamespaceScopes(path);
            _csharpNamespaceScopesByPath[path] = namespaceScopes;
        }

        if (!_csharpUsingNamespaceScopesByPath.TryGetValue(path, out var usingNamespaceScopes))
        {
            usingNamespaceScopes = LoadCSharpUsingNamespaceScopes(path);
            _csharpUsingNamespaceScopesByPath[path] = usingNamespaceScopes;
        }

        var activeNamespaces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scope in namespaceScopes)
        {
            if (lineNumber >= scope.ScopeStartLine && lineNumber <= scope.ScopeEndLine)
                activeNamespaces.Add(scope.QualifiedName);
        }

        if (activeNamespaces.Count == 0)
            activeNamespaces.Add(string.Empty);

        foreach (var scope in usingNamespaceScopes)
        {
            if (scope.Line > lineNumber)
                continue;
            if (lineNumber < scope.ScopeStartLine || lineNumber > scope.ScopeEndLine)
                continue;
            activeNamespaces.Add(scope.TargetQualifiedName);
        }

        foreach (var globalNamespace in GetGlobalCSharpUsingNamespaces())
            activeNamespaces.Add(globalNamespace);

        return activeNamespaces;
    }

    private List<CSharpContainingTypeScope> GetActiveCSharpContainingTypeScopes(string path, int lineNumber)
    {
        if (!_csharpContainingTypeScopesByPath.TryGetValue(path, out var containingTypeScopes))
        {
            containingTypeScopes = LoadCSharpContainingTypeScopes(path);
            _csharpContainingTypeScopesByPath[path] = containingTypeScopes;
        }

        var activeContainingTypes = new List<CSharpContainingTypeScope>();
        foreach (var scope in containingTypeScopes)
        {
            if (lineNumber >= scope.ScopeStartLine && lineNumber <= scope.ScopeEndLine)
                activeContainingTypes.Add(scope);
        }

        return activeContainingTypes;
    }

    private HashSet<string> GetInheritedCSharpContainingTypes(CSharpContainingTypeScope containingTypeScope)
    {
        if (_csharpInheritedContainingTypesByQualifiedName.TryGetValue(containingTypeScope.QualifiedName, out var cached))
            return cached;

        var inheritedContainingTypes = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal)
        {
            containingTypeScope.QualifiedName,
        };
        CollectInheritedCSharpContainingTypes(containingTypeScope, inheritedContainingTypes, visited);
        _csharpInheritedContainingTypesByQualifiedName[containingTypeScope.QualifiedName] = inheritedContainingTypes;
        return inheritedContainingTypes;
    }

    private void CollectInheritedCSharpContainingTypes(CSharpContainingTypeScope containingTypeScope, HashSet<string> inheritedContainingTypes, HashSet<string> visited)
    {
        var directBaseScope = ResolveDirectCSharpBaseContainingTypeScope(containingTypeScope);
        if (directBaseScope == null || !visited.Add(directBaseScope.QualifiedName))
            return;

        inheritedContainingTypes.Add(directBaseScope.QualifiedName);
        CollectInheritedCSharpContainingTypes(directBaseScope, inheritedContainingTypes, visited);
    }

    private CSharpContainingTypeScope? GetCSharpContainingTypeScope(string qualifiedName)
    {
        if (_csharpContainingTypeScopeByQualifiedName.TryGetValue(qualifiedName, out var cached))
            return cached;

        var lastDot = qualifiedName.LastIndexOf('.');
        var shortName = lastDot >= 0 ? qualifiedName[(lastDot + 1)..] : qualifiedName;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT f.path, s.kind, s.name, s.container_name, s.container_qualified_name, s.visibility, s.signature, s.body_start_line, s.body_end_line, s.start_line, s.end_line
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.lang = 'csharp'
              AND s.name = @symbolName COLLATE NOCASE
              AND s.kind IN ('class', 'struct', 'interface')";
        cmd.Parameters.AddWithValue("@symbolName", shortName);

        CSharpContainingTypeScope? resolved = null;
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            var scope = CreateCSharpContainingTypeScope(
                reader.GetString(0),
                GetNullableString(reader, 1),
                GetNullableString(reader, 2),
                GetNullableString(reader, 3),
                GetNullableString(reader, 4),
                GetNullableString(reader, 5),
                GetNullableString(reader, 6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetInt32(9),
                reader.IsDBNull(10) ? null : reader.GetInt32(10));
            if (scope == null)
                continue;
            if (!string.Equals(scope.QualifiedName, qualifiedName, StringComparison.Ordinal))
                continue;
            resolved = scope;
            break;
        }

        _csharpContainingTypeScopeByQualifiedName[qualifiedName] = resolved;
        return resolved;
    }

    private bool IsKnownCSharpTypeQualifiedName(string qualifiedName)
    {
        var normalizedQualifiedName = NormalizeCSharpAliasTargetForTypeLookup(qualifiedName);
        if (string.IsNullOrWhiteSpace(normalizedQualifiedName))
            return false;

        var lastDot = normalizedQualifiedName.LastIndexOf('.');
        var shortName = lastDot >= 0
            ? normalizedQualifiedName[(lastDot + 1)..]
            : normalizedQualifiedName;
        var containerQualifiedName = lastDot >= 0
            ? normalizedQualifiedName[..lastDot]
            : string.Empty;

        var namespaceCandidates = GetCSharpTypeNamespacesByName(shortName);
        foreach (var candidate in namespaceCandidates)
        {
            if (string.Equals(candidate.QualifiedName, containerQualifiedName, StringComparison.Ordinal))
                return true;
        }

        var containingTypes = GetCSharpTypeContainingTypesByName(shortName);
        if (containingTypes.Any(candidate => string.Equals(candidate.QualifiedName, containerQualifiedName, StringComparison.Ordinal)))
            return true;

        return false;
    }

    private static string NormalizeCSharpAliasTargetForTypeLookup(string qualifiedName)
    {
        if (string.IsNullOrWhiteSpace(qualifiedName))
            return string.Empty;

        var trimmed = qualifiedName.Trim();
        var builder = new System.Text.StringBuilder(trimmed.Length);
        var genericDepth = 0;
        for (var i = 0; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];
            if (ch == '<')
            {
                genericDepth++;
                continue;
            }

            if (ch == '>')
            {
                if (genericDepth > 0)
                    genericDepth--;
                continue;
            }

            if (genericDepth == 0)
                builder.Append(ch);
        }

        var normalized = builder.ToString().Trim();
        while (normalized.EndsWith("?", StringComparison.Ordinal))
            normalized = normalized[..^1].TrimEnd();
        while (normalized.EndsWith("[]", StringComparison.Ordinal))
            normalized = normalized[..^2].TrimEnd();

        return normalized;
    }

    private bool HasActiveCSharpUsingTypeAlias(string path, int lineNumber, string symbolName)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(symbolName))
            return false;

        if (TryResolveActiveCSharpUsingAliasScope(path, lineNumber, symbolName, requireTypeAlias: true, out _))
            return true;

        if (!_csharpUsingAliasScopesByPath.TryGetValue(path, out var scopes))
        {
            scopes = LoadCSharpUsingAliasScopes(path);
            _csharpUsingAliasScopesByPath[path] = scopes;
        }

        for (var i = scopes.Count - 1; i >= 0; i--)
        {
            var scope = scopes[i];
            if (!string.Equals(scope.AliasName, symbolName, StringComparison.Ordinal))
                continue;
            if (scope.Line > lineNumber)
                continue;
            if (lineNumber < scope.ScopeStartLine || lineNumber > scope.ScopeEndLine)
                continue;
            if (IsKnownCSharpTypeQualifiedName(scope.TargetQualifiedName))
                return true;

            var resolvedContainer = ResolveScopedCSharpContainingTypeQualifiedName(path, lineNumber, scope.TargetQualifiedName);
            if (string.IsNullOrWhiteSpace(resolvedContainer))
                continue;

            if (IsKnownCSharpTypeQualifiedName(resolvedContainer))
                return true;

            foreach (var activeNamespace in GetActiveCSharpTypeNamespaces(path, lineNumber))
            {
                if (string.IsNullOrWhiteSpace(activeNamespace))
                    continue;

                var namespacedTarget = CombineDbQualifiedName(activeNamespace, resolvedContainer);
                if (!string.IsNullOrWhiteSpace(namespacedTarget)
                    && IsKnownCSharpTypeQualifiedName(namespacedTarget))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private HashSet<string> GetActiveCSharpUsingStaticTargets(string path, int lineNumber)
    {
        if (!_csharpUsingStaticScopesByPath.TryGetValue(path, out var scopes))
        {
            scopes = LoadCSharpUsingStaticScopes(path);
            _csharpUsingStaticScopesByPath[path] = scopes;
        }

        var activeTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scope in scopes)
        {
            if (scope.Line > lineNumber)
                continue;
            if (lineNumber < scope.ScopeStartLine || lineNumber > scope.ScopeEndLine)
                continue;
            activeTargets.Add(scope.TargetQualifiedName);
        }

        foreach (var globalTarget in GetGlobalCSharpUsingStaticTargets())
            activeTargets.Add(globalTarget);

        return activeTargets;
    }

    private List<CSharpNamespaceScope> LoadCSharpNamespaceScopes(string path)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT s.line, s.body_start_line, s.body_end_line, s.end_line, s.name, s.signature, f.lines
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.path = @path
              AND f.lang = 'csharp'
              AND s.kind = 'namespace'
            ORDER BY s.line";
        cmd.Parameters.AddWithValue("@path", path);

        var scopes = new List<CSharpNamespaceScope>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            var line = reader.GetInt32(0);
            var startLine = reader.IsDBNull(1) ? line : reader.GetInt32(1);
            var endLine = reader.IsDBNull(2)
                ? (reader.IsDBNull(3) ? line : reader.GetInt32(3))
                : reader.GetInt32(2);
            var signature = GetNullableString(reader, 5);
            if (!string.IsNullOrWhiteSpace(signature)
                && signature.TrimEnd().EndsWith(';')
                && !reader.IsDBNull(6))
            {
                endLine = Math.Max(endLine, reader.GetInt32(6));
            }

            if (startLine <= 0 || endLine < startLine)
                continue;

            var qualifiedName = NormalizeDbCSharpQualifiedName(reader.GetString(4)) ?? string.Empty;
            scopes.Add(new CSharpNamespaceScope(qualifiedName, startLine, endLine));
        }

        return scopes;
    }

    private List<CSharpUsingNamespaceScope> LoadCSharpUsingNamespaceScopes(string path)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT s.kind, s.line, s.body_start_line, s.body_end_line, s.end_line, s.signature, f.lines
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.path = @path
              AND f.lang = 'csharp'
              AND (s.kind = 'import' OR s.kind = 'namespace')
            ORDER BY s.line";
        cmd.Parameters.AddWithValue("@path", path);

        var namespaceScopes = new List<(int StartLine, int EndLine)>();
        var imports = new List<(int Line, string Signature)>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            var kind = reader.GetString(0);
            var line = reader.GetInt32(1);
            if (kind == "namespace")
            {
                var startLine = reader.IsDBNull(2) ? line : reader.GetInt32(2);
                var endLine = reader.IsDBNull(3)
                    ? (reader.IsDBNull(4) ? line : reader.GetInt32(4))
                    : reader.GetInt32(3);
                var signature = GetNullableString(reader, 5);
                if (!string.IsNullOrWhiteSpace(signature)
                    && signature.TrimEnd().EndsWith(';')
                    && !reader.IsDBNull(6))
                {
                    endLine = Math.Max(endLine, reader.GetInt32(6));
                }

                if (startLine > 0 && endLine >= startLine)
                    namespaceScopes.Add((startLine, endLine));
                continue;
            }

            if (!reader.IsDBNull(5))
                imports.Add((line, reader.GetString(5)));
        }

        var scopes = new List<CSharpUsingNamespaceScope>();
        foreach (var import in imports)
        {
            if (!TryParseCSharpUsingNamespaceImport(import.Signature, out var target, out var isGlobal)
                || isGlobal)
            {
                continue;
            }

            var scopeStartLine = 1;
            var scopeEndLine = int.MaxValue;
            var scopeWidth = int.MaxValue;
            foreach (var (startLine, endLine) in namespaceScopes)
            {
                if (import.Line < startLine || import.Line > endLine)
                    continue;

                var width = endLine - startLine;
                if (width > scopeWidth)
                    continue;

                scopeStartLine = startLine;
                scopeEndLine = endLine;
                scopeWidth = width;
            }

            scopes.Add(new CSharpUsingNamespaceScope(target!, import.Line, scopeStartLine, scopeEndLine));
        }

        return scopes;
    }

    private List<CSharpContainingTypeScope> LoadCSharpContainingTypeScopes(string path)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT s.kind, s.name, s.container_name, s.container_qualified_name, s.visibility, s.signature, s.body_start_line, s.body_end_line, s.start_line, s.end_line
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.path = @path
              AND f.lang = 'csharp'
              AND s.kind IN ('class', 'struct', 'interface')
            ORDER BY s.start_line";
        cmd.Parameters.AddWithValue("@path", path);

        var scopes = new List<CSharpContainingTypeScope>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            var scope = CreateCSharpContainingTypeScope(
                path,
                GetNullableString(reader, 0),
                GetNullableString(reader, 1),
                GetNullableString(reader, 2),
                GetNullableString(reader, 3),
                GetNullableString(reader, 4),
                GetNullableString(reader, 5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetInt32(9));
            if (scope == null)
                continue;

            scopes.Add(scope);
            _csharpContainingTypeScopeByQualifiedName.TryAdd(scope.QualifiedName, scope);
        }

        return scopes;
    }

    private static CSharpContainingTypeScope? CreateCSharpContainingTypeScope(
        string path,
        string? kind,
        string? name,
        string? containerName,
        string? containerQualifiedName,
        string? visibility,
        string? signature,
        int? bodyStartLine,
        int? bodyEndLine,
        int? startLine,
        int? endLine)
    {
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(name))
            return null;

        var qualifiedName = CombineDbQualifiedName(
            NormalizeDbCSharpQualifiedName(containerQualifiedName ?? containerName ?? string.Empty),
            NormalizeDbCSharpQualifiedName(name));
        if (string.IsNullOrWhiteSpace(qualifiedName))
            return null;

        var resolvedStartLine = bodyStartLine ?? startLine ?? 0;
        var resolvedEndLine = bodyEndLine ?? endLine ?? resolvedStartLine;
        var declarationLine = startLine ?? resolvedStartLine;
        if (resolvedStartLine <= 0 || resolvedEndLine < resolvedStartLine || declarationLine <= 0)
            return null;

        return new CSharpContainingTypeScope(path, kind, qualifiedName, visibility, signature, declarationLine, resolvedStartLine, resolvedEndLine);
    }

    private CSharpContainingTypeScope? ResolveDirectCSharpBaseContainingTypeScope(CSharpContainingTypeScope containingTypeScope)
    {
        if (!string.Equals(containingTypeScope.Kind, "class", StringComparison.Ordinal))
            return null;

        var baseTypeReference = ParseCSharpBaseTypeReference(containingTypeScope.Signature);
        if (string.IsNullOrWhiteSpace(baseTypeReference))
            return null;

        var directBaseQualifiedName = ResolveScopedCSharpContainingTypeQualifiedName(
            containingTypeScope.Path,
            containingTypeScope.DeclarationLine,
            baseTypeReference);
        if (string.IsNullOrWhiteSpace(directBaseQualifiedName))
            return null;

        var directBaseScope = GetCSharpContainingTypeScope(directBaseQualifiedName);
        if (directBaseScope == null || !string.Equals(directBaseScope.Kind, "class", StringComparison.Ordinal))
            return null;

        return directBaseScope;
    }

    private string? ResolveScopedCSharpContainingTypeQualifiedName(string path, int lineNumber, string typeReference)
    {
        var normalizedReference = NormalizeCSharpBaseTypeReference(typeReference);
        if (string.IsNullOrWhiteSpace(normalizedReference))
            return null;

        normalizedReference = NormalizeCSharpBaseTypeReference(ResolveActiveCSharpUsingAliasReference(path, lineNumber, normalizedReference));
        if (string.IsNullOrWhiteSpace(normalizedReference))
            return null;

        var shortName = GetLastQualifiedSegment(normalizedReference);
        var candidateContainingTypes = GetCSharpTypeContainingTypesByName(shortName);
        var candidateNamespaces = GetCSharpTypeNamespacesByName(shortName);
        if (candidateContainingTypes.Count == 0 && candidateNamespaces.Count == 0)
            return normalizedReference;

        var lastDot = normalizedReference.LastIndexOf('.');
        if (lastDot >= 0)
        {
            var qualifiedPrefix = NormalizeDbCSharpQualifiedName(normalizedReference[..lastDot]);
            if (!string.IsNullOrWhiteSpace(qualifiedPrefix))
            {
                var exactContainingType = candidateContainingTypes.FirstOrDefault(candidate =>
                    string.Equals(candidate.QualifiedName, qualifiedPrefix, StringComparison.Ordinal));
                if (exactContainingType != null)
                    return CombineDbQualifiedName(qualifiedPrefix, shortName);

                var exactNamespace = candidateNamespaces.FirstOrDefault(candidate =>
                    string.Equals(candidate.QualifiedName, qualifiedPrefix, StringComparison.Ordinal));
                if (exactNamespace != null)
                    return CombineDbQualifiedName(qualifiedPrefix, shortName);
            }
        }

        foreach (var activeContainingTypeScope in GetActiveCSharpContainingTypeScopes(path, lineNumber))
        {
            var exactContainingType = candidateContainingTypes.FirstOrDefault(candidate =>
                string.Equals(candidate.QualifiedName, activeContainingTypeScope.QualifiedName, StringComparison.Ordinal));
            if (exactContainingType != null)
                return CombineDbQualifiedName(activeContainingTypeScope.QualifiedName, shortName);
        }

        foreach (var activeNamespace in GetActiveCSharpTypeNamespaces(path, lineNumber))
        {
            var exactNamespace = candidateNamespaces.FirstOrDefault(candidate =>
                string.Equals(candidate.QualifiedName, activeNamespace, StringComparison.Ordinal));
            if (exactNamespace != null)
                return CombineDbQualifiedName(activeNamespace, shortName);
        }

        return normalizedReference;
    }

    private static string? ParseCSharpBaseTypeReference(string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return null;

        var text = signature.TrimEnd();
        if (text.EndsWith("{", StringComparison.Ordinal))
            text = text[..^1].TrimEnd();

        var colonIndex = FindCSharpBaseListColonIndex(text);
        if (colonIndex < 0)
            return null;

        var baseList = text[(colonIndex + 1)..];
        var whereIndex = baseList.IndexOf(" where ", StringComparison.Ordinal);
        if (whereIndex >= 0)
            baseList = baseList[..whereIndex];

        var firstEntry = TakeFirstCSharpBaseListEntry(baseList).Trim();
        return firstEntry.Length == 0 ? null : firstEntry;
    }

    private static int FindCSharpBaseListColonIndex(string signature)
    {
        var angleDepth = 0;
        var parenDepth = 0;
        var squareDepth = 0;
        for (var i = 0; i < signature.Length; i++)
        {
            switch (signature[i])
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0)
                        angleDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0)
                        squareDepth--;
                    break;
                case ':':
                    if (angleDepth == 0 && parenDepth == 0 && squareDepth == 0)
                        return i;
                    break;
            }
        }

        return -1;
    }

    private static string TakeFirstCSharpBaseListEntry(string baseList)
    {
        var angleDepth = 0;
        var parenDepth = 0;
        var squareDepth = 0;
        for (var i = 0; i < baseList.Length; i++)
        {
            switch (baseList[i])
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0)
                        angleDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0)
                        squareDepth--;
                    break;
                case ',':
                    if (angleDepth == 0 && parenDepth == 0 && squareDepth == 0)
                        return baseList[..i];
                    break;
            }
        }

        return baseList;
    }

    private static string NormalizeCSharpBaseTypeReference(string typeReference)
    {
        if (string.IsNullOrWhiteSpace(typeReference))
            return string.Empty;

        var builder = new System.Text.StringBuilder(typeReference.Length);
        var angleDepth = 0;
        var parenDepth = 0;
        var squareDepth = 0;
        for (var i = 0; i < typeReference.Length; i++)
        {
            var ch = typeReference[i];
            switch (ch)
            {
                case '<':
                    angleDepth++;
                    continue;
                case '>':
                    if (angleDepth > 0)
                        angleDepth--;
                    continue;
                case '(':
                    if (angleDepth == 0 && squareDepth == 0)
                        return NormalizeDbCSharpQualifiedName(builder.ToString()) ?? string.Empty;
                    parenDepth++;
                    continue;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    continue;
                case '[':
                    if (angleDepth == 0 && parenDepth == 0)
                        squareDepth++;
                    continue;
                case ']':
                    if (squareDepth > 0)
                        squareDepth--;
                    continue;
            }

            if (angleDepth == 0 && parenDepth == 0 && squareDepth == 0)
                builder.Append(ch);
        }

        return NormalizeDbCSharpQualifiedName(builder.ToString()) ?? string.Empty;
    }

    private static bool IsNestedCSharpTypeAccessibleFromDerivedType(string? visibility, string? signature)
    {
        if (!string.IsNullOrWhiteSpace(visibility))
            return !string.Equals(visibility, "private", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(signature))
            return false;

        var normalizedSignature = signature.TrimStart();
        return normalizedSignature.StartsWith("public ", StringComparison.Ordinal)
            || normalizedSignature.StartsWith("protected ", StringComparison.Ordinal)
            || normalizedSignature.StartsWith("internal ", StringComparison.Ordinal)
            || normalizedSignature.StartsWith("private protected ", StringComparison.Ordinal)
            || normalizedSignature.StartsWith("protected internal ", StringComparison.Ordinal);
    }

    private static string GetLastQualifiedSegment(string qualifiedName)
    {
        if (string.IsNullOrWhiteSpace(qualifiedName))
            return string.Empty;

        var lastDot = qualifiedName.LastIndexOf('.');
        var lastColon = qualifiedName.LastIndexOf("::", StringComparison.Ordinal);
        var split = Math.Max(lastDot, lastColon);
        return split < 0 ? qualifiedName : qualifiedName[(split + (split == lastColon ? 2 : 1))..];
    }

    private List<CSharpUsingStaticScope> LoadCSharpUsingStaticScopes(string path)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT s.kind, s.line, s.body_start_line, s.body_end_line, s.end_line, s.signature, f.lines
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.path = @path
              AND f.lang = 'csharp'
              AND (s.kind = 'import' OR s.kind = 'namespace')
            ORDER BY s.line";
        cmd.Parameters.AddWithValue("@path", path);

        var namespaceScopes = new List<(int StartLine, int EndLine)>();
        var imports = new List<(int Line, string Signature)>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            var kind = reader.GetString(0);
            var line = reader.GetInt32(1);
            if (kind == "namespace")
            {
                var startLine = reader.IsDBNull(2) ? line : reader.GetInt32(2);
                var endLine = reader.IsDBNull(3)
                    ? (reader.IsDBNull(4) ? line : reader.GetInt32(4))
                    : reader.GetInt32(3);
                var signature = GetNullableString(reader, 5);
                if (!string.IsNullOrWhiteSpace(signature)
                    && signature.TrimEnd().EndsWith(';')
                    && !reader.IsDBNull(6))
                {
                    endLine = Math.Max(endLine, reader.GetInt32(6));
                }

                if (startLine > 0 && endLine >= startLine)
                    namespaceScopes.Add((startLine, endLine));
                continue;
            }

            if (!reader.IsDBNull(5))
                imports.Add((line, reader.GetString(5)));
        }

        var scopes = new List<CSharpUsingStaticScope>();
        foreach (var import in imports)
        {
            if (!TryParseCSharpUsingStaticImport(import.Signature, out var target, out var isGlobal)
                || isGlobal)
            {
                continue;
            }

            var scopeStartLine = 1;
            var scopeEndLine = int.MaxValue;
            var scopeWidth = int.MaxValue;
            foreach (var (startLine, endLine) in namespaceScopes)
            {
                if (import.Line < startLine || import.Line > endLine)
                    continue;

                var width = endLine - startLine;
                if (width > scopeWidth)
                    continue;

                scopeStartLine = startLine;
                scopeEndLine = endLine;
                scopeWidth = width;
            }

            scopes.Add(new CSharpUsingStaticScope(target!, import.Line, scopeStartLine, scopeEndLine));
        }

        return scopes;
    }

    private List<CSharpUsingAliasScope> LoadCSharpUsingAliasScopes(string path)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT s.kind, s.line, s.body_start_line, s.body_end_line, s.end_line, s.signature, f.lines
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.path = @path
              AND f.lang = 'csharp'
              AND (s.kind = 'import' OR s.kind = 'namespace')
            ORDER BY s.line";
        cmd.Parameters.AddWithValue("@path", path);

        var namespaceScopes = new List<(int StartLine, int EndLine)>();
        var imports = new List<(int Line, string Signature)>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            var kind = reader.GetString(0);
            var line = reader.GetInt32(1);
            if (kind == "namespace")
            {
                var startLine = reader.IsDBNull(2) ? line : reader.GetInt32(2);
                var endLine = reader.IsDBNull(3)
                    ? (reader.IsDBNull(4) ? line : reader.GetInt32(4))
                    : reader.GetInt32(3);
                var signature = GetNullableString(reader, 5);
                if (!string.IsNullOrWhiteSpace(signature)
                    && signature.TrimEnd().EndsWith(';')
                    && !reader.IsDBNull(6))
                {
                    endLine = Math.Max(endLine, reader.GetInt32(6));
                }

                if (startLine > 0 && endLine >= startLine)
                    namespaceScopes.Add((startLine, endLine));
                continue;
            }

            if (!reader.IsDBNull(5))
                imports.Add((line, reader.GetString(5)));
        }

        var scopes = new List<CSharpUsingAliasScope>();
        foreach (var import in imports)
        {
            if (!TryParseCSharpUsingAliasImport(import.Signature, out var aliasName, out var targetQualifiedName, out var isGlobal)
                || isGlobal)
            {
                continue;
            }

            var scopeStartLine = 1;
            var scopeEndLine = int.MaxValue;
            var scopeWidth = int.MaxValue;
            foreach (var (startLine, endLine) in namespaceScopes)
            {
                if (import.Line < startLine || import.Line > endLine)
                    continue;

                var width = endLine - startLine;
                if (width > scopeWidth)
                    continue;

                scopeStartLine = startLine;
                scopeEndLine = endLine;
                scopeWidth = width;
            }

            scopes.Add(new CSharpUsingAliasScope(
                aliasName!,
                targetQualifiedName!,
                import.Line,
                scopeStartLine,
                scopeEndLine,
                IsKnownCSharpTypeQualifiedName(targetQualifiedName!)));
        }

        return scopes;
    }

    private HashSet<string> GetGlobalCSharpUsingStaticTargets()
    {
        if (_csharpGlobalUsingStaticTargets != null)
            return _csharpGlobalUsingStaticTargets;

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT s.signature
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.lang = 'csharp'
              AND s.kind = 'import'";

        var targets = new HashSet<string>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            if (reader.IsDBNull(0))
                continue;
            if (TryParseCSharpUsingStaticImport(reader.GetString(0), out var target, out var isGlobal)
                && isGlobal)
            {
                targets.Add(target!);
            }
        }

        _csharpGlobalUsingStaticTargets = targets;
        return _csharpGlobalUsingStaticTargets;
    }

    private Dictionary<string, CSharpUsingAliasScope> GetGlobalCSharpUsingAliasesByName()
    {
        if (_csharpGlobalUsingAliasesByName != null)
            return _csharpGlobalUsingAliasesByName;

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT s.signature
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.lang = 'csharp'
              AND s.kind = 'import'";

        var aliases = new Dictionary<string, CSharpUsingAliasScope>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            if (reader.IsDBNull(0))
                continue;
            if (TryParseCSharpUsingAliasImport(reader.GetString(0), out var aliasName, out var targetQualifiedName, out var isGlobal)
                && isGlobal)
            {
                aliases[aliasName!] = new CSharpUsingAliasScope(
                    aliasName!,
                    targetQualifiedName!,
                    0,
                    1,
                    int.MaxValue,
                    IsKnownCSharpTypeQualifiedName(targetQualifiedName!));
            }
        }

        _csharpGlobalUsingAliasesByName = aliases;
        return _csharpGlobalUsingAliasesByName;
    }

    private HashSet<string> GetGlobalCSharpUsingNamespaces()
    {
        if (_csharpGlobalUsingNamespaces != null)
            return _csharpGlobalUsingNamespaces;

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT s.signature
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.lang = 'csharp'
              AND s.kind = 'import'";

        var namespaces = new HashSet<string>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            if (reader.IsDBNull(0))
                continue;
            if (TryParseCSharpUsingNamespaceImport(reader.GetString(0), out var target, out var isGlobal)
                && isGlobal)
            {
                namespaces.Add(target!);
            }
        }

        _csharpGlobalUsingNamespaces = namespaces;
        return _csharpGlobalUsingNamespaces;
    }

    private static bool TryParseCSharpUsingStaticImport(string signature, out string? target, out bool isGlobal)
    {
        target = null;
        isGlobal = false;
        if (string.IsNullOrWhiteSpace(signature))
            return false;

        var match = CSharpUsingStaticImportRegex.Match(signature);
        if (!match.Success)
            return false;

        target = NormalizeDbCSharpQualifiedName(match.Groups["target"].Value);
        if (string.IsNullOrWhiteSpace(target))
            return false;

        isGlobal = signature.TrimStart().StartsWith("global using static ", StringComparison.Ordinal);
        return true;
    }

    private static bool TryParseCSharpUsingAliasImport(string signature, out string? aliasName, out string? target, out bool isGlobal)
    {
        aliasName = null;
        target = null;
        isGlobal = false;
        if (string.IsNullOrWhiteSpace(signature))
            return false;

        var match = CSharpUsingAliasImportRegex.Match(signature);
        if (!match.Success)
            return false;

        aliasName = match.Groups["alias"].Value.Trim();
        target = NormalizeDbCSharpQualifiedName(match.Groups["target"].Value);
        if (string.IsNullOrWhiteSpace(aliasName) || string.IsNullOrWhiteSpace(target))
            return false;

        isGlobal = signature.TrimStart().StartsWith("global using ", StringComparison.Ordinal);
        return true;
    }

    private bool TryResolveActiveCSharpUsingAliasScope(string path, int lineNumber, string aliasReference, bool requireTypeAlias, out CSharpUsingAliasScope? resolvedScope)
    {
        resolvedScope = null;
        if (string.IsNullOrWhiteSpace(aliasReference))
            return false;

        var normalizedReference = NormalizeDbCSharpQualifiedName(aliasReference);
        if (string.IsNullOrWhiteSpace(normalizedReference))
            return false;

        var firstDot = normalizedReference.IndexOf('.');
        var aliasName = firstDot >= 0
            ? normalizedReference[..firstDot]
            : normalizedReference;
        if (string.IsNullOrWhiteSpace(aliasName))
            return false;

        if (!_csharpUsingAliasScopesByPath.TryGetValue(path, out var scopes))
        {
            scopes = LoadCSharpUsingAliasScopes(path);
            _csharpUsingAliasScopesByPath[path] = scopes;
        }

        for (var i = scopes.Count - 1; i >= 0; i--)
        {
            var scope = scopes[i];
            if (!string.Equals(scope.AliasName, aliasName, StringComparison.Ordinal))
                continue;
            if (scope.Line > lineNumber)
                continue;
            if (lineNumber < scope.ScopeStartLine || lineNumber > scope.ScopeEndLine)
                continue;
            if (requireTypeAlias && !scope.TargetsType)
                return false;
            resolvedScope = scope;
            return true;
        }

        var globalAliases = GetGlobalCSharpUsingAliasesByName();
        if (!globalAliases.TryGetValue(aliasName, out var globalScope))
            return false;
        if (requireTypeAlias && !globalScope.TargetsType)
            return false;

        resolvedScope = globalScope;
        return true;
    }

    private string ResolveActiveCSharpUsingAliasReference(string path, int lineNumber, string typeReference)
    {
        var resolvedReference = typeReference;
        if (string.IsNullOrWhiteSpace(resolvedReference))
            return string.Empty;

        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (visited.Add(resolvedReference)
               && TryResolveActiveCSharpUsingAliasScope(path, lineNumber, resolvedReference, requireTypeAlias: false, out var resolvedScope)
               && resolvedScope != null)
        {
            var normalizedReference = NormalizeDbCSharpQualifiedName(resolvedReference);
            if (string.IsNullOrWhiteSpace(normalizedReference))
                break;

            var firstDot = normalizedReference.IndexOf('.');
            var suffix = firstDot >= 0
                ? NormalizeDbCSharpQualifiedName(normalizedReference[(firstDot + 1)..])
                : string.Empty;
            var nextReference = string.IsNullOrWhiteSpace(suffix)
                ? resolvedScope.TargetQualifiedName
                : CombineDbQualifiedName(resolvedScope.TargetQualifiedName, suffix);
            if (string.IsNullOrWhiteSpace(nextReference)
                || string.Equals(nextReference, resolvedReference, StringComparison.Ordinal))
            {
                break;
            }

            resolvedReference = nextReference;
        }

        return resolvedReference;
    }

    private static bool TryParseCSharpUsingNamespaceImport(string signature, out string? target, out bool isGlobal)
    {
        target = null;
        isGlobal = false;
        if (string.IsNullOrWhiteSpace(signature)
            || signature.IndexOf('=') >= 0)
        {
            return false;
        }

        var match = CSharpUsingNamespaceImportRegex.Match(signature);
        if (!match.Success)
            return false;

        target = NormalizeDbCSharpQualifiedName(match.Groups["target"].Value);
        if (string.IsNullOrWhiteSpace(target))
            return false;

        isGlobal = signature.TrimStart().StartsWith("global using ", StringComparison.Ordinal);
        return true;
    }

    private static bool IsCSharpUsingStaticConstantPatternContext(string context, string symbolName, int columnNumber)
    {
        if (string.IsNullOrWhiteSpace(context))
            return false;

        if (!TryFindCSharpReferenceTokenStart(context, symbolName, columnNumber, out var symbolColumn))
            return false;

        var cursor = symbolColumn;
        cursor = SkipCSharpTriviaBackward(context, cursor);
        return IsCSharpUsingStaticConstantPatternAnchor(context, ref cursor, out _)
            || IsCSharpUsingStaticConstantTypeKeywordAnchor(context, ref cursor);
    }

    private static bool TryExtractQualifiedCSharpPatternQualifier(string context, string symbolName, int columnNumber, out string qualifier, out string anchorKind)
    {
        qualifier = string.Empty;
        anchorKind = string.Empty;
        if (string.IsNullOrWhiteSpace(context)
            || string.IsNullOrWhiteSpace(symbolName)
            || !TryFindCSharpReferenceTokenStart(context, symbolName, columnNumber, out var symbolColumn))
        {
            return false;
        }

        var headCursor = symbolColumn + symbolName.Length;
        if (!SkipCSharpPatternHeadBackward(context, ref headCursor))
            return false;

        var fullHead = NormalizeDbCSharpQualifiedName(context[headCursor..(symbolColumn + symbolName.Length)]);
        if (string.IsNullOrWhiteSpace(fullHead))
            return false;

        var lastDot = fullHead.LastIndexOf('.');
        if (lastDot < 0)
            return false;

        var anchorCursor = headCursor;
        if (!IsCSharpUsingStaticConstantPatternAnchor(context, ref anchorCursor, out anchorKind))
            return false;

        qualifier = fullHead[..lastDot];
        return !string.IsNullOrWhiteSpace(qualifier);
    }

    private static bool IsCSharpUsingStaticConstantPatternAnchor(string text, ref int cursor, out string anchorKind)
    {
        anchorKind = string.Empty;
        cursor = SkipCSharpTriviaBackward(text, cursor);
        if (TryConsumeTrailingCSharpToken(text, ref cursor, "not"))
            cursor = SkipCSharpTriviaBackward(text, cursor);

        while (true)
        {
            if (TryConsumeTrailingCSharpToken(text, ref cursor, "case"))
            {
                anchorKind = "case";
                return true;
            }

            if (TryConsumeTrailingCSharpToken(text, ref cursor, "is"))
            {
                anchorKind = "is";
                return true;
            }

            if (!TryConsumeTrailingCSharpToken(text, ref cursor, "or")
                && !TryConsumeTrailingCSharpToken(text, ref cursor, "and"))
            {
                return false;
            }

            cursor = SkipCSharpTriviaBackward(text, cursor);
            if (!SkipCSharpPatternHeadBackward(text, ref cursor))
                return false;
            cursor = SkipCSharpTriviaBackward(text, cursor);
            if (TryConsumeTrailingCSharpToken(text, ref cursor, "not"))
                cursor = SkipCSharpTriviaBackward(text, cursor);
        }
    }

    private static bool IsCSharpUsingStaticConstantTypeKeywordAnchor(string text, ref int cursor)
    {
        cursor = SkipCSharpTriviaBackward(text, cursor);
        if (cursor <= 0 || text[cursor - 1] != '(')
            return false;

        cursor--;
        cursor = SkipCSharpTriviaBackward(text, cursor);
        return TryConsumeTrailingCSharpToken(text, ref cursor, "typeof")
            || TryConsumeTrailingCSharpToken(text, ref cursor, "sizeof")
            || TryConsumeTrailingCSharpToken(text, ref cursor, "default");
    }

    private bool TryBuildCSharpUsingStaticPatternContextWindow(
        string path,
        int lineNumber,
        string contextForFilter,
        int columnNumber,
        string symbolName,
        out string patternContext,
        out int patternColumn)
    {
        patternContext = contextForFilter;
        patternColumn = columnNumber;
        if (!_hasChunksTable
            || string.IsNullOrWhiteSpace(path)
            || string.IsNullOrWhiteSpace(symbolName)
            || string.IsNullOrWhiteSpace(contextForFilter)
            || lineNumber <= 1
            || columnNumber <= 0)
        {
            return IsCSharpUsingStaticConstantPatternContext(patternContext, symbolName, patternColumn)
                || TryExtractQualifiedCSharpPatternQualifier(patternContext, symbolName, patternColumn, out _, out _);
        }

        if (IsCSharpUsingStaticConstantPatternContext(patternContext, symbolName, patternColumn)
            || TryExtractQualifiedCSharpPatternQualifier(patternContext, symbolName, patternColumn, out _, out _))
        {
            return true;
        }

        var maxLookback = lineNumber - 1;
        var lookback = Math.Min(2, maxLookback);
        while (true)
        {
            var startLine = Math.Max(1, lineNumber - lookback);
            if (!TryLoadIndexedFileLines(path, out _, out _, out var lineMap, startLine, lineNumber)
                || !lineMap.TryGetValue(lineNumber, out var currentLine))
            {
                return IsCSharpUsingStaticConstantPatternContext(patternContext, symbolName, patternColumn)
                    || TryExtractQualifiedCSharpPatternQualifier(patternContext, symbolName, patternColumn, out _, out _);
            }

            var lines = new List<string>();
            var prefixLength = 0;
            for (var absoluteLine = startLine; absoluteLine <= lineNumber; absoluteLine++)
            {
                if (!lineMap.TryGetValue(absoluteLine, out var lineText))
                    continue;

                if (absoluteLine < lineNumber)
                    prefixLength += lineText.Length + 1;
                lines.Add(lineText);
            }

            patternContext = lines.Count <= 1 ? currentLine : string.Join('\n', lines);
            patternColumn = lines.Count <= 1 ? columnNumber : prefixLength + columnNumber;
            if (IsCSharpUsingStaticConstantPatternContext(patternContext, symbolName, patternColumn)
                || TryExtractQualifiedCSharpPatternQualifier(patternContext, symbolName, patternColumn, out _, out _))
            {
                return true;
            }

            if (startLine == 1 || lookback >= maxLookback)
                return false;

            lookback = Math.Min(maxLookback, Math.Max(lookback + 1, lookback * 2));
        }
    }

    private HashSet<string> GetScopedCSharpQualifiedPatternQualifierCandidates(string path, int lineNumber, string qualifier)
    {
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        var normalizedQualifier = NormalizeDbCSharpQualifiedName(ResolveActiveCSharpUsingAliasReference(path, lineNumber, qualifier));
        if (string.IsNullOrWhiteSpace(normalizedQualifier))
            return candidates;

        candidates.Add(normalizedQualifier);

        foreach (var activeNamespace in GetActiveCSharpTypeNamespaces(path, lineNumber))
        {
            if (string.IsNullOrWhiteSpace(activeNamespace))
                continue;
            candidates.Add(activeNamespace + "." + normalizedQualifier);
        }

        foreach (var activeContainingTypeScope in GetActiveCSharpContainingTypeScopes(path, lineNumber))
        {
            candidates.Add(activeContainingTypeScope.QualifiedName + "." + normalizedQualifier);

            var inheritedContainingTypes = GetInheritedCSharpContainingTypes(activeContainingTypeScope);
            foreach (var inheritedContainingType in inheritedContainingTypes)
            {
                candidates.Add(inheritedContainingType + "." + normalizedQualifier);
            }
        }

        return candidates;
    }

    private static int SkipCSharpTriviaBackward(string text, int cursor)
    {
        while (cursor > 0)
        {
            if (char.IsWhiteSpace(text[cursor - 1]))
            {
                cursor--;
                continue;
            }

            if (cursor >= 2
                && text[cursor - 1] == '/'
                && text[cursor - 2] == '*')
            {
                var commentStart = text.LastIndexOf("/*", cursor - 2, StringComparison.Ordinal);
                if (commentStart >= 0)
                {
                    cursor = commentStart;
                    continue;
                }
            }

            if (TryGetCSharpSingleLineCommentLineStart(text, cursor, out var commentLineStart))
            {
                cursor = commentLineStart;
                continue;
            }

            break;
        }

        return cursor;
    }

    private static bool TryGetCSharpSingleLineCommentLineStart(string text, int cursor, out int commentLineStart)
    {
        commentLineStart = -1;
        if (cursor <= 0)
            return false;

        var lineStart = text.LastIndexOf('\n', Math.Min(cursor - 1, text.Length - 1));
        lineStart = lineStart < 0 ? 0 : lineStart + 1;

        var firstNonWhitespace = lineStart;
        while (firstNonWhitespace < cursor && char.IsWhiteSpace(text[firstNonWhitespace]))
            firstNonWhitespace++;

        if (firstNonWhitespace + 1 >= cursor
            || text[firstNonWhitespace] != '/'
            || text[firstNonWhitespace + 1] != '/')
        {
            return false;
        }

        commentLineStart = lineStart;
        return true;
    }

    private static bool SkipCSharpPatternHeadBackward(string text, ref int cursor)
    {
        if (!TryConsumeTrailingCSharpIdentifier(text, ref cursor))
            return false;

        while (true)
        {
            cursor = SkipCSharpTriviaBackward(text, cursor);
            if (cursor >= 2
                && text[cursor - 2] == ':'
                && text[cursor - 1] == ':')
            {
                cursor -= 2;
            }
            else if (cursor > 0 && text[cursor - 1] == '.')
            {
                cursor--;
            }
            else
            {
                break;
            }

            cursor = SkipCSharpTriviaBackward(text, cursor);
            if (!TryConsumeTrailingCSharpIdentifier(text, ref cursor))
                return false;
        }

        return true;
    }

    private static bool TryConsumeTrailingCSharpIdentifier(string text, ref int cursor)
    {
        var end = cursor;
        while (cursor > 0
               && (char.IsLetterOrDigit(text[cursor - 1])
                   || text[cursor - 1] == '_'))
        {
            cursor--;
        }

        if (cursor == end)
            return false;

        if (cursor > 0 && text[cursor - 1] == '@')
            cursor--;

        return true;
    }

    private static bool TryFindCSharpReferenceTokenStart(string text, string token, int preferredColumn, out int matchIndex)
    {
        matchIndex = -1;
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(token))
            return false;

        var preferredIndex = Math.Max(0, preferredColumn - 1);
        var searchStart = 0;
        while (searchStart < text.Length)
        {
            var candidate = text.IndexOf(token, searchStart, StringComparison.Ordinal);
            if (candidate < 0)
                break;

            searchStart = candidate + token.Length;
            if (!IsCSharpTokenBoundary(text, candidate - 1) || !IsCSharpTokenBoundary(text, candidate + token.Length))
                continue;

            if (candidate <= preferredIndex)
            {
                matchIndex = candidate;
                continue;
            }

            if (matchIndex < 0)
                matchIndex = candidate;
            break;
        }

        return matchIndex >= 0;
    }

    private static bool IsCSharpTokenBoundary(string text, int index)
    {
        if (index < 0 || index >= text.Length)
            return true;

        return !char.IsLetterOrDigit(text[index]) && text[index] != '_';
    }

    private static bool TryConsumeTrailingCSharpToken(string text, ref int cursor, string token)
    {
        var tokenStart = cursor - token.Length;
        if (tokenStart < 0
            || !text.AsSpan(tokenStart, token.Length).SequenceEqual(token))
        {
            return false;
        }

        if (tokenStart > 0 && (char.IsLetterOrDigit(text[tokenStart - 1]) || text[tokenStart - 1] == '_'))
            return false;
        if (cursor < text.Length && (char.IsLetterOrDigit(text[cursor]) || text[cursor] == '_'))
            return false;

        cursor = tokenStart;
        return true;
    }

    private List<CSharpTypeNamespaceCandidate> GetCSharpTypeNamespacesByName(string symbolName)
    {
        if (_csharpTypeNamespacesByName.TryGetValue(symbolName, out var cached))
            return cached;

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT DISTINCT s.container_kind, s.container_name, s.container_qualified_name, f.path, s.visibility, s.signature
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.lang = 'csharp'
              AND s.name = @symbolName COLLATE NOCASE
              AND s.kind IN ('class', 'struct', 'interface', 'enum', 'delegate')";
        cmd.Parameters.AddWithValue("@symbolName", symbolName);

        var namespaces = new List<CSharpTypeNamespaceCandidate>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            var containerKind = GetNullableString(reader, 0);
            var path = reader.GetString(3);
            var visibility = GetNullableString(reader, 4);
            var signature = GetNullableString(reader, 5);
            var isFileLocal = string.Equals(visibility, "file", StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(signature) && signature.Contains("file ", StringComparison.Ordinal));
            if (string.Equals(containerKind, "namespace", StringComparison.Ordinal))
            {
                var qualifiedNamespace = GetNullableString(reader, 2);
                var fallbackNamespace = GetNullableString(reader, 1);
                var namespaceName = NormalizeDbCSharpQualifiedName(qualifiedNamespace ?? fallbackNamespace ?? string.Empty)
                    ?? string.Empty;
                namespaces.Add(new CSharpTypeNamespaceCandidate(namespaceName, path, isFileLocal));
                continue;
            }

            if (containerKind == null)
                namespaces.Add(new CSharpTypeNamespaceCandidate(string.Empty, path, isFileLocal));
        }

        _csharpTypeNamespacesByName[symbolName] = namespaces;
        return namespaces;
    }

    private List<CSharpContainingTypeCandidate> GetCSharpTypeContainingTypesByName(string symbolName)
    {
        if (_csharpTypeContainingTypesByName.TryGetValue(symbolName, out var cached))
            return cached;

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT DISTINCT s.container_kind, s.container_name, s.container_qualified_name, s.visibility, s.signature
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.lang = 'csharp'
              AND s.name = @symbolName COLLATE NOCASE
              AND s.kind IN ('class', 'struct', 'interface', 'enum', 'delegate')";
        cmd.Parameters.AddWithValue("@symbolName", symbolName);

        var containingTypes = new List<CSharpContainingTypeCandidate>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            var containerKind = GetNullableString(reader, 0);
            if (containerKind is not ("class" or "struct" or "interface"))
                continue;

            var containerQualifiedName = GetNullableString(reader, 2);
            var containerName = GetNullableString(reader, 1);
            var qualifiedContainer = NormalizeDbCSharpQualifiedName(containerQualifiedName ?? containerName ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(qualifiedContainer))
            {
                containingTypes.Add(new CSharpContainingTypeCandidate(
                    qualifiedContainer,
                    IsNestedCSharpTypeAccessibleFromDerivedType(GetNullableString(reader, 3), GetNullableString(reader, 4))));
            }
        }

        _csharpTypeContainingTypesByName[symbolName] = containingTypes;
        return containingTypes;
    }

    private HashSet<string> GetCSharpConstantPatternContainersByMemberName(string symbolName)
    {
        if (_csharpConstantPatternContainersByMemberName.TryGetValue(symbolName, out var cached))
            return cached;

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT s.kind, s.container_kind, s.container_name, s.container_qualified_name, s.signature
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.lang = 'csharp'
              AND s.name = @symbolName COLLATE NOCASE
              AND s.container_name IS NOT NULL";
        cmd.Parameters.AddWithValue("@symbolName", symbolName);

        var containers = new HashSet<string>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            var kind = reader.GetString(0);
            var containerKind = GetNullableString(reader, 1);
            var containerName = GetNullableString(reader, 2);
            if (string.IsNullOrWhiteSpace(containerName))
                continue;

            var isConstantPatternMember = (kind == "enum" && containerKind == "enum")
                || (containerKind is "class" or "struct" && !reader.IsDBNull(4) && IsCSharpConstSignature(reader.GetString(4)));
            if (!isConstantPatternMember)
                continue;

            var qualifiedContainer = GetNullableString(reader, 3);
            containers.Add(string.IsNullOrWhiteSpace(qualifiedContainer) ? containerName! : qualifiedContainer!);
        }

        _csharpConstantPatternContainersByMemberName[symbolName] = containers;
        return containers;
    }

    private static bool IsCSharpConstSignature(string signature) =>
        signature.Contains(" const ", StringComparison.Ordinal)
        || signature.StartsWith("const ", StringComparison.Ordinal);

    private static string? NormalizeDbCSharpQualifiedName(string candidate)
    {
        var trimmed = candidate.Trim();
        if (trimmed.StartsWith("global::", StringComparison.Ordinal))
            trimmed = trimmed["global::".Length..];
        if (string.IsNullOrWhiteSpace(trimmed))
            return null;

        var segments = trimmed
            .Split(["::", "."], StringSplitOptions.None)
            .Select(segment => segment.Trim())
            .Where(segment => segment.Length > 0)
            .Select(segment => segment[0] == '@' ? segment[1..] : segment)
            .ToList();
        return segments.Count == 0 ? null : string.Join(".", segments);
    }

    // Query-side mirror of the C# declaration canonicalizer. Users commonly type source
    // spellings such as `@class` or `Outer.@class`; the DB stores the canonical names
    // without the verbatim `@`, so query entrypoints normalize to the persisted form first.
    // Rust macro names are also accepted with a trailing `!` because the extractor stores
    // them without the punctuation, so `my_macro!` and `my_macro` resolve to the same row.
    // The normalization is applied when `--lang` is omitted or explicitly `csharp` because
    // name-based lookup still needs to treat C# verbatim spellings as canonical symbol names.
    // Other languages, including SQL, must preserve leading `@` characters.
    // C# 宣言側 canonicalizer の query 側ミラー。`@class` / `Outer.@class` のような source
    // spelling を受けても、DB 側の `@` なし canonical 名に合わせてから検索する。
    // Rust macro 名も extractor 側では末尾 `!` を落として保存するため、`my_macro!` と `my_macro`
    // を同じ行へ解決できるようにする。
    // `--lang` 未指定または `csharp` 指定では name-based lookup が verbatim spelling を canonical 名へ寄せる。
    // それ以外の言語、特に SQL では先頭 `@` を保持する。
    private static string? NormalizeCSharpVerbatimQuery(string? query, string? lang)
    {
        if (!string.IsNullOrWhiteSpace(lang) && string.Equals(lang, "rust", StringComparison.OrdinalIgnoreCase))
        {
            var rustNormalized = NormalizeRustSymbolSearchQuery(query);
            return string.IsNullOrWhiteSpace(rustNormalized) ? null : rustNormalized;
        }

        if (!string.IsNullOrWhiteSpace(lang) && !string.Equals(lang, "csharp", StringComparison.OrdinalIgnoreCase))
            return query;
        var normalized = query == null ? null : NormalizeDbCSharpQualifiedName(query);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string? NormalizeRustMacroQuery(string? query)
    {
        if (query == null)
            return null;

        var trimmed = query.TrimEnd();
        if (!trimmed.EndsWith("!", StringComparison.Ordinal))
            return trimmed;

        var normalized = trimmed[..^1].TrimEnd();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static bool IsBareVerbatimQueryToken(string? value)
    {
        var trimmed = value?.Trim();
        return trimmed is { Length: > 0 } && trimmed.All(ch => ch == '@');
    }

    private static string? CombineDbQualifiedName(string? parentQualifiedName, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return parentQualifiedName;
        if (string.IsNullOrWhiteSpace(parentQualifiedName))
            return name;
        return $"{parentQualifiedName}.{name}";
    }

    private QueryCountResult CountSearchReferencesTotalWithUsingStaticFilter(string? query, string? lang, string? referenceKind, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests, bool exact)
    {
        if (!_hasReferencesTable)
            return new QueryCountResult(0, 0);

        using var cmd = CreateSearchReferencesCommand(
            query,
            int.MaxValue,
            lang,
            referenceKind,
            pathPatterns,
            excludePathPatterns,
            excludeTests,
            exact,
            includeOrdering: false);
        using var reader = cmd.ExecuteTrackedReader();

        int count = 0;
        bool includesSql = false;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        while (reader.TrackedRead())
        {
            var row = ReadSearchReferenceRawRow(reader);
            if (ShouldSuppressCSharpUsingStaticConstantPatternReference(row))
                continue;

            count++;
            includesSql |= IsSqlLanguage(row.Lang);
            paths.Add(row.Path);
        }

        return new QueryCountResult(count, paths.Count, includesSql);
    }

    public int CountSearchReferences(string? query = null, int limit = 20, string? lang = null, string? referenceKind = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false)
    {
        query = NormalizeSymbolSearchQuery(query, lang, exact) ?? query ?? string.Empty;
        if (ShouldApplyCSharpUsingStaticConstantPatternReferenceFilter(lang, referenceKind, exact))
            return SearchReferences(query, limit, lang, referenceKind, pathPatterns, excludePathPatterns, excludeTests, exact).Count;

        if (!_hasReferencesTable) return 0;
        using var cmd = _conn.CreateCommand();
        var referenceLineJoin = ReferenceLineJoinSql("r");
        var contextSql = ReferenceContextSql("r");

        var innerSql = @"
            SELECT 1
            FROM symbol_references r
	                JOIN files f ON r.file_id = f.id" + referenceLineJoin + $@"
            WHERE 1=1";
        innerSql += $" AND {BuildGraphSupportedLanguagePredicate(cmd, "f", "graphLang")}";

        var countSuffixAlias = ComputeCSharpAttributeSuffixAlias(query, lang, referenceKind);
        var countAliasScope = countSuffixAlias != null
            ? " AND f.lang = 'csharp' AND r.reference_kind = 'attribute'"
            : string.Empty;
        const string sqlLeafCountScope = " AND f.lang = 'sql'";
        if (query != null)
        {
            var allowSqlLeafFallback = AllowSqlLeafFallbackForQuery(query);
            var useSqlQualifiedContextMatch = SqlNameResolver.HasQualifier(query);
            if (useSqlQualifiedContextMatch && exact && _foldReady)
                innerSql += countSuffixAlias != null
                    ? $" AND (((f.lang = 'sql') AND sql_context_has_name_folded_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND (r.symbol_name_folded = @query OR (r.symbol_name_folded = @queryAttributeAlias{countAliasScope}))))"
                    : $" AND (((f.lang = 'sql') AND sql_context_has_name_folded_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name_folded = @query))";
            else if (useSqlQualifiedContextMatch && exact)
                innerSql += countSuffixAlias != null
                    ? $" AND (((f.lang = 'sql') AND sql_context_has_name_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND (r.symbol_name = @query COLLATE NOCASE OR (r.symbol_name = @queryAttributeAlias COLLATE NOCASE{countAliasScope}))))"
                    : $" AND (((f.lang = 'sql') AND sql_context_has_name_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name = @query COLLATE NOCASE))";
            else if (useSqlQualifiedContextMatch && _foldReady)
                innerSql += countSuffixAlias != null
                    ? $" AND (((f.lang = 'sql') AND sql_context_like_name_folded_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND (r.symbol_name LIKE @query ESCAPE '\\' OR (r.symbol_name = @queryAttributeAlias COLLATE NOCASE{countAliasScope}))))"
                    : $" AND (((f.lang = 'sql') AND sql_context_like_name_folded_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name LIKE @query ESCAPE '\\'))";
            else if (useSqlQualifiedContextMatch)
                innerSql += countSuffixAlias != null
                    ? $" AND (((f.lang = 'sql') AND sql_context_like_name_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND (r.symbol_name LIKE @query ESCAPE '\\' OR (r.symbol_name = @queryAttributeAlias COLLATE NOCASE{countAliasScope}))))"
                    : $" AND (((f.lang = 'sql') AND sql_context_like_name_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name LIKE @query ESCAPE '\\'))";
            else if (exact && _foldReady)
                innerSql += countSuffixAlias != null
                    ? $" AND (r.symbol_name_folded = @query OR (r.symbol_name_folded = @queryAttributeAlias{countAliasScope}){(allowSqlLeafFallback ? $" OR (r.symbol_name_folded = @aliasQueryLeafFolded{sqlLeafCountScope})" : string.Empty)})"
                    : allowSqlLeafFallback
                        ? $" AND (r.symbol_name_folded = @query OR (r.symbol_name_folded = @aliasQueryLeafFolded{sqlLeafCountScope}))"
                        : " AND r.symbol_name_folded = @query";
            else if (exact)
                innerSql += countSuffixAlias != null
                    ? $" AND (r.symbol_name = @query COLLATE NOCASE OR (r.symbol_name = @queryAttributeAlias COLLATE NOCASE{countAliasScope}){(allowSqlLeafFallback ? $" OR (r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE{sqlLeafCountScope})" : string.Empty)})"
                    : allowSqlLeafFallback
                        ? $" AND (r.symbol_name = @query COLLATE NOCASE OR (r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE{sqlLeafCountScope}))"
                        : " AND r.symbol_name = @query COLLATE NOCASE";
            else
                innerSql += countSuffixAlias != null
                    ? $" AND (r.symbol_name LIKE @query ESCAPE '\\' OR (r.symbol_name = @queryAttributeAlias COLLATE NOCASE{countAliasScope}) OR (r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE{sqlLeafCountScope}))"
                    : $" AND (r.symbol_name LIKE @query ESCAPE '\\' OR (r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE{sqlLeafCountScope}))";
        }
        if (referenceKind != null)
            innerSql += " AND r.reference_kind = @referenceKind";
        if (lang != null)
            innerSql += " AND f.lang = @lang";
        AppendPathFilters(ref innerSql, pathPatterns, excludePathPatterns, excludeTests);
        if (referenceKind == null)
            innerSql += $" GROUP BY r.file_id, r.symbol_name, r.line, r.column_number, {GetLogicalReferenceKindSql("r.reference_kind")}";
        innerSql += " LIMIT @limit";

        cmd.CommandText = $"SELECT COUNT(*) FROM ({innerSql})";
        if (query != null)
        {
            var value = !exact
                ? $"%{EscapeLikeQuery(query)}%"
                : _foldReady
                    ? NameFold.Fold(query) ?? query
                    : query;
            cmd.Parameters.AddWithValue("@query", value);
            cmd.Parameters.AddWithValue("@aliasQuery", query);
            cmd.Parameters.AddWithValue("@aliasQueryLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(query)) ?? SqlNameResolver.GetLeafName(query));
            if (countSuffixAlias != null)
            {
                var aliasParam = exact && _foldReady
                    ? NameFold.Fold(countSuffixAlias) ?? countSuffixAlias
                    : countSuffixAlias;
                cmd.Parameters.AddWithValue("@queryAttributeAlias", aliasParam);
            }
        }
        if (referenceKind != null)
            cmd.Parameters.AddWithValue("@referenceKind", referenceKind);
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", NormalizeQueryLanguage(lang));
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        cmd.Parameters.AddWithValue("@limit", limit);

        var raw = cmd.ExecuteScalar();
        return raw is long l ? (int)l : Convert.ToInt32(raw);
    }

    public QueryCountResult CountSearchReferencesTotal(string? query = null, string? lang = null, string? referenceKind = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false)
    {
        lang = NormalizeQueryLanguage(lang);
        query = NormalizeSymbolSearchQuery(query, lang, exact) ?? query ?? string.Empty;
        if (ShouldApplyCSharpUsingStaticConstantPatternReferenceFilter(lang, referenceKind, exact))
            return CountSearchReferencesTotalWithUsingStaticFilter(query, lang, referenceKind, pathPatterns, excludePathPatterns, excludeTests, exact);

        if (!_hasReferencesTable)
            return new QueryCountResult(0, 0);

        using var cmd = _conn.CreateCommand();
        var referenceLineJoin = ReferenceLineJoinSql("r");
        var contextSql = ReferenceContextSql("r");

        var innerSql = @"
            SELECT path, lang
            FROM (
                SELECT f.path AS path, f.lang AS lang, r.file_id, r.symbol_name, r.line, r.column_number, " + GetLogicalReferenceKindSql("r.reference_kind") + @" AS logical_reference_kind
                FROM symbol_references r
                JOIN files f ON r.file_id = f.id" + referenceLineJoin + $@"
                WHERE 1=1";
        innerSql += $" AND {BuildGraphSupportedLanguagePredicate(cmd, "f", "graphLang")}";

        var totalSuffixAlias = ComputeCSharpAttributeSuffixAlias(query, lang, referenceKind);
        var totalAliasScope = totalSuffixAlias != null
            ? " AND f.lang = 'csharp' AND r.reference_kind = 'attribute'"
            : string.Empty;
        var totalCssScssVariableAlias = ComputeCssScssVariableAlias(query);
        var totalCssScssVariableAliasScope = totalCssScssVariableAlias != null
            ? " AND f.lang = 'css'"
            : string.Empty;
        const string sqlLeafTotalScope = " AND f.lang = 'sql'";
        if (query != null)
        {
            var allowSqlLeafFallback = AllowSqlLeafFallbackForQuery(query);
            var useSqlQualifiedContextMatch = SqlNameResolver.HasQualifier(query);
            if (useSqlQualifiedContextMatch && exact && _foldReady)
                innerSql += totalSuffixAlias != null
                    ? $" AND (((f.lang = 'sql') AND sql_context_has_name_folded_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND (r.symbol_name_folded = @query OR (r.symbol_name_folded = @queryAttributeAlias{totalAliasScope}))))"
                    : $" AND (((f.lang = 'sql') AND sql_context_has_name_folded_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name_folded = @query))";
            else if (useSqlQualifiedContextMatch && exact)
                innerSql += totalSuffixAlias != null
                    ? $" AND (((f.lang = 'sql') AND sql_context_has_name_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND (r.symbol_name = @query COLLATE NOCASE OR (r.symbol_name = @queryAttributeAlias COLLATE NOCASE{totalAliasScope}))))"
                    : $" AND (((f.lang = 'sql') AND sql_context_has_name_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name = @query COLLATE NOCASE))";
            else if (useSqlQualifiedContextMatch && _foldReady)
                innerSql += totalSuffixAlias != null
                    ? $" AND (((f.lang = 'sql') AND sql_context_like_name_folded_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND (r.symbol_name LIKE @query ESCAPE '\\' OR (r.symbol_name = @queryAttributeAlias COLLATE NOCASE{totalAliasScope}))))"
                    : $" AND (((f.lang = 'sql') AND sql_context_like_name_folded_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name LIKE @query ESCAPE '\\'))";
            else if (useSqlQualifiedContextMatch)
                innerSql += totalSuffixAlias != null
                    ? $" AND (((f.lang = 'sql') AND sql_context_like_name_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND (r.symbol_name LIKE @query ESCAPE '\\' OR (r.symbol_name = @queryAttributeAlias COLLATE NOCASE{totalAliasScope}))))"
                    : $" AND (((f.lang = 'sql') AND sql_context_like_name_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name LIKE @query ESCAPE '\\'))";
            else if (exact && _foldReady)
                innerSql += totalSuffixAlias != null
                    ? $" AND (r.symbol_name_folded = @query OR (r.symbol_name_folded = @queryAttributeAlias{totalAliasScope}){(allowSqlLeafFallback ? $" OR (r.symbol_name_folded = @aliasQueryLeafFolded{sqlLeafTotalScope})" : string.Empty)})"
                    : totalCssScssVariableAlias != null
                        ? $" AND (r.symbol_name_folded = @query OR (r.symbol_name_folded = @queryCssScssVariableAlias{totalCssScssVariableAliasScope}){(allowSqlLeafFallback ? $" OR (r.symbol_name_folded = @aliasQueryLeafFolded{sqlLeafTotalScope})" : string.Empty)})"
                        : allowSqlLeafFallback
                        ? $" AND (r.symbol_name_folded = @query OR (r.symbol_name_folded = @aliasQueryLeafFolded{sqlLeafTotalScope}))"
                        : " AND r.symbol_name_folded = @query";
            else if (exact)
                innerSql += totalSuffixAlias != null
                    ? $" AND (r.symbol_name = @query COLLATE NOCASE OR (r.symbol_name = @queryAttributeAlias COLLATE NOCASE{totalAliasScope}){(allowSqlLeafFallback ? $" OR (r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE{sqlLeafTotalScope})" : string.Empty)})"
                    : totalCssScssVariableAlias != null
                        ? $" AND (r.symbol_name = @query COLLATE NOCASE OR (r.symbol_name = @queryCssScssVariableAlias COLLATE NOCASE{totalCssScssVariableAliasScope}){(allowSqlLeafFallback ? $" OR (r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE{sqlLeafTotalScope})" : string.Empty)})"
                        : allowSqlLeafFallback
                        ? $" AND (r.symbol_name = @query COLLATE NOCASE OR (r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE{sqlLeafTotalScope}))"
                        : " AND r.symbol_name = @query COLLATE NOCASE";
            else
                innerSql += totalSuffixAlias != null
                    ? $" AND (r.symbol_name LIKE @query ESCAPE '\\' OR (r.symbol_name = @queryAttributeAlias COLLATE NOCASE{totalAliasScope}) OR (r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE{sqlLeafTotalScope}))"
                    : totalCssScssVariableAlias != null
                        ? $" AND (r.symbol_name LIKE @query ESCAPE '\\' OR (r.symbol_name = @queryCssScssVariableAlias COLLATE NOCASE{totalCssScssVariableAliasScope}) OR (r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE{sqlLeafTotalScope}))"
                        : $" AND (r.symbol_name LIKE @query ESCAPE '\\' OR (r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE{sqlLeafTotalScope}))";
        }
        if (referenceKind != null)
            innerSql += " AND r.reference_kind = @referenceKind";
        if (lang != null)
            innerSql += " AND f.lang = @lang";
        AppendPathFilters(ref innerSql, pathPatterns, excludePathPatterns, excludeTests);
        if (referenceKind == null)
            innerSql += $" GROUP BY f.path, f.lang, r.file_id, r.symbol_name, r.line, r.column_number, {GetLogicalReferenceKindSql("r.reference_kind")}";
        innerSql += ")";

        cmd.CommandText = $"SELECT COUNT(*), COUNT(DISTINCT path), MAX(CASE WHEN lang = 'sql' THEN 1 ELSE 0 END) FROM ({innerSql})";
        if (query != null)
        {
            var value = !exact
                ? $"%{EscapeLikeQuery(query)}%"
                : _foldReady
                    ? NameFold.Fold(query) ?? query
                    : query;
            cmd.Parameters.AddWithValue("@query", value);
            cmd.Parameters.AddWithValue("@aliasQuery", query);
            cmd.Parameters.AddWithValue("@aliasQueryLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(query)) ?? SqlNameResolver.GetLeafName(query));
            if (totalSuffixAlias != null)
            {
                var aliasParam = exact && _foldReady
                    ? NameFold.Fold(totalSuffixAlias) ?? totalSuffixAlias
                    : totalSuffixAlias;
                cmd.Parameters.AddWithValue("@queryAttributeAlias", aliasParam);
            }
            if (totalCssScssVariableAlias != null)
            {
                var aliasParam = exact && _foldReady
                    ? NameFold.Fold(totalCssScssVariableAlias) ?? totalCssScssVariableAlias
                    : totalCssScssVariableAlias;
                cmd.Parameters.AddWithValue("@queryCssScssVariableAlias", aliasParam);
            }
        }
        if (referenceKind != null)
            cmd.Parameters.AddWithValue("@referenceKind", referenceKind);
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", NormalizeQueryLanguage(lang));
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);

        return ExecuteCountSummary(cmd);
    }

    /// <summary>
    /// Find callers for a referenced symbol.
    /// 指定シンボルを呼び出している呼び出し元を探す。
    /// </summary>
    public List<CallerResult> GetCallers(string query, int limit = 20, string? lang = null, string? referenceKind = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false)
    {
        if (string.IsNullOrWhiteSpace(query) || IsBareVerbatimQueryToken(query))
            return new List<CallerResult>();
        lang = NormalizeQueryLanguage(lang);
        query = NormalizeSymbolSearchQuery(query, lang, exact) ?? query ?? string.Empty;
        if (!_hasReferencesTable) return new List<CallerResult>();
        using var cmd = _conn.CreateCommand();
        var referenceLineJoin = ReferenceLineJoinSql("r");
        var contextSql = ReferenceContextSql("r");
        var callerContainerPredicate = BuildCallerContainerPredicate("f", "r");
        var supportedLangPredicate = BuildGraphSupportedLanguagePredicate(cmd, "f", "graphLang");

        var sql = referenceKind == null
            ? @"
            WITH logical_references AS (
                SELECT f.path, f.lang, r.container_kind, r.container_name, r.symbol_name,
                       " + GetGroupedCallerReferenceKindSql("r.reference_kind") + @" AS reference_kind,
                       r.line
                FROM symbol_references r
                JOIN files f ON r.file_id = f.id" + referenceLineJoin + @"
                WHERE " + callerContainerPredicate + @"
                  AND r.reference_kind IN " + CallGraphReferenceKindsSql + @"
                  AND " + supportedLangPredicate
            : @"
            SELECT f.path, f.lang, " + BuildCallerKindProjectionSql("r") + @" AS container_kind, " + BuildCallerNameProjectionSql("r") + @" AS container_name, r.symbol_name,
                   r.reference_kind, MIN(r.line) AS first_line, COUNT(*) AS reference_count,
                   GROUP_CONCAT(DISTINCT r.reference_kind) AS reference_kinds
            FROM symbol_references r
            JOIN files f ON r.file_id = f.id" + referenceLineJoin + @"
            WHERE " + BuildCallerContainerPredicate("f", "r");
        if (referenceKind != null)
            sql += " AND " + supportedLangPredicate;

        if (referenceKind != null)
            sql += " AND r.reference_kind = @referenceKind";
        else
            sql += NonInvocationReferenceKindsExclusion;
        var allowSqlLeafFallback = AllowSqlLeafFallbackForQuery(query);
        var useSqlQualifiedContextMatch = SqlNameResolver.HasQualifier(query);
        var cssScssVariableAlias = ComputeCssScssVariableAlias(query);
        var cssScssVariableAliasScope = cssScssVariableAlias != null
            ? " AND f.lang = 'css'"
            : string.Empty;
        if (useSqlQualifiedContextMatch && exact && _foldReady)
            sql += $" AND (((f.lang = 'sql') AND sql_context_has_name_folded_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name_folded = @query))";
        else if (useSqlQualifiedContextMatch && exact)
            sql += $" AND (((f.lang = 'sql') AND sql_context_has_name_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name = @query COLLATE NOCASE))";
        else if (useSqlQualifiedContextMatch && _foldReady)
            sql += $" AND (((f.lang = 'sql') AND sql_context_like_name_folded_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name LIKE @query ESCAPE '\\'))";
        else if (useSqlQualifiedContextMatch)
            sql += $" AND (((f.lang = 'sql') AND sql_context_like_name_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name LIKE @query ESCAPE '\\'))";
        else if (exact && _foldReady)
            sql += allowSqlLeafFallback
                ? cssScssVariableAlias != null
                    ? $" AND (r.symbol_name_folded = @query OR (r.symbol_name_folded = @queryCssScssVariableAlias{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND r.symbol_name_folded = @aliasQueryLeafFolded))"
                    : " AND (r.symbol_name_folded = @query OR (f.lang = 'sql' AND r.symbol_name_folded = @aliasQueryLeafFolded))"
                : " AND r.symbol_name_folded = @query";
        else if (exact)
            sql += allowSqlLeafFallback
                ? cssScssVariableAlias != null
                    ? $" AND (r.symbol_name = @query COLLATE NOCASE OR (r.symbol_name = @queryCssScssVariableAlias COLLATE NOCASE{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE))"
                    : " AND (r.symbol_name = @query COLLATE NOCASE OR (f.lang = 'sql' AND r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE))"
                : " AND r.symbol_name = @query COLLATE NOCASE";
        else
            sql += cssScssVariableAlias != null
                ? $" AND (r.symbol_name LIKE @query ESCAPE '\\' OR (r.symbol_name = @queryCssScssVariableAlias COLLATE NOCASE{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE))"
                : " AND (r.symbol_name LIKE @query ESCAPE '\\' OR (f.lang = 'sql' AND r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE))";
        if (lang != null)
            sql += " AND f.lang = @lang";
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        if (referenceKind == null)
        {
            sql += @"
                GROUP BY f.path, f.lang, r.container_kind, r.container_name, r.symbol_name, r.file_id, r.line, r.column_number
            )
            SELECT path, lang, " + BuildCallerKindProjectionSql("r") + @" AS container_kind, " + BuildCallerNameProjectionSql("r") + @" AS container_name, symbol_name,
                   " + GetGroupedCallerReferenceKindSql("r.reference_kind") + @" AS reference_kind,
                   MIN(line) AS first_line, COUNT(*) AS reference_count,
                   GROUP_CONCAT(DISTINCT r.reference_kind) AS reference_kinds
            FROM logical_references r
            GROUP BY path, lang, container_kind, container_name, symbol_name";
        }
        else
        {
            sql += " GROUP BY f.path, f.lang, r.container_kind, r.container_name, r.symbol_name";
        }
        sql += $" ORDER BY CASE WHEN @preferExactCase = 1 AND r.symbol_name = @rawQuery THEN 0 ELSE 1 END, {(referenceKind == null ? GetPathBucketOrderSql("r.path") : PathBucketOrder)}, CASE WHEN lower(r.symbol_name) = lower(@rankingQuery) THEN 0 ELSE 1 END, reference_count DESC, {(referenceKind == null ? "r.path" : "f.path")}, first_line LIMIT @limit";

        cmd.CommandText = sql;
        string callersQueryParam;
        if (!exact)
            callersQueryParam = $"%{EscapeLikeQuery(query)}%";
        else if (_foldReady)
            callersQueryParam = NameFold.Fold(query) ?? query;
        else
            callersQueryParam = query;
        cmd.Parameters.AddWithValue("@query", callersQueryParam);
        cmd.Parameters.AddWithValue("@aliasQuery", query);
        cmd.Parameters.AddWithValue("@aliasQueryLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(query)) ?? SqlNameResolver.GetLeafName(query));
        if (cssScssVariableAlias != null)
        {
            var aliasParam = exact && _foldReady
                ? NameFold.Fold(cssScssVariableAlias) ?? cssScssVariableAlias
                : cssScssVariableAlias;
            cmd.Parameters.AddWithValue("@queryCssScssVariableAlias", aliasParam);
        }
        cmd.Parameters.AddWithValue("@preferExactCase", exact ? 1 : 0);
        cmd.Parameters.AddWithValue("@rawQuery", exact ? query : string.Empty);
        cmd.Parameters.AddWithValue("@rankingQuery", query.Trim());
        if (referenceKind != null)
            cmd.Parameters.AddWithValue("@referenceKind", referenceKind);
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", NormalizeQueryLanguage(lang));
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<CallerResult>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            var primaryKind = reader.GetString(5);
            var kinds = ParseDistinctReferenceKinds(GetNullableString(reader, 8), primaryKind);
            results.Add(new CallerResult
            {
                Path = reader.GetString(0),
                Lang = GetNullableString(reader, 1),
                CallerKind = GetNullableString(reader, 2),
                CallerName = GetNullableString(reader, 3),
                CalleeName = reader.GetString(4),
                ReferenceKind = primaryKind,
                ReferenceKinds = kinds,
                HasMixedReferenceKinds = kinds.Count > 1,
                FirstLine = reader.GetInt32(6),
                ReferenceCount = reader.GetInt32(7),
            });
        }
        return results;
    }

    public int CountCallers(string query, int limit = 20, string? lang = null, string? referenceKind = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false)
    {
        if (string.IsNullOrWhiteSpace(query) || IsBareVerbatimQueryToken(query))
            return 0;
        lang = NormalizeQueryLanguage(lang);
        query = NormalizeSymbolSearchQuery(query, lang, exact) ?? query ?? string.Empty;
        if (!_hasReferencesTable) return 0;
        using var cmd = _conn.CreateCommand();
        var referenceLineJoin = ReferenceLineJoinSql("r");
        var contextSql = ReferenceContextSql("r");
        var groupedSql = @"
            SELECT path, lang, container_kind, container_name, symbol_name
            FROM (
                SELECT f.path AS path, f.lang AS lang, r.container_kind AS container_kind,
                       r.container_name AS container_name, r.symbol_name AS symbol_name
            FROM symbol_references r
            JOIN files f ON r.file_id = f.id" + referenceLineJoin + @"
            WHERE " + BuildCallerContainerPredicate("f", "r");
        groupedSql += $" AND {BuildGraphSupportedLanguagePredicate(cmd, "f", "graphLang")}";

        if (referenceKind != null)
            groupedSql += " AND r.reference_kind = @referenceKind";
        else
            groupedSql += $" AND r.reference_kind IN {CallGraphReferenceKindsSql}";
        var allowSqlLeafFallback = AllowSqlLeafFallbackForQuery(query);
        var useSqlQualifiedContextMatch = SqlNameResolver.HasQualifier(query);
        var cssScssVariableAlias = ComputeCssScssVariableAlias(query);
        var cssScssVariableAliasScope = cssScssVariableAlias != null
            ? " AND f.lang = 'css'"
            : string.Empty;
        if (useSqlQualifiedContextMatch && exact && _foldReady)
            groupedSql += $" AND (((f.lang = 'sql') AND sql_context_has_name_folded_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name_folded = @query))";
        else if (useSqlQualifiedContextMatch && exact)
            groupedSql += $" AND (((f.lang = 'sql') AND sql_context_has_name_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name = @query COLLATE NOCASE))";
        else if (useSqlQualifiedContextMatch && _foldReady)
            groupedSql += $" AND (((f.lang = 'sql') AND sql_context_like_name_folded_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name LIKE @query ESCAPE '\\'))";
        else if (useSqlQualifiedContextMatch)
            groupedSql += $" AND (((f.lang = 'sql') AND sql_context_like_name_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name LIKE @query ESCAPE '\\'))";
        else if (exact && _foldReady)
            groupedSql += allowSqlLeafFallback
                ? cssScssVariableAlias != null
                    ? $" AND (r.symbol_name_folded = @query OR (r.symbol_name_folded = @queryCssScssVariableAlias{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND r.symbol_name_folded = @aliasQueryLeafFolded))"
                    : " AND (r.symbol_name_folded = @query OR (f.lang = 'sql' AND r.symbol_name_folded = @aliasQueryLeafFolded))"
                : " AND r.symbol_name_folded = @query";
        else if (exact)
            groupedSql += allowSqlLeafFallback
                ? cssScssVariableAlias != null
                    ? $" AND (r.symbol_name = @query COLLATE NOCASE OR (r.symbol_name = @queryCssScssVariableAlias COLLATE NOCASE{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE))"
                    : " AND (r.symbol_name = @query COLLATE NOCASE OR (f.lang = 'sql' AND r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE))"
                : " AND r.symbol_name = @query COLLATE NOCASE";
        else
            groupedSql += cssScssVariableAlias != null
                ? $" AND (r.symbol_name LIKE @query ESCAPE '\\' OR (r.symbol_name = @queryCssScssVariableAlias COLLATE NOCASE{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE))"
                : " AND (r.symbol_name LIKE @query ESCAPE '\\' OR (f.lang = 'sql' AND r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE))";
        if (lang != null)
            groupedSql += " AND f.lang = @lang";
        AppendPathFilters(ref groupedSql, pathPatterns, excludePathPatterns, excludeTests);
        if (referenceKind == null)
            groupedSql += $" GROUP BY f.path, f.lang, r.container_kind, r.container_name, r.symbol_name, r.file_id, r.line, r.column_number, {GetLogicalReferenceKindSql("r.reference_kind")}";
        groupedSql += " ) grouped_call_sites GROUP BY path, lang, container_kind, container_name, symbol_name LIMIT @limit";

        cmd.CommandText = $"SELECT COUNT(*) FROM ({groupedSql})";
        var value = !exact
            ? $"%{EscapeLikeQuery(query)}%"
            : _foldReady
                ? NameFold.Fold(query) ?? query
                : query;
        cmd.Parameters.AddWithValue("@query", value);
        cmd.Parameters.AddWithValue("@aliasQuery", query);
        cmd.Parameters.AddWithValue("@aliasQueryLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(query)) ?? SqlNameResolver.GetLeafName(query));
        if (cssScssVariableAlias != null)
        {
            var aliasParam = exact && _foldReady
                ? NameFold.Fold(cssScssVariableAlias) ?? cssScssVariableAlias
                : cssScssVariableAlias;
            cmd.Parameters.AddWithValue("@queryCssScssVariableAlias", aliasParam);
        }
        if (referenceKind != null)
            cmd.Parameters.AddWithValue("@referenceKind", referenceKind);
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", NormalizeQueryLanguage(lang));
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        cmd.Parameters.AddWithValue("@limit", limit);

        var raw = cmd.ExecuteScalar();
        return raw is long l ? (int)l : Convert.ToInt32(raw);
    }

    public QueryCountResult CountCallersTotal(string query, string? lang = null, string? referenceKind = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false)
    {
        if (!_hasReferencesTable)
            return new QueryCountResult(0, 0);

        using var cmd = _conn.CreateCommand();
        var referenceLineJoin = ReferenceLineJoinSql("r");
        var contextSql = ReferenceContextSql("r");
        var cssScssVariableAlias = ComputeCssScssVariableAlias(query);
        var cssScssVariableAliasScope = cssScssVariableAlias != null
            ? " AND f.lang = 'css'"
            : string.Empty;
        var groupedSql = @"
            SELECT path, lang
            FROM (
                SELECT f.path AS path, f.lang AS lang, r.container_kind AS container_kind,
                       r.container_name AS container_name, r.symbol_name AS symbol_name
                FROM symbol_references r
                JOIN files f ON r.file_id = f.id" + referenceLineJoin + @"
                WHERE " + BuildCallerContainerPredicate("f", "r");
        groupedSql += $" AND {BuildGraphSupportedLanguagePredicate(cmd, "f", "graphLang")}";

        if (referenceKind != null)
            groupedSql += " AND r.reference_kind = @referenceKind";
        else
            groupedSql += $" AND r.reference_kind IN {CallGraphReferenceKindsSql}";
        var allowSqlLeafFallback = AllowSqlLeafFallbackForQuery(query);
        var useSqlQualifiedContextMatch = SqlNameResolver.HasQualifier(query);
        if (useSqlQualifiedContextMatch && exact && _foldReady)
            groupedSql += $" AND (((f.lang = 'sql') AND sql_context_has_name_folded_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name_folded = @query))";
        else if (useSqlQualifiedContextMatch && exact)
            groupedSql += $" AND (((f.lang = 'sql') AND sql_context_has_name_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name = @query COLLATE NOCASE))";
        else if (useSqlQualifiedContextMatch && _foldReady)
            groupedSql += $" AND (((f.lang = 'sql') AND sql_context_like_name_folded_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name LIKE @query ESCAPE '\\'))";
        else if (useSqlQualifiedContextMatch)
            groupedSql += $" AND (((f.lang = 'sql') AND sql_context_like_name_at({contextSql}, @aliasQuery, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name LIKE @query ESCAPE '\\'))";
        else if (exact && _foldReady)
            groupedSql += allowSqlLeafFallback
                ? cssScssVariableAlias != null
                    ? $" AND (r.symbol_name_folded = @query OR (r.symbol_name_folded = @queryCssScssVariableAlias{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND r.symbol_name_folded = @aliasQueryLeafFolded))"
                    : " AND (r.symbol_name_folded = @query OR (f.lang = 'sql' AND r.symbol_name_folded = @aliasQueryLeafFolded))"
                : " AND r.symbol_name_folded = @query";
        else if (exact)
            groupedSql += allowSqlLeafFallback
                ? cssScssVariableAlias != null
                    ? $" AND (r.symbol_name = @query COLLATE NOCASE OR (r.symbol_name = @queryCssScssVariableAlias COLLATE NOCASE{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE))"
                    : " AND (r.symbol_name = @query COLLATE NOCASE OR (f.lang = 'sql' AND r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE))"
                : " AND r.symbol_name = @query COLLATE NOCASE";
        else
            groupedSql += cssScssVariableAlias != null
                ? $" AND (r.symbol_name LIKE @query ESCAPE '\\' OR (r.symbol_name = @queryCssScssVariableAlias COLLATE NOCASE{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE))"
                : " AND (r.symbol_name LIKE @query ESCAPE '\\' OR (f.lang = 'sql' AND r.symbol_name = sql_leaf_name(@aliasQuery) COLLATE NOCASE))";
        if (lang != null)
            groupedSql += " AND f.lang = @lang";
        AppendPathFilters(ref groupedSql, pathPatterns, excludePathPatterns, excludeTests);
        if (referenceKind == null)
            groupedSql += $" GROUP BY f.path, f.lang, r.container_kind, r.container_name, r.symbol_name, r.file_id, r.line, r.column_number, {GetLogicalReferenceKindSql("r.reference_kind")}";
        groupedSql += " ) grouped_call_sites GROUP BY path, lang, container_kind, container_name, symbol_name";

        cmd.CommandText = $"SELECT COUNT(*), COUNT(DISTINCT path), MAX(CASE WHEN lang = 'sql' THEN 1 ELSE 0 END) FROM ({groupedSql})";
        var value = !exact
            ? $"%{EscapeLikeQuery(query)}%"
            : _foldReady
                ? NameFold.Fold(query) ?? query
                : query;
        cmd.Parameters.AddWithValue("@query", value);
        cmd.Parameters.AddWithValue("@aliasQuery", query);
        cmd.Parameters.AddWithValue("@aliasQueryLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(query)) ?? SqlNameResolver.GetLeafName(query));
        if (cssScssVariableAlias != null)
        {
            var aliasParam = exact && _foldReady
                ? NameFold.Fold(cssScssVariableAlias) ?? cssScssVariableAlias
                : cssScssVariableAlias;
            cmd.Parameters.AddWithValue("@queryCssScssVariableAlias", aliasParam);
        }
        if (referenceKind != null)
            cmd.Parameters.AddWithValue("@referenceKind", referenceKind);
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", NormalizeQueryLanguage(lang));
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);

        return ExecuteCountSummary(cmd);
    }

    /// <summary>
    /// Find callees used by a caller/container symbol.
    /// 呼び出し元シンボルが使っている呼び出し先を探す。
    /// </summary>
    public List<CalleeResult> GetCallees(string query, int limit = 20, string? lang = null, string? referenceKind = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false)
    {
        if (string.IsNullOrWhiteSpace(query) || IsBareVerbatimQueryToken(query))
            return new List<CalleeResult>();
        lang = NormalizeQueryLanguage(lang);
        query = NormalizeSymbolSearchQuery(query, lang, exact) ?? query ?? string.Empty;
        if (!_hasReferencesTable) return new List<CalleeResult>();
        using var cmd = _conn.CreateCommand();

        var sql = referenceKind == null
            ? $@"
            WITH logical_references AS (
                SELECT f.path, f.lang, r.container_kind, r.container_name, r.symbol_name,
                       {GetPreferredReferenceKindSql("r.reference_kind")} AS reference_kind,
                       r.line
                FROM symbol_references r
                JOIN files f ON r.file_id = f.id
                WHERE r.container_name IS NOT NULL
                  AND r.reference_kind IN {CallGraphReferenceKindsSql}
                  AND {BuildGraphSupportedLanguagePredicate(cmd, "f", "graphLang")}"
            : @"
            SELECT f.path, f.lang, r.container_kind, r.container_name, r.symbol_name,
                   r.reference_kind, MIN(r.line) AS first_line, COUNT(*) AS reference_count,
                   GROUP_CONCAT(DISTINCT r.reference_kind) AS reference_kinds
            FROM symbol_references r
            JOIN files f ON r.file_id = f.id
            WHERE r.container_name IS NOT NULL";
        if (referenceKind != null)
            sql += $" AND {BuildGraphSupportedLanguagePredicate(cmd, "f", "graphLang")}";

        if (referenceKind != null)
            sql += " AND r.reference_kind = @referenceKind";
        else
            sql += NonInvocationReferenceKindsExclusion;
        var allowSqlLeafFallback = AllowSqlLeafFallbackForQuery(query);
        var useSqlQualifiedContainerMatch = SqlNameResolver.HasQualifier(query);
        var cssScssVariableAlias = ComputeCssScssVariableAlias(query);
        var cssScssVariableAliasScope = cssScssVariableAlias != null
            ? " AND f.lang = 'css'"
            : string.Empty;
        if (exact && useSqlQualifiedContainerMatch && _foldReady)
            sql += " AND (((f.lang = 'sql') AND sql_segment_count(r.container_name) = @aliasQuerySegmentCount AND sql_normalize_name_folded(r.container_name) = @aliasQueryNormalizedFolded) OR ((f.lang != 'sql') AND r.container_name_folded = @query))";
        else if (exact && useSqlQualifiedContainerMatch)
            sql += " AND (((f.lang = 'sql') AND sql_segment_count(r.container_name) = @aliasQuerySegmentCount AND sql_normalize_name(r.container_name) = @aliasQueryNormalized COLLATE NOCASE) OR ((f.lang != 'sql') AND r.container_name = @query COLLATE NOCASE))";
        else if (exact && _foldReady)
            sql += allowSqlLeafFallback
                ? cssScssVariableAlias != null
                    ? $" AND (r.container_name_folded = @query OR (r.container_name_folded = @queryCssScssVariableAlias{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND sql_leaf_name_folded(r.container_name) = @aliasQueryLeafFolded))"
                    : " AND (r.container_name_folded = @query OR (f.lang = 'sql' AND sql_leaf_name_folded(r.container_name) = @aliasQueryLeafFolded))"
                : " AND r.container_name_folded = @query";
        else if (exact)
            sql += allowSqlLeafFallback
                ? cssScssVariableAlias != null
                    ? $" AND (r.container_name = @query COLLATE NOCASE OR (r.container_name = @queryCssScssVariableAlias COLLATE NOCASE{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND sql_leaf_name(r.container_name) = @aliasQuery COLLATE NOCASE))"
                    : " AND (r.container_name = @query COLLATE NOCASE OR (f.lang = 'sql' AND sql_leaf_name(r.container_name) = @aliasQuery COLLATE NOCASE))"
                : " AND r.container_name = @query COLLATE NOCASE";
        else
            sql += cssScssVariableAlias != null
                ? $" AND (r.container_name LIKE @query ESCAPE '\\' OR (r.container_name = @queryCssScssVariableAlias COLLATE NOCASE{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND sql_leaf_name(r.container_name) = @aliasQuery COLLATE NOCASE))"
                : " AND (r.container_name LIKE @query ESCAPE '\\' OR (f.lang = 'sql' AND sql_leaf_name(r.container_name) = @aliasQuery COLLATE NOCASE))";
        if (lang != null)
            sql += " AND f.lang = @lang";
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        if (referenceKind == null)
        {
            sql += @"
                GROUP BY f.path, f.lang, r.container_kind, r.container_name, r.symbol_name, r.file_id, r.line, r.column_number
            )
            SELECT path, lang, container_kind, container_name, symbol_name,
                   reference_kind, MIN(line) AS first_line, COUNT(*) AS reference_count,
                   GROUP_CONCAT(DISTINCT reference_kind) AS reference_kinds
            FROM logical_references r
            GROUP BY path, lang, container_kind, container_name, symbol_name, reference_kind";
        }
        else
        {
            sql += " GROUP BY f.path, f.lang, r.container_kind, r.container_name, r.symbol_name, r.reference_kind";
        }
        sql += $" ORDER BY CASE WHEN @preferExactCase = 1 AND r.container_name = @rawQuery THEN 0 ELSE 1 END, {(referenceKind == null ? GetPathBucketOrderSql("r.path") : PathBucketOrder)}, CASE WHEN lower(r.container_name) = lower(@rankingQuery) THEN 0 ELSE 1 END, reference_count DESC, {(referenceKind == null ? "r.path" : "f.path")}, first_line LIMIT @limit";

        cmd.CommandText = sql;
        string calleesQueryParam;
        if (!exact)
            calleesQueryParam = $"%{EscapeLikeQuery(query)}%";
        else if (_foldReady)
            calleesQueryParam = NameFold.Fold(query) ?? query;
        else
            calleesQueryParam = query;
        cmd.Parameters.AddWithValue("@query", calleesQueryParam);
        cmd.Parameters.AddWithValue("@aliasQuery", query);
        cmd.Parameters.AddWithValue("@aliasQueryLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(query)) ?? SqlNameResolver.GetLeafName(query));
        cmd.Parameters.AddWithValue("@aliasQueryNormalized", SqlNameResolver.NormalizeQualifiedName(query));
        cmd.Parameters.AddWithValue("@aliasQueryNormalizedFolded", NameFold.Fold(SqlNameResolver.NormalizeQualifiedName(query)) ?? SqlNameResolver.NormalizeQualifiedName(query));
        cmd.Parameters.AddWithValue("@aliasQuerySegmentCount", SqlNameResolver.GetSegmentCount(query));
        if (cssScssVariableAlias != null)
        {
            var aliasParam = exact && _foldReady
                ? NameFold.Fold(cssScssVariableAlias) ?? cssScssVariableAlias
                : cssScssVariableAlias;
            cmd.Parameters.AddWithValue("@queryCssScssVariableAlias", aliasParam);
        }
        cmd.Parameters.AddWithValue("@preferExactCase", exact ? 1 : 0);
        cmd.Parameters.AddWithValue("@rawQuery", exact ? query : string.Empty);
        cmd.Parameters.AddWithValue("@rankingQuery", query.Trim());
        if (referenceKind != null)
            cmd.Parameters.AddWithValue("@referenceKind", referenceKind);
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", lang);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<CalleeResult>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            var primaryKind = reader.GetString(5);
            var kinds = ParseDistinctReferenceKinds(GetNullableString(reader, 8), primaryKind);
            results.Add(new CalleeResult
            {
                Path = reader.GetString(0),
                Lang = GetNullableString(reader, 1),
                CallerKind = GetNullableString(reader, 2),
                CallerName = GetNullableString(reader, 3),
                CalleeName = reader.GetString(4),
                ReferenceKind = primaryKind,
                ReferenceKinds = kinds,
                HasMixedReferenceKinds = kinds.Count > 1,
                FirstLine = reader.GetInt32(6),
                ReferenceCount = reader.GetInt32(7),
            });
        }
        return results;
    }

    public int CountCallees(string query, int limit = 20, string? lang = null, string? referenceKind = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false)
    {
        if (string.IsNullOrWhiteSpace(query) || IsBareVerbatimQueryToken(query))
            return 0;
        lang = NormalizeQueryLanguage(lang);
        query = NormalizeSymbolSearchQuery(query, lang, exact) ?? query ?? string.Empty;
        if (!_hasReferencesTable) return 0;
        using var cmd = _conn.CreateCommand();
        var groupedSql = @"
            SELECT path, lang, container_kind, container_name, symbol_name, reference_kind
            FROM (
                SELECT f.path AS path, f.lang AS lang, r.container_kind AS container_kind,
                       r.container_name AS container_name, r.symbol_name AS symbol_name,
                       " + (referenceKind == null
                           ? GetPreferredReferenceKindSql("r.reference_kind")
                           : "r.reference_kind") + @" AS reference_kind
            FROM symbol_references r
            JOIN files f ON r.file_id = f.id
            WHERE r.container_name IS NOT NULL";
        groupedSql += $" AND {BuildGraphSupportedLanguagePredicate(cmd, "f", "graphLang")}";

        if (referenceKind != null)
            groupedSql += " AND r.reference_kind = @referenceKind";
        else
            groupedSql += $" AND r.reference_kind IN {CallGraphReferenceKindsSql}";
        var allowSqlLeafFallback = AllowSqlLeafFallbackForQuery(query);
        var useSqlQualifiedContainerMatch = SqlNameResolver.HasQualifier(query);
        var cssScssVariableAlias = ComputeCssScssVariableAlias(query);
        var cssScssVariableAliasScope = cssScssVariableAlias != null
            ? " AND f.lang = 'css'"
            : string.Empty;
        if (exact && useSqlQualifiedContainerMatch && _foldReady)
            groupedSql += " AND (((f.lang = 'sql') AND sql_segment_count(r.container_name) = @aliasQuerySegmentCount AND sql_normalize_name_folded(r.container_name) = @aliasQueryNormalizedFolded) OR ((f.lang != 'sql') AND r.container_name_folded = @query))";
        else if (exact && useSqlQualifiedContainerMatch)
            groupedSql += " AND (((f.lang = 'sql') AND sql_segment_count(r.container_name) = @aliasQuerySegmentCount AND sql_normalize_name(r.container_name) = @aliasQueryNormalized COLLATE NOCASE) OR ((f.lang != 'sql') AND r.container_name = @query COLLATE NOCASE))";
        else if (exact && _foldReady)
            groupedSql += allowSqlLeafFallback
                ? cssScssVariableAlias != null
                    ? $" AND (r.container_name_folded = @query OR (r.container_name_folded = @queryCssScssVariableAlias{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND sql_leaf_name_folded(r.container_name) = @aliasQueryLeafFolded))"
                    : " AND (r.container_name_folded = @query OR (f.lang = 'sql' AND sql_leaf_name_folded(r.container_name) = @aliasQueryLeafFolded))"
                : " AND r.container_name_folded = @query";
        else if (exact)
            groupedSql += allowSqlLeafFallback
                ? cssScssVariableAlias != null
                    ? $" AND (r.container_name = @query COLLATE NOCASE OR (r.container_name = @queryCssScssVariableAlias COLLATE NOCASE{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND sql_leaf_name(r.container_name) = @aliasQuery COLLATE NOCASE))"
                    : " AND (r.container_name = @query COLLATE NOCASE OR (f.lang = 'sql' AND sql_leaf_name(r.container_name) = @aliasQuery COLLATE NOCASE))"
                : " AND r.container_name = @query COLLATE NOCASE";
        else
            groupedSql += cssScssVariableAlias != null
                ? $" AND (r.container_name LIKE @query ESCAPE '\\' OR (r.container_name = @queryCssScssVariableAlias COLLATE NOCASE{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND sql_leaf_name(r.container_name) = @aliasQuery COLLATE NOCASE))"
                : " AND (r.container_name LIKE @query ESCAPE '\\' OR (f.lang = 'sql' AND sql_leaf_name(r.container_name) = @aliasQuery COLLATE NOCASE))";
        if (lang != null)
            groupedSql += " AND f.lang = @lang";
        AppendPathFilters(ref groupedSql, pathPatterns, excludePathPatterns, excludeTests);
        if (referenceKind == null)
            groupedSql += $" GROUP BY f.path, f.lang, r.container_kind, r.container_name, r.symbol_name, r.file_id, r.line, r.column_number, {GetLogicalReferenceKindSql("r.reference_kind")}";
        groupedSql += " ) grouped_call_sites GROUP BY path, lang, container_kind, container_name, symbol_name, reference_kind LIMIT @limit";

        cmd.CommandText = $"SELECT COUNT(*) FROM ({groupedSql})";
        var value = !exact
            ? $"%{EscapeLikeQuery(query)}%"
            : _foldReady
                ? NameFold.Fold(query) ?? query
                : query;
        cmd.Parameters.AddWithValue("@query", value);
        cmd.Parameters.AddWithValue("@aliasQuery", query);
        cmd.Parameters.AddWithValue("@aliasQueryLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(query)) ?? SqlNameResolver.GetLeafName(query));
        cmd.Parameters.AddWithValue("@aliasQueryNormalized", SqlNameResolver.NormalizeQualifiedName(query));
        cmd.Parameters.AddWithValue("@aliasQueryNormalizedFolded", NameFold.Fold(SqlNameResolver.NormalizeQualifiedName(query)) ?? SqlNameResolver.NormalizeQualifiedName(query));
        cmd.Parameters.AddWithValue("@aliasQuerySegmentCount", SqlNameResolver.GetSegmentCount(query));
        if (cssScssVariableAlias != null)
        {
            var aliasParam = exact && _foldReady
                ? NameFold.Fold(cssScssVariableAlias) ?? cssScssVariableAlias
                : cssScssVariableAlias;
            cmd.Parameters.AddWithValue("@queryCssScssVariableAlias", aliasParam);
        }
        if (referenceKind != null)
            cmd.Parameters.AddWithValue("@referenceKind", referenceKind);
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", lang);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        cmd.Parameters.AddWithValue("@limit", limit);

        var raw = cmd.ExecuteScalar();
        return raw is long l ? (int)l : Convert.ToInt32(raw);
    }

    public QueryCountResult CountCalleesTotal(string query, string? lang = null, string? referenceKind = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false)
    {
        lang = NormalizeQueryLanguage(lang);
        if (!_hasReferencesTable)
            return new QueryCountResult(0, 0);

        using var cmd = _conn.CreateCommand();
        var groupedSql = @"
            SELECT path, lang
            FROM (
                SELECT f.path AS path, f.lang AS lang, r.container_kind AS container_kind,
                       r.container_name AS container_name, r.symbol_name AS symbol_name,
                       " + (referenceKind == null
                           ? GetPreferredReferenceKindSql("r.reference_kind")
                           : "r.reference_kind") + @" AS reference_kind
                FROM symbol_references r
                JOIN files f ON r.file_id = f.id
                WHERE r.container_name IS NOT NULL";
        groupedSql += $" AND {BuildGraphSupportedLanguagePredicate(cmd, "f", "graphLang")}";

        if (referenceKind != null)
            groupedSql += " AND r.reference_kind = @referenceKind";
        else
            groupedSql += $" AND r.reference_kind IN {CallGraphReferenceKindsSql}";
        var allowSqlLeafFallback = AllowSqlLeafFallbackForQuery(query);
        var useSqlQualifiedContainerMatch = SqlNameResolver.HasQualifier(query);
        var cssScssVariableAlias = ComputeCssScssVariableAlias(query);
        var cssScssVariableAliasScope = cssScssVariableAlias != null
            ? " AND f.lang = 'css'"
            : string.Empty;
        if (exact && useSqlQualifiedContainerMatch && _foldReady)
            groupedSql += " AND (((f.lang = 'sql') AND sql_segment_count(r.container_name) = @aliasQuerySegmentCount AND sql_normalize_name_folded(r.container_name) = @aliasQueryNormalizedFolded) OR ((f.lang != 'sql') AND r.container_name_folded = @query))";
        else if (exact && useSqlQualifiedContainerMatch)
            groupedSql += " AND (((f.lang = 'sql') AND sql_segment_count(r.container_name) = @aliasQuerySegmentCount AND sql_normalize_name(r.container_name) = @aliasQueryNormalized COLLATE NOCASE) OR ((f.lang != 'sql') AND r.container_name = @query COLLATE NOCASE))";
        else if (exact && _foldReady)
            groupedSql += allowSqlLeafFallback
                ? cssScssVariableAlias != null
                    ? $" AND (r.container_name_folded = @query OR (r.container_name_folded = @queryCssScssVariableAlias{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND sql_leaf_name_folded(r.container_name) = @aliasQueryLeafFolded))"
                    : " AND (r.container_name_folded = @query OR (f.lang = 'sql' AND sql_leaf_name_folded(r.container_name) = @aliasQueryLeafFolded))"
                : " AND r.container_name_folded = @query";
        else if (exact)
            groupedSql += allowSqlLeafFallback
                ? cssScssVariableAlias != null
                    ? $" AND (r.container_name = @query COLLATE NOCASE OR (r.container_name = @queryCssScssVariableAlias COLLATE NOCASE{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND sql_leaf_name(r.container_name) = @aliasQuery COLLATE NOCASE))"
                    : " AND (r.container_name = @query COLLATE NOCASE OR (f.lang = 'sql' AND sql_leaf_name(r.container_name) = @aliasQuery COLLATE NOCASE))"
                : " AND r.container_name = @query COLLATE NOCASE";
        else
            groupedSql += cssScssVariableAlias != null
                ? $" AND (r.container_name LIKE @query ESCAPE '\\' OR (r.container_name = @queryCssScssVariableAlias COLLATE NOCASE{cssScssVariableAliasScope}) OR (f.lang = 'sql' AND sql_leaf_name(r.container_name) = @aliasQuery COLLATE NOCASE))"
                : " AND (r.container_name LIKE @query ESCAPE '\\' OR (f.lang = 'sql' AND sql_leaf_name(r.container_name) = @aliasQuery COLLATE NOCASE))";
        if (lang != null)
            groupedSql += " AND f.lang = @lang";
        AppendPathFilters(ref groupedSql, pathPatterns, excludePathPatterns, excludeTests);
        if (referenceKind == null)
            groupedSql += $" GROUP BY f.path, f.lang, r.container_kind, r.container_name, r.symbol_name, r.file_id, r.line, r.column_number, {GetLogicalReferenceKindSql("r.reference_kind")}";
        groupedSql += " ) grouped_call_sites GROUP BY path, lang, container_kind, container_name, symbol_name, reference_kind";

        cmd.CommandText = $"SELECT COUNT(*), COUNT(DISTINCT path), MAX(CASE WHEN lang = 'sql' THEN 1 ELSE 0 END) FROM ({groupedSql})";
        var value = !exact
            ? $"%{EscapeLikeQuery(query)}%"
            : _foldReady
                ? NameFold.Fold(query) ?? query
                : query;
        cmd.Parameters.AddWithValue("@query", value);
        cmd.Parameters.AddWithValue("@aliasQuery", query);
        cmd.Parameters.AddWithValue("@aliasQueryLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(query)) ?? SqlNameResolver.GetLeafName(query));
        cmd.Parameters.AddWithValue("@aliasQueryNormalized", SqlNameResolver.NormalizeQualifiedName(query));
        cmd.Parameters.AddWithValue("@aliasQueryNormalizedFolded", NameFold.Fold(SqlNameResolver.NormalizeQualifiedName(query)) ?? SqlNameResolver.NormalizeQualifiedName(query));
        cmd.Parameters.AddWithValue("@aliasQuerySegmentCount", SqlNameResolver.GetSegmentCount(query));
        if (cssScssVariableAlias != null)
        {
            var aliasParam = exact && _foldReady
                ? NameFold.Fold(cssScssVariableAlias) ?? cssScssVariableAlias
                : cssScssVariableAlias;
            cmd.Parameters.AddWithValue("@queryCssScssVariableAlias", aliasParam);
        }
        if (referenceKind != null)
            cmd.Parameters.AddWithValue("@referenceKind", referenceKind);
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", lang);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);

        return ExecuteCountSummary(cmd);
    }

    /// <summary>
    /// Resolve a user-provided symbol name to its actual indexed casing via definition lookup.
    /// Prefers exact-case match, then falls back to case-insensitive. Only considers
    /// graph-supported languages. Returns the original input if no match is found.
    /// ユーザ入力のシンボル名を定義検索で実際のインデックス済みケーシングに解決する。
    /// 完全一致を優先し、なければ大文字小文字無視でフォールバック。graph 対応言語のみ対象。
    /// 見つからなければ元の入力をそのまま返す。
    /// </summary>
    private string ResolveSymbolName(string symbolName, string? lang)
    {
        var normalizedSymbolName = NormalizeCSharpVerbatimQuery(symbolName, lang) ?? symbolName;
        // Exact lookup mirrors the leaf `--exact` readers: folded equality when FoldReady,
        // ASCII `COLLATE NOCASE` fallback on legacy / partial-backfill DBs.
        // No path/test filters — definitions outside caller scope must still be found.
        // Only considers graph-supported languages to avoid resolving to unsupported ones.
        // FoldReady なら folded equality、legacy DB では ASCII `COLLATE NOCASE` にフォールバック。
        var normalizedName = SqlNameResolver.NormalizeQualifiedName(normalizedSymbolName);
        var leafName = SqlNameResolver.GetLeafName(normalizedSymbolName);
        var segmentCount = SqlNameResolver.GetSegmentCount(normalizedSymbolName);
        var allowLeafFallback = !SqlNameResolver.HasQualifier(normalizedSymbolName);
        using var cmd = _conn.CreateCommand();
        var supportedLangFilter = BuildGraphSupportedLanguagePredicate(cmd, "f", "resolveLang");
        var nameCondition = _foldReady
            ? allowLeafFallback
                ? "(s.name_folded = @nameFolded OR (f.lang = 'sql' AND ((sql_segment_count(s.name) = @segmentCount AND sql_normalize_name_folded(s.name) = @normalizedNameFolded) OR sql_leaf_name_folded(s.name) = @leafNameFolded)))"
                : "(s.name_folded = @nameFolded OR (f.lang = 'sql' AND sql_segment_count(s.name) = @segmentCount AND sql_normalize_name_folded(s.name) = @normalizedNameFolded))"
            : allowLeafFallback
                ? "(s.name = @name COLLATE NOCASE OR (f.lang = 'sql' AND ((sql_segment_count(s.name) = @segmentCount AND sql_normalize_name(s.name) = @normalizedName COLLATE NOCASE) OR sql_leaf_name(s.name) = @leafName COLLATE NOCASE)))"
                : "(s.name = @name COLLATE NOCASE OR (f.lang = 'sql' AND sql_segment_count(s.name) = @segmentCount AND sql_normalize_name(s.name) = @normalizedName COLLATE NOCASE))";
        cmd.CommandText = @"SELECT s.name FROM symbols s JOIN files f ON s.file_id = f.id
                            WHERE " + nameCondition + @"
                              AND " + supportedLangFilter + @"
                            ORDER BY CASE
                                         WHEN s.name = @name THEN 0
                                         WHEN f.lang = 'sql' AND sql_segment_count(s.name) = @segmentCount AND sql_normalize_name(s.name) = @normalizedName THEN 1
                                         WHEN f.lang = 'sql' AND sql_segment_count(s.name) = @segmentCount AND sql_normalize_name_folded(s.name) = @normalizedNameFolded THEN 2
                                         WHEN @allowLeafFallback = 1 AND f.lang = 'sql' AND sql_leaf_name(s.name) = @leafName THEN 3
                                         WHEN @allowLeafFallback = 1 AND f.lang = 'sql' AND sql_leaf_name_folded(s.name) = @leafNameFolded THEN 4
                                         ELSE 5
                                     END LIMIT 1";
        cmd.Parameters.AddWithValue("@name", normalizedSymbolName);
        cmd.Parameters.AddWithValue("@normalizedName", normalizedName);
        cmd.Parameters.AddWithValue("@normalizedNameFolded", NameFold.Fold(normalizedName) ?? normalizedName);
        cmd.Parameters.AddWithValue("@leafName", leafName);
        cmd.Parameters.AddWithValue("@leafNameFolded", NameFold.Fold(leafName) ?? leafName);
        cmd.Parameters.AddWithValue("@segmentCount", segmentCount);
        cmd.Parameters.AddWithValue("@allowLeafFallback", allowLeafFallback ? 1 : 0);
        if (_foldReady)
            cmd.Parameters.AddWithValue("@nameFolded", NameFold.Fold(normalizedSymbolName) ?? normalizedSymbolName);
        using var reader = cmd.ExecuteTrackedReader();
        return reader.TrackedRead() ? reader.GetString(0) : symbolName;
    }

    /// <summary>
    /// Find exact-match callers for BFS traversal. Uses per-row case sensitivity
    /// and filters to graph-supported languages only (preventing stale edges from
    /// unsupported languages leaking into results on pre-upgrade databases).
    /// BFS 走査用の完全一致 caller 検索。行ごとの case sensitivity 判定、
    /// かつ graph 対応言語のみにフィルタ（アップグレード前 DB の古いエッジ漏れを防止）。
    /// </summary>
    private List<CallerResult> GetCallersExact(string symbolName, int limit, int offset = 0, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false)
    {
        if (!_hasReferencesTable) return new List<CallerResult>();
        using var cmd = _conn.CreateCommand();
        var referenceLineJoin = ReferenceLineJoinSql("r");
        var contextSql = ReferenceContextSql("r");

        var supportedLangFilter = BuildGraphSupportedLanguagePredicate(cmd, "f", "callerLang");

        // Exact caller matching mirrors the leaf `--exact` readers: folded equality when
        // FoldReady, ASCII `COLLATE NOCASE` fallback on legacy / partial-backfill DBs.
        // ResolveSymbolName() already normalizes the root symbol first, so this catches
        // caller rows whose stored callee casing differs from the resolved definition.
        // caller 側も leaf `--exact` と同じく FoldReady なら folded equality、legacy DB では
        // `COLLATE NOCASE` fallback。definition と caller 行の casing 差もここで吸収する。
        var allowSqlLeafFallback = !SqlNameResolver.HasQualifier(symbolName);
        var nameCondition = _foldReady
            ? allowSqlLeafFallback
                ? @"
              AND (r.symbol_name_folded = @symbolNameFolded OR (f.lang = 'sql' AND r.symbol_name_folded = @symbolNameLeafFolded))"
                : @"
              AND (((f.lang = 'sql') AND sql_context_has_name_folded_at(" + contextSql + @", @symbolName, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name_folded = @symbolNameFolded))"
            : allowSqlLeafFallback
                ? @"
              AND (r.symbol_name = @symbolName COLLATE NOCASE OR (f.lang = 'sql' AND r.symbol_name = sql_leaf_name(@symbolName) COLLATE NOCASE))"
                : @"
              AND (((f.lang = 'sql') AND sql_context_has_name_at(" + contextSql + @", @symbolName, r.column_number) = 1) OR ((f.lang != 'sql') AND r.symbol_name = @symbolName COLLATE NOCASE))";

        // impact BFS must share the call-graph contract with `callers`/`callees`/`hotspots`,
        // so event subscriptions (`Click += OnClick`) also participate in the transitive
        // caller chain. Metadata edges (`attribute`, `annotation`) stay excluded.
        // impact の BFS は `callers`/`callees`/`hotspots` と同じ call-graph 契約を共有し、
        // `subscribe` エッジ（`Click += OnClick` 等）も推移 caller に含める。`attribute` /
        // `annotation` のような metadata エッジは引き続き除外する。
        var callerContainerPredicate = BuildCallerContainerPredicate("f", "r");
        var sql = $@"
            WITH logical_references AS (
                SELECT f.path, f.lang, r.container_kind, r.container_name, r.symbol_name, r.line
                FROM symbol_references r
                JOIN files f ON r.file_id = f.id{referenceLineJoin}
                WHERE {callerContainerPredicate}
                  AND r.reference_kind IN {CallGraphReferenceKindsSql}
                  AND {supportedLangFilter}
                  {nameCondition}";
        if (lang != null)
            sql += " AND f.lang = @lang";
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        sql += @"
                GROUP BY f.path, f.lang, r.container_kind, r.container_name, r.symbol_name, r.file_id, r.line, r.column_number
            )
            SELECT path, lang, " + BuildCallerKindProjectionSql("r") + @" AS container_kind, " + BuildCallerNameProjectionSql("r") + @" AS container_name, symbol_name,
                   MIN(line) AS first_line, COUNT(*) AS reference_count
            FROM logical_references r
            GROUP BY path, lang, container_kind, container_name, symbol_name";
        sql += $" ORDER BY {GetPathBucketOrderSql("r.path")}, reference_count DESC, r.path, COALESCE(r.container_name, ''), COALESCE(r.container_kind, ''), r.symbol_name, first_line LIMIT @limit OFFSET @offset";

        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@symbolName", symbolName);
        cmd.Parameters.AddWithValue("@symbolNameLeafFolded", NameFold.Fold(SqlNameResolver.GetLeafName(symbolName)) ?? SqlNameResolver.GetLeafName(symbolName));
        if (_foldReady)
            cmd.Parameters.AddWithValue("@symbolNameFolded", NameFold.Fold(symbolName) ?? symbolName);
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", lang);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", offset);

        var results = new List<CallerResult>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            results.Add(new CallerResult
            {
                Path = reader.GetString(0),
                Lang = GetNullableString(reader, 1),
                CallerKind = GetNullableString(reader, 2),
                CallerName = GetNullableString(reader, 3),
                CalleeName = reader.GetString(4),
                FirstLine = reader.GetInt32(5),
                ReferenceCount = reader.GetInt32(6),
            });
        }
        return results;
    }

    /// <summary>
    /// Compute transitive callers of a symbol using BFS with exact matching.
    /// Returns each unique caller in the call chain with its depth from the root symbol.
    /// Truncation is signaled via the Truncated property in results.
    /// 完全一致の BFS でシンボルの推移的呼び出し元を算出。各呼び出し元とルートシンボルからの深さを返す。
    /// 結果が切り詰められた場合は Truncated フラグで通知する。
    /// </summary>
    public (List<ImpactResult> Results, bool Truncated) GetTransitiveCallers(string symbolName, int maxDepth = 5, int limit = 50, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false)
    {
        // Resolve the symbol name through definitions first so case-mismatched queries
        // like "run" find the actual "Run" symbol. Falls back to user input if not found.
        // 定義を通じてシンボル名を解決し、"run" → "Run" のようなケース違いを補正する。
        // 見つからなければユーザ入力をフォールバック使用。
        var resolvedName = ResolveSymbolName(symbolName, lang);

        var results = new List<ImpactResult>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Symbol, int Depth)>();
        queue.Enqueue((resolvedName, 0));
        visited.Add(resolvedName);
        var truncated = false;
        // Safety cap to prevent infinite loops on pathological graphs / 病的グラフでの無限ループ防止
        const int maxFetchIterations = 1000;

        while (queue.Count > 0 && results.Count < limit)
        {
            var (currentSymbol, depth) = queue.Dequeue();
            if (depth > maxDepth)
                break;

            // Fetch callers in pages, filtering out already-visited before counting toward limit.
            // This prevents diamond graphs from hiding reachable callers behind visited duplicates.
            // ページングで caller を取得し、visited フィルタ後にカウント。
            // ダイヤモンド型グラフで到達可能な caller が visited 重複に隠れるのを防止。
            var needed = limit - results.Count;
            var offset = 0;
            const int pageSize = 200;
            var fetchIterations = 0;

            while (results.Count < limit && fetchIterations < maxFetchIterations)
            {
                fetchIterations++;
                var page = GetCallersExact(currentSymbol, pageSize, offset, lang, pathPatterns, excludePathPatterns, excludeTests);

                if (page.Count == 0)
                    break; // No more callers for this symbol / このシンボルの caller は尽きた

                foreach (var caller in page)
                {
                    if (results.Count >= limit)
                    {
                        truncated = true;
                        break;
                    }

                    var callerName = caller.CallerName ?? SyntheticTopLevelCallerName;
                    var key = $"{caller.Path}:{callerName}";

                    if (!visited.Add(key))
                        continue;

                    results.Add(new ImpactResult
                    {
                        Path = caller.Path,
                        Lang = caller.Lang,
                        CallerKind = caller.CallerKind,
                        CallerName = caller.CallerName,
                        CalleeName = caller.CalleeName,
                        Depth = depth + 1,
                        FirstLine = caller.FirstLine,
                        ReferenceCount = caller.ReferenceCount,
                    });

                    if (caller.CallerName != null
                        && caller.CallerName != SyntheticTopLevelCallerName
                        && depth + 1 < maxDepth)
                        queue.Enqueue((caller.CallerName, depth + 1));
                }

                offset += page.Count;

                // If this page was full, there might be more — continue paging
                // ページが満杯なら、まだある可能性 — ページングを継続
                if (page.Count < pageSize)
                    break;
            }

            // If fetch iteration cap was hit, mark as truncated / フェッチ反復上限に達した場合も truncated
            if (fetchIterations >= maxFetchIterations)
                truncated = true;
        }

        if (queue.Count > 0 && results.Count >= limit)
            truncated = true;

        return (results, truncated);
    }

    /// <summary>
    /// Analyze impact for a query by combining transitive callers with symbol-resolution
    /// metadata and a class-like file-dependency fallback when symbol-level callers are absent.
    /// impact 用に caller BFS と解決メタデータを束ね、class 系で caller 不在なら
    /// file dependency をフォールバックとして返す。
    /// </summary>
    public ImpactAnalysisResult AnalyzeImpact(string symbolName, int maxDepth = 5, int limit = 50, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false)
    {
        lang = NormalizeQueryLanguage(lang);
        var resolvedName = ResolveSymbolName(symbolName, lang);
        var definitions = ResolveImpactDefinitions(resolvedName, lang, pathPatterns, excludePathPatterns, excludeTests);
        var definitionPaths = definitions
            .Select(d => d.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var hasMultipleDefinitions = definitions.Count > 1;
        var fallbackDefinitions = definitions
            .Where(d => IsPreciseImpactFallbackKind(d.Kind))
            .ToList();
        var fallbackDefinitionPaths = fallbackDefinitions
            .Select(d => d.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var hasMultipleFallbackDefinitions = fallbackDefinitions.Count > 1;
        var hasMultipleFallbackDefinitionFiles = fallbackDefinitionPaths.Count > 1;
        var hasClassLikeDefinitions = fallbackDefinitions.Count > 0;

        if (maxDepth <= 0)
        {
            return new ImpactAnalysisResult
            {
                Query = symbolName,
                ResolvedName = resolvedName,
                ImpactMode = "none",
                Heuristic = false,
                MaxDepth = maxDepth,
                DefinitionCount = definitions.Count,
                DefinitionFileCount = definitionPaths.Count,
                HintCount = 0,
                HasClassLikeDefinitions = hasClassLikeDefinitions,
                HasMultipleDefinitions = hasMultipleDefinitions,
                HasMultipleDefinitionFiles = definitionPaths.Count > 1,
                Definitions = definitions,
                Callers = [],
                FileImpacts = [],
                Truncated = false,
                GraphTableAvailable = _hasReferencesTable,
                ZeroResultReason = definitions.Count == 0 ? "no_matching_definition" : "depth_zero",
                Suggestion = definitions.Count == 0
                    ? "Try `cdidx definition <symbol>` to confirm the indexed name."
                    : "Use `cdidx impact <symbol> --depth 1` or higher to traverse callers.",
            };
        }

        var (callers, truncated) = GetTransitiveCallers(symbolName, maxDepth, limit, lang, pathPatterns, excludePathPatterns, excludeTests);

        var impactMode = "callers";
        var fileImpacts = new List<FileDependencyResult>();
        string? zeroResultReason = null;
        string? suggestion = null;
        var heuristic = false;

        if (callers.Count == 0)
        {
            impactMode = "none";

            if (_hasReferencesTable)
            {
                if (definitions.Count > 0 && definitions.All(d => IsNonCallableImpactKind(d.Kind)))
                {
                    zeroResultReason = "non_callable_symbol_kind";
                    suggestion = "Try `cdidx definition <symbol>` and then run `impact` on a specific callable member instead.";
                }
                else if (hasMultipleFallbackDefinitions)
                {
                    zeroResultReason = hasMultipleFallbackDefinitionFiles ? "multiple_definition_files" : "multiple_definitions";
                    suggestion = BuildImpactSuggestion(fallbackDefinitionPaths, hasClassLikeDefinitions, hasMultipleDefinitions: true, hasMultipleDefinitionFiles: hasMultipleFallbackDefinitionFiles);
                }
                else if (fallbackDefinitions.Count == 1)
                {
                    var fallbackNames = ResolveImpactFallbackNames(fallbackDefinitions[0]);
                    var (hintResults, hintTruncated) = GetFileDependencyHintsToResolvedType(fallbackDefinitions[0], fallbackNames, limit, lang, pathPatterns, excludePathPatterns, excludeTests);
                    fileImpacts = hintResults;
                    truncated |= hintTruncated;
                    if (fileImpacts.Count > 0)
                    {
                        impactMode = "file_dependency_hints";
                        heuristic = true;
                        suggestion = "These file-level dependents are heuristic only; confirm with `cdidx deps --path <definition-path> --reverse` and a member-level `impact` query.";
                    }
                    else
                    {
                        zeroResultReason = "class_symbol_no_symbol_callers";
                        suggestion = BuildImpactSuggestion(definitionPaths, hasClassLikeDefinitions, hasMultipleDefinitions: false, hasMultipleDefinitionFiles: false);
                    }
                }
                else if (hasMultipleDefinitions)
                {
                    zeroResultReason = definitionPaths.Count > 1 ? "multiple_definition_files" : "multiple_definitions";
                    suggestion = BuildImpactSuggestion(definitionPaths, hasClassLikeDefinitions, hasMultipleDefinitions: true, hasMultipleDefinitionFiles: definitionPaths.Count > 1);
                }
                else if (definitions.Count == 0)
                {
                    zeroResultReason = "no_matching_definition";
                    suggestion = "Try `cdidx definition <symbol>` to confirm the indexed name.";
                }
            }
        }

        return new ImpactAnalysisResult
        {
            Query = symbolName,
            ResolvedName = resolvedName,
            ImpactMode = impactMode,
            Heuristic = heuristic,
            MaxDepth = maxDepth,
            DefinitionCount = definitions.Count,
            DefinitionFileCount = definitionPaths.Count,
            HintCount = fileImpacts.Count,
            HasClassLikeDefinitions = hasClassLikeDefinitions,
            HasMultipleDefinitions = hasMultipleDefinitions,
            HasMultipleDefinitionFiles = definitionPaths.Count > 1,
            Definitions = definitions,
            Callers = callers,
            FileImpacts = fileImpacts,
            Truncated = truncated,
            GraphTableAvailable = _hasReferencesTable,
            ZeroResultReason = zeroResultReason,
            Suggestion = suggestion,
        };
    }

    private List<SymbolResult> ResolveImpactDefinitions(string resolvedName, string? lang, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests)
    {
        var normalizedName = SqlNameResolver.NormalizeQualifiedName(resolvedName);
        var leafName = SqlNameResolver.GetLeafName(resolvedName);
        var segmentCount = SqlNameResolver.GetSegmentCount(resolvedName);
        var allowLeafFallback = !SqlNameResolver.HasQualifier(resolvedName);
        using var cmd = _conn.CreateCommand();
        var supportedLangFilter = BuildGraphSupportedLanguagePredicate(cmd, "f", "impactDefLang");
        var nameCondition = _foldReady
            ? allowLeafFallback
                ? "(s.name_folded = @resolvedNameFolded OR (f.lang = 'sql' AND ((sql_segment_count(s.name) = @resolvedNameSegmentCount AND sql_normalize_name_folded(s.name) = @resolvedNameNormalizedFolded) OR sql_leaf_name_folded(s.name) = @resolvedNameLeafFolded)))"
                : "(s.name_folded = @resolvedNameFolded OR (f.lang = 'sql' AND sql_segment_count(s.name) = @resolvedNameSegmentCount AND sql_normalize_name_folded(s.name) = @resolvedNameNormalizedFolded))"
            : allowLeafFallback
                ? "(s.name = @resolvedName COLLATE NOCASE OR (f.lang = 'sql' AND ((sql_segment_count(s.name) = @resolvedNameSegmentCount AND sql_normalize_name(s.name) = @resolvedNameNormalized COLLATE NOCASE) OR sql_leaf_name(s.name) = @resolvedNameLeaf COLLATE NOCASE)))"
                : "(s.name = @resolvedName COLLATE NOCASE OR (f.lang = 'sql' AND sql_segment_count(s.name) = @resolvedNameSegmentCount AND sql_normalize_name(s.name) = @resolvedNameNormalized COLLATE NOCASE))";
        var sql = $@"
            SELECT f.path, f.lang, s.kind, s.name, s.line,
                   {GetSymbolColumnSql("start_line", "s.line")} AS start_line,
                   {GetSymbolColumnSql("end_line", "s.line")} AS end_line,
                   {GetSymbolColumnSql("body_start_line")} AS body_start_line,
                   {GetSymbolColumnSql("body_end_line")} AS body_end_line,
                   {GetSymbolColumnSql("signature")} AS signature,
                   {GetSymbolColumnSql("container_kind")} AS container_kind,
                   {GetSymbolColumnSql("container_name")} AS container_name,
                   {GetSymbolColumnSql("visibility")} AS visibility,
                   {GetSymbolColumnSql("return_type")} AS return_type
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE {nameCondition}
              AND {supportedLangFilter}";

        if (lang != null)
            sql += " AND f.lang = @lang";
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        sql += @" ORDER BY CASE
                     WHEN s.name = @resolvedName THEN 0
                     WHEN f.lang = 'sql' AND sql_segment_count(s.name) = @resolvedNameSegmentCount AND sql_normalize_name(s.name) = @resolvedNameNormalized THEN 1
                     WHEN f.lang = 'sql' AND sql_segment_count(s.name) = @resolvedNameSegmentCount AND sql_normalize_name_folded(s.name) = @resolvedNameNormalizedFolded THEN 2
                     WHEN @allowLeafFallback = 1 AND f.lang = 'sql' AND sql_leaf_name(s.name) = @resolvedNameLeaf THEN 3
                     WHEN @allowLeafFallback = 1 AND f.lang = 'sql' AND sql_leaf_name_folded(s.name) = @resolvedNameLeafFolded THEN 4
                     ELSE 5
                   END, " + $"{PathBucketOrder}, {VisibilityOrder}, s.name, f.path, s.line LIMIT @limit";

        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@resolvedName", resolvedName);
        cmd.Parameters.AddWithValue("@resolvedNameNormalized", normalizedName);
        cmd.Parameters.AddWithValue("@resolvedNameNormalizedFolded", NameFold.Fold(normalizedName) ?? normalizedName);
        cmd.Parameters.AddWithValue("@resolvedNameLeaf", leafName);
        cmd.Parameters.AddWithValue("@resolvedNameLeafFolded", NameFold.Fold(leafName) ?? leafName);
        cmd.Parameters.AddWithValue("@resolvedNameSegmentCount", segmentCount);
        cmd.Parameters.AddWithValue("@allowLeafFallback", allowLeafFallback ? 1 : 0);
        if (_foldReady)
            cmd.Parameters.AddWithValue("@resolvedNameFolded", NameFold.Fold(resolvedName) ?? resolvedName);
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", lang);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);
        cmd.Parameters.AddWithValue("@limit", 50);

        var results = new List<SymbolResult>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            results.Add(new SymbolResult
            {
                Path = reader.GetString(0),
                Lang = reader.GetString(1),
                Kind = reader.GetString(2),
                Name = reader.GetString(3),
                Line = reader.GetInt32(4),
                StartLine = !reader.IsDBNull(5) ? reader.GetInt32(5) : reader.GetInt32(4),
                EndLine = !reader.IsDBNull(6) ? reader.GetInt32(6) : reader.GetInt32(4),
                BodyStartLine = !reader.IsDBNull(7) ? reader.GetInt32(7) : null,
                BodyEndLine = !reader.IsDBNull(8) ? reader.GetInt32(8) : null,
                Signature = !reader.IsDBNull(9) ? reader.GetString(9) : null,
                ContainerKind = !reader.IsDBNull(10) ? reader.GetString(10) : null,
                ContainerName = !reader.IsDBNull(11) ? reader.GetString(11) : null,
                Visibility = !reader.IsDBNull(12) ? reader.GetString(12) : null,
                ReturnType = !reader.IsDBNull(13) ? reader.GetString(13) : null,
            });
        }

        return results;
    }

    // C# convention: a class `FooAttribute` is used in source as `[Foo]`, so the reference
    // site is stored with `symbol_name = "Foo"`. When a user queries with the class name
    // (`references FooAttribute`, `inspect FooAttribute`, `analyze_symbol("FooAttribute")`),
    // return the suffix-stripped form as an alias so the query still reaches the idiomatic
    // use site. Only applies for C# scope — other languages do not share the convention.
    // C# の規約: クラス `FooAttribute` はソース中で `[Foo]` として使われるため、参照サイトは
    // `symbol_name = "Foo"` で保存される。ユーザーがクラス名で問い合わせたとき
    // (`references FooAttribute` 等) でも慣用的な利用サイトに到達できるよう、
    // suffix を外した別名を返す。C# 以外の言語ではこの規約を持たないので適用しない。
    private static string? ComputeCSharpAttributeSuffixAlias(string? query, string? lang, string? referenceKind)
    {
        if (string.IsNullOrEmpty(query)) return null;
        if (lang != null && !lang.Equals("csharp", StringComparison.OrdinalIgnoreCase)) return null;
        // Only metadata lookups should apply the suffix alias: ordinary call-graph
        // queries (`--kind call` / `instantiate` / `subscribe`) must not match `Foo()`
        // call rows when the user typed `FooAttribute`. When `referenceKind` is null,
        // the SQL side additionally constrains the alias clause to attribute rows only.
        // metadata 参照の問い合わせ時だけ alias を適用する: `--kind call` などの call-graph
        // クエリは `FooAttribute` と入力されたときに `Foo()` の call 行に一致してはならない。
        // referenceKind が null のときは SQL 側でも alias 節を attribute 行に限定する。
        if (referenceKind != null && !referenceKind.Equals("attribute", StringComparison.OrdinalIgnoreCase))
            return null;
        const string suffix = "Attribute";
        // Case-insensitive suffix detection so `references myauditattribute` and
        // `inspect MyAuditATTRIBUTE` still produce the `MyAudit` alias, matching the
        // NOCASE / folded contract of the surrounding exact/substring query paths.
        // 大文字小文字を無視して suffix を検出することで、`myauditattribute` や
        // `MyAuditATTRIBUTE` のような形でも alias を生成できる。
        if (!query!.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return null;
        if (query.Length <= suffix.Length) return null;
        return query.Substring(0, query.Length - suffix.Length);
    }

    // CSS/SCSS convention: Sass variables are stored without the leading `$`, so queries
    // that keep the sigil should still reach the canonical symbol/reference rows.
    // CSS/SCSS の規約: Sass 変数は先頭の `$` を外した形で保存されるため、sigil 付きの
    // クエリでも canonical な symbol/reference 行に到達できるようにする。
    private static string? ComputeCssScssVariableAlias(string? query)
    {
        if (string.IsNullOrEmpty(query) || query[0] != '$')
            return null;
        if (query.Length <= 1)
            return null;
        return query[1..];
    }

    private List<string> ResolveImpactFallbackNames(SymbolResult definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Path) || string.IsNullOrWhiteSpace(definition.Name))
            return new List<string>();

        using var cmd = _conn.CreateCommand();
        var supportedLangFilter = BuildGraphSupportedLanguagePredicate(cmd, "f", "impactSafeNameLang");
        cmd.CommandText = @"
            SELECT DISTINCT s.name
            FROM symbols s
            JOIN files f ON s.file_id = f.id
            WHERE f.path = @targetPath
              AND " + supportedLangFilter + @"
              AND (
                    (s.name = @containerName AND s.kind = @containerKind)
                    OR s.container_name = @containerName
                  )
            ORDER BY s.name";
        cmd.Parameters.AddWithValue("@targetPath", definition.Path);
        cmd.Parameters.AddWithValue("@containerName", definition.Name);
        cmd.Parameters.AddWithValue("@containerKind", definition.Kind);

        var results = new List<string>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
            results.Add(reader.GetString(0));

        // C# attribute naming convention: a class `FooAttribute` is used as `[Foo]` in source,
        // so reference sites are stored with symbol_name `Foo`. Add the suffix-stripped alias
        // for the resolved definition itself so impact on `FooAttribute` can find metadata-only
        // usage sites. Only the resolved definition's own name gets the alias — applying the
        // strip to every same-file fallback name (e.g. a nested `BarAttribute` inside the file
        // that defines `FooAttribute`) would let `impact FooAttribute` falsely report `[Bar]`
        // usages as part of `FooAttribute`'s blast radius.
        // C# の属性命名規約: クラス `FooAttribute` はソースで `[Foo]` として使われ、参照サイトは
        // symbol_name `Foo` で保存される。`FooAttribute` への impact でも metadata 参照サイトを
        // 見つけられるよう、*解決済み定義自身* にのみサフィックスを外した別名を追加する。
        // same-file fallback 名全体（例: `FooAttribute` と同一ファイルに nested で存在する
        // `BarAttribute`）にまで strip を適用すると、`impact FooAttribute` が `[Bar]` 利用を
        // 誤って `FooAttribute` の影響範囲として報告してしまうため、定義自身だけに限定する。
        if (string.Equals(definition.Lang, "csharp", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(definition.Name) &&
            definition.Name.Length > "Attribute".Length &&
            definition.Name.EndsWith("Attribute", StringComparison.Ordinal))
        {
            var stripped = definition.Name.Substring(0, definition.Name.Length - "Attribute".Length);
            if (stripped.Length > 0 && !results.Contains(stripped))
                results.Add(stripped);
        }

        return results;
    }

    private (List<FileDependencyResult> Results, bool Truncated) GetFileDependencyHintsToResolvedType(SymbolResult definition, IReadOnlyList<string> fallbackNames, int limit, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false)
    {
        if (!_hasReferencesTable || string.IsNullOrWhiteSpace(definition.Path) || fallbackNames.Count == 0)
            return (new List<FileDependencyResult>(), false);

        using var cmd = _conn.CreateCommand();
        var innerSql = @"
                SELECT src.id AS source_file_id, src.path AS source_path, @impactTargetPath AS target_path,
                       r.symbol_name AS symbol_name,
                       r.line,
                       r.column_number,
                       " + GetLogicalReferenceKindSql("r.reference_kind") + @" AS logical_reference_kind
                FROM symbol_references r
                JOIN files src ON r.file_id = src.id
                WHERE src.path != @impactTargetPath";
        // `impact` heuristic file hints intentionally include metadata-only reference
        // kinds (`attribute` / `annotation`). A rename or removal of `User` breaks
        // `[JsonConverter(typeof(User))]` / `@Inject(User.class)` at compile time just
        // as surely as it breaks `new User()`, so file-level blast-radius analysis
        // must surface those sites as real dependencies. `callers` / `callees` still
        // reject metadata kinds at the CLI / MCP boundary because those commands model
        // the dynamic call graph, not the dependency graph.
        // `impact` の heuristic file hint は metadata-only な参照 (`attribute` /
        // `annotation`) も意図的に含める。`User` を rename / 削除すると
        // `[JsonConverter(typeof(User))]` / `@Inject(User.class)` も compile-time で
        // 壊れるため、ファイル単位の blast-radius 分析ではそれらも本物の依存として
        // 出す必要がある。`callers` / `callees` は call graph を扱うので、metadata 種別
        // の拒否は引き続き CLI / MCP boundary 側で行う。
        innerSql += $" AND {BuildGraphSupportedLanguagePredicate(cmd, "src", "impactDepsLang")}";
        if (lang != null)
            innerSql += " AND src.lang = @lang";
        var nameClauses = new List<string>(fallbackNames.Count);
        for (int i = 0; i < fallbackNames.Count; i++)
            nameClauses.Add($"r.symbol_name = @impactFallbackName{i}");
        innerSql += " AND (" + string.Join(" OR ", nameClauses) + ")";

        if (pathPatterns is { Count: > 0 })
        {
            var ors = new List<string>(pathPatterns.Count);
            for (int i = 0; i < pathPatterns.Count; i++)
                ors.Add($"src.path LIKE @pathPattern{i} ESCAPE '\\'");
            innerSql += " AND (" + string.Join(" OR ", ors) + ")";
        }
        if (excludePathPatterns is { Count: > 0 })
        {
            for (int i = 0; i < excludePathPatterns.Count; i++)
                innerSql += $" AND src.path NOT LIKE @excludePath{i} ESCAPE '\\'";
        }
        if (excludeTests)
            innerSql += $" AND NOT {TestPathCondition.Replace("f.path", "src.path")}";
        innerSql = "SELECT DISTINCT * FROM (" + innerSql + ")";

        cmd.CommandText = $@"
            SELECT source_file_id, source_path, target_path,
                   COUNT(*) AS reference_count,
                   GROUP_CONCAT(DISTINCT symbol_name) AS symbols,
                   MAX(CASE WHEN logical_reference_kind IN ('attribute','annotation') THEN 1 ELSE 0 END) AS has_metadata_ref
            FROM ({innerSql}) edges
            GROUP BY source_file_id, source_path, target_path
            ORDER BY reference_count DESC, source_path, target_path";
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", lang);
        cmd.Parameters.AddWithValue("@impactTargetPath", definition.Path);
        for (int i = 0; i < fallbackNames.Count; i++)
            cmd.Parameters.AddWithValue($"@impactFallbackName{i}", fallbackNames[i]);
        AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns);

        var candidates = new List<(long SourceFileId, bool HasMetadataRef, FileDependencyResult Edge)>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            candidates.Add((
                reader.GetInt64(0),
                reader.GetInt32(5) == 1,
                new FileDependencyResult
                {
                    SourcePath = reader.GetString(1),
                    TargetPath = reader.GetString(2),
                    ReferenceCount = reader.GetInt32(3),
                    Symbols = reader.GetString(4),
                }));
        }

        // Metadata references only carry the short use-site name (`Foo` for `[Foo]`,
        // `@Foo`). If multiple class-like definitions share the same unqualified name
        // across namespaces / packages (e.g. `A.MyAuditAttribute` and
        // `B.MyAuditAttribute`), we cannot uniquely attribute a `[MyAudit]` site to
        // either target. Skip the metadata evidence bypass in that ambiguous case so
        // `impact` does not over-report the blast radius of a rename / removal.
        // metadata 参照は use-site 側の短縮名 (`[Foo]` / `@Foo` の `Foo`) しか持た
        // ないため、namespace / package を跨いで同名の class-like 定義が複数存在
        // する場合、`[MyAudit]` 参照をどちらの target にも一意に紐付けられない。
        // そのような曖昧なケースでは metadata の evidence bypass を行わず、
        // `impact` が rename / 削除の影響範囲を過大報告しないようにする。
        var metadataBypassSafe = IsMetadataTargetUnambiguous(definition, lang, pathPatterns, excludePathPatterns, excludeTests);
        var evidenceCache = new Dictionary<long, bool>();
        var filtered = new List<FileDependencyResult>();
        foreach (var candidate in candidates)
        {
            // Metadata-only consumers (attribute / annotation sites like `[MyAudit]` or
            // `@Inject(User.class)`) legitimately lack structured type evidence in the
            // source file. Bypass the evidence guard for those edges only when the
            // class-like target is unambiguous so deps/impact can surface pure-attribute
            // consumers without over-attributing same-named targets.
            // metadata 専用の参照 (`[MyAudit]` や `@Inject(User.class)` のような attribute /
            // annotation 利用) は、source 側のファイルに structured な型利用が無くても
            // 正当な依存となるが、class-like target が一意に決まるときだけ evidence guard
            // をスキップする。曖昧なときは下の evidence 要求へフォールスルーさせ、
            // 同名 target への誤帰属を防ぐ。
            if (candidate.HasMetadataRef && metadataBypassSafe)
            {
                filtered.Add(candidate.Edge);
                continue;
            }
            if (!evidenceCache.TryGetValue(candidate.SourceFileId, out var hasEvidence))
            {
                hasEvidence = SourceFileHasStructuredTypeEvidence(candidate.SourceFileId, definition.Name);
                evidenceCache[candidate.SourceFileId] = hasEvidence;
            }
            if (hasEvidence)
                filtered.Add(candidate.Edge);
        }

        var truncated = filtered.Count > limit;
        if (truncated)
            filtered.RemoveRange(limit, filtered.Count - limit);

        return (filtered, truncated);
    }

    // Returns true when the metadata target name resolves to at most one class-like
    // symbol across the graph-supported languages. Ambiguous names (same unqualified
    // name under different namespaces / packages) must not trigger the metadata
    // evidence bypass because attribute / annotation reference rows only keep the
    // short name and cannot disambiguate between them.
    // graph 対応言語の中で class-like シンボルが高々 1 件しか存在しないときに true。
    // namespace / package を跨いで同名の class-like 定義が複数ある曖昧なケースでは
    // attribute / annotation 参照行が短縮名しか持たず区別できないため、metadata の
    // evidence bypass を許可しない。
    private bool IsMetadataTargetUnambiguous(
        SymbolResult definition,
        string? lang,
        IReadOnlyList<string>? pathPatterns,
        IReadOnlyList<string>? excludePathPatterns,
        bool excludeTests)
    {
        if (string.IsNullOrWhiteSpace(definition.Name))
            return false;
        using var cmd = _conn.CreateCommand();
        var supportedLangFilter = BuildGraphSupportedLanguagePredicate(cmd, "f", "metadataAmbigLang");
        // Count at symbol-identity level (path + line + name) rather than at path
        // level, so two same-named class-like definitions in the same source file
        // (e.g. `namespace A { class MyAuditAttribute { } } namespace B { class
        // MyAuditAttribute { } }` both in one .cs file) still register as ambiguous.
        // DISTINCT f.path alone would collapse them to 1 and falsely trigger the
        // metadata bypass.
        // 曖昧性は path 単位ではなく symbol identity 単位 (path + line + name) で数える。
        // 同じ .cs ファイル内に別名前空間で同名の class-like が 2 つあるケース
        // (例: `namespace A { class MyAuditAttribute { } } namespace B { class
        // MyAuditAttribute { } }`) でも ambiguity を 2 として検出できる。DISTINCT
        // f.path のままだと 1 に潰れ、metadata bypass が誤って有効化される。
        // For C# specifically, only count class-like definitions that are
        // plausible attribute metadata targets. We don't resolve base types
        // transitively at SQL time, so the best portable approximation is
        // "has an inheritance clause": any class declared with `: ...` is a
        // potential attribute type (direct `: Attribute`, indirect
        // `: BaseAudit` where BaseAudit itself derives from Attribute, or
        // any other `: Base` chain). A plain `class MyAuditAttribute { }`
        // with no `:` clause is not a valid `[MyAudit]` target at compile
        // time, so excluding it prevents the metadata bypass from being
        // falsely suppressed. We deliberately over-accept non-attribute
        // derived classes rather than under-accept indirectly-derived
        // attribute classes, because an invalid `[MyFoo]` against a
        // non-attribute class would fail to compile and therefore not
        // appear as a real reference. Other languages keep the broad
        // class-like candidate set because their metadata-target markers
        // don't match this signature shape.
        // C# は SQL 時点で基底型を遡れないため、「何かを継承している
        // class-like」を attribute 候補の近似として扱う。`: Attribute` の
        // 直接継承も、`: BaseAudit` のような中間基底経由の間接継承も、
        // 何らかの `: Base` があれば候補に含める。継承節の無い plain
        // `class MyAuditAttribute { }` だけを除外することで metadata
        // bypass の誤抑止を防ぐ。非 attribute を過剰に含めるが、無効な
        // `[MyFoo]` はコンパイルできないので実参照にはならず実害が無い。
        // 署名列が無い legacy DB では degrade して class 限定のみ使う。
        var metadataTargetKindExprF = BuildMetadataTargetKindExpr("f");
        var sql = $@"
            SELECT COUNT(*) FROM (
                SELECT DISTINCT f.path, s.line, s.name
                FROM symbols s
                JOIN files f ON s.file_id = f.id
                WHERE s.name = @metadataAmbigName COLLATE NOCASE
                  AND {metadataTargetKindExprF}
                  AND {supportedLangFilter}";
        if (lang != null)
        {
            sql += " AND f.lang = @metadataAmbigLangFilter";
            cmd.Parameters.AddWithValue("@metadataAmbigLangFilter", lang);
        }
        // Path / exclude-path parameters share the same glob-aware LIKE
        // translation as the rest of the reader. Plain text keeps substring
        // behavior, while `*` / `?` become wildcards. Passing the raw CLI
        // value here would let `--path src/A/*.cs` stay literal and undercount
        // ambiguous targets, so centralize the conversion in
        // BuildPathLikePattern.
        // path / exclude-path のパラメータは reader 全体で共通の glob 対応
        // LIKE 変換を使う。ワイルドカードを含まない文字列は従来どおり部分文字列、
        // `*` / `?` はワイルドカードとして扱う。CLI の生値をそのまま渡すと
        // `--path src/A/*.cs` がリテラル扱いのままになり、曖昧性の件数を誤って
        // 数え込むため、変換は BuildPathLikePattern に集約する。
        if (pathPatterns is { Count: > 0 })
        {
            var ors = new List<string>(pathPatterns.Count);
            for (int i = 0; i < pathPatterns.Count; i++)
            {
                ors.Add($"f.path LIKE @metadataAmbigPath{i} ESCAPE '\\'");
                cmd.Parameters.AddWithValue($"@metadataAmbigPath{i}", BuildPathLikePattern(pathPatterns[i]));
            }
            sql += " AND (" + string.Join(" OR ", ors) + ")";
        }
        if (excludePathPatterns is { Count: > 0 })
        {
            for (int i = 0; i < excludePathPatterns.Count; i++)
            {
                sql += $" AND f.path NOT LIKE @metadataAmbigExcludePath{i} ESCAPE '\\'";
                cmd.Parameters.AddWithValue($"@metadataAmbigExcludePath{i}", BuildPathLikePattern(excludePathPatterns[i]));
            }
        }
        if (excludeTests)
            sql += $" AND NOT {TestPathCondition}";
        sql += ")";
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@metadataAmbigName", definition.Name);
        var count = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        // Require exactly one authoritative metadata target named `definition.Name`.
        // `count == 0` is also unsafe for the bypass — if no class-like symbol with
        // that name is a valid metadata target, then a `[Foo]` reference cannot
        // resolve to the passed-in definition either. `count <= 1` would let the
        // bypass fire with zero candidates and falsely attribute `[Foo]` sites to a
        // non-attribute definition (e.g. `class FooAttribute : BaseService` post
        // #435 iter 4 scope-aware resolver). Issue #435 codex review iter 4.
        // 1 件厳密一致のみ unambiguous とみなす。count=0 はメタデータターゲットが
        // 一つも無い状態であり、`[Foo]` が passed-in 定義へ解決する根拠も無いため
        // bypass は発動させない。`<= 1` だと #435 iter 4 のスコープ対応で非属性
        // 派生になったクラスに `[Foo]` 参照を誤帰属させる。
        return count == 1;
    }

    private bool SourceFileHasStructuredTypeEvidence(long fileId, string typeName)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT s.name,
                   " + GetSymbolColumnSql("signature") + @" AS signature,
                   " + GetSymbolColumnSql("return_type") + @" AS return_type
            FROM symbols s
            WHERE s.file_id = @fileId";
        cmd.Parameters.AddWithValue("@fileId", fileId);

        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            var symbolName = reader.GetString(0);
            var signature = !reader.IsDBNull(1) ? reader.GetString(1) : null;
            var returnType = !reader.IsDBNull(2) ? reader.GetString(2) : null;
            if (SymbolProvidesStructuredTypeEvidence(symbolName, signature, returnType, typeName))
                return true;
        }

        return false;
    }

    private static bool SymbolProvidesStructuredTypeEvidence(string symbolName, string? signature, string? returnType, string typeName)
    {
        if (FoldedImpactNameEquals(returnType, typeName))
            return true;
        if (string.IsNullOrWhiteSpace(signature))
            return false;

        foreach (Match match in ImpactSignatureIdentifierRegex.Matches(signature))
        {
            var token = match.Value;
            if (FoldedImpactNameEquals(token, symbolName))
                continue;
            if (FoldedImpactNameEquals(token, typeName))
                return true;
        }

        return false;
    }

    private static bool FoldedImpactNameEquals(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        var leftFolded = NameFold.Fold(left) ?? left;
        var rightFolded = NameFold.Fold(right) ?? right;
        return string.Equals(leftFolded, rightFolded, StringComparison.Ordinal);
    }

    private static bool IsNonCallableImpactKind(string? kind) =>
        kind is "namespace" or "import";

    private static bool IsPreciseImpactFallbackKind(string? kind)
    {
        return kind is "class" or "struct" or "interface";
    }

    private static string BuildImpactSuggestion(IReadOnlyList<string> definitionPaths, bool hasClassLikeDefinitions, bool hasMultipleDefinitions, bool hasMultipleDefinitionFiles)
    {
        if (hasClassLikeDefinitions)
        {
            if (hasMultipleDefinitionFiles)
                return "Try `cdidx deps --path <definition-path> --reverse` for each definition file or query a member symbol instead.";
            if (hasMultipleDefinitions)
                return "Try a fully qualified or member symbol query, or inspect the overlapping definitions with `cdidx definition <symbol> --body`.";
            if (definitionPaths.Count > 0)
                return $"Try `cdidx deps --path {definitionPaths[0]} --reverse` or query a member symbol instead.";
        }

        if (hasMultipleDefinitions)
            return "Try a more specific symbol name or inspect each definition file with `cdidx definition <symbol> --body`.";

        return "Try `cdidx definition <symbol>` to confirm the indexed symbol and then query a more specific callable member.";
    }

    /// <summary>
    /// Find literal substring matches inside path-scoped indexed files.
    /// path で絞ったインデックス済みファイル内でリテラル部分文字列一致を探す。
    /// </summary>
    public List<FileFindResult> FindInFiles(string query, int limit, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, int before = 0, int after = 0, bool exact = false, int maxLineWidth = LineWidthFormatter.DefaultMaxLineWidth)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0 || pathPatterns == null || pathPatterns.Count == 0)
            return [];

        before = Math.Max(0, before);
        after = Math.Max(0, after);
        maxLineWidth = LineWidthFormatter.ClampMaxLineWidth(maxLineWidth);
        var comparison = exact ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        using var fileCmd = _conn.CreateCommand();
        var sql = "SELECT f.path, f.lang, f.lines FROM files f WHERE 1=1";
        if (lang != null)
            sql += " AND f.lang = @lang";
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        sql += $" ORDER BY {PathBucketOrder}, f.path";
        fileCmd.CommandText = sql;
        if (lang != null)
            fileCmd.Parameters.AddWithValue("@lang", lang);
        AddPathFilterParameters(fileCmd, pathPatterns, excludePathPatterns);

        var results = new List<FileFindResult>();
        using var fileReader = fileCmd.ExecuteTrackedReader();
        while (fileReader.TrackedRead())
        {
            if (results.Count >= limit)
                break;

            var path = fileReader.GetString(0);
            var fileLang = GetNullableString(fileReader, 1);
            var totalLines = fileReader.GetInt32(2);
            if (!TryLoadIndexedFileLines(path, out _, out _, out var lineMap) || lineMap.Count == 0)
                continue;

            var searchQuery = exact ? ExactSourceSearchNormalizer.Normalize(query, fileLang) : query;
            for (int lineNumber = 1; lineNumber <= totalLines && results.Count < limit; lineNumber++)
            {
                if (!lineMap.TryGetValue(lineNumber, out var lineText))
                    continue;

                int[]? rawIndexMap = null;
                var searchLine = exact
                    ? ExactSourceSearchNormalizer.Normalize(lineText, fileLang, out rawIndexMap)
                    : lineText;
                var snippetStart = Math.Max(1, lineNumber - before);
                var snippetEnd = Math.Min(totalLines, lineNumber + after);
                var snippetLineNumbers = Enumerable.Range(snippetStart, snippetEnd - snippetStart + 1)
                    .Where(lineMap.ContainsKey)
                    .ToList();
                if (snippetLineNumbers.Count == 0)
                    continue;

                for (int searchStart = 0; searchStart < searchLine.Length && results.Count < limit;)
                {
                    var matchColumn = searchLine.IndexOf(searchQuery, searchStart, comparison);
                    if (matchColumn < 0)
                        break;

                    var rawMatchColumn = rawIndexMap == null ? matchColumn : rawIndexMap[matchColumn];
                    var rawMatchLength = searchQuery.Length;
                    if (rawIndexMap != null && rawMatchLength > 0)
                    {
                        var rawMatchEndIndex = rawIndexMap[matchColumn + rawMatchLength - 1];
                        rawMatchLength = rawMatchEndIndex - rawMatchColumn + 1;
                    }

                    var snippetLines = snippetLineNumbers.Select(line => lineMap[line]).ToList();
                    var clampedSnippet = LineWidthFormatter.ClampLines(
                        snippetLines,
                        maxLineWidth,
                        focusLineIndex: snippetLineNumbers.IndexOf(lineNumber),
                        focusColumn: rawMatchColumn + 1,
                        focusLength: rawMatchLength);

                    results.Add(new FileFindResult
                    {
                        Path = path,
                        Lang = fileLang,
                        Line = lineNumber,
                        Column = rawMatchColumn + 1,
                        StartLine = snippetLineNumbers[0],
                        EndLine = snippetLineNumbers[^1],
                        Snippet = clampedSnippet.Text,
                        SnippetTruncated = clampedSnippet.Truncated,
                    });

                    searchStart = matchColumn + 1;
                }
            }
        }

        return results;
    }

    public QueryCountResult CountFindInFiles(string query, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool exact = false)
    {
        if (string.IsNullOrWhiteSpace(query) || pathPatterns == null || pathPatterns.Count == 0)
            return new QueryCountResult(0, 0);

        var comparison = exact ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        using var fileCmd = _conn.CreateCommand();
        var sql = "SELECT f.path, f.lang, f.lines FROM files f WHERE 1=1";
        if (lang != null)
            sql += " AND f.lang = @lang";
        AppendPathFilters(ref sql, pathPatterns, excludePathPatterns, excludeTests);
        sql += $" ORDER BY {PathBucketOrder}, f.path";
        fileCmd.CommandText = sql;
        if (lang != null)
            fileCmd.Parameters.AddWithValue("@lang", lang);
        AddPathFilterParameters(fileCmd, pathPatterns, excludePathPatterns);

        var count = 0;
        var fileCount = 0;
        using var fileReader = fileCmd.ExecuteTrackedReader();
        while (fileReader.TrackedRead())
        {
            var path = fileReader.GetString(0);
            var fileLang = GetNullableString(fileReader, 1);
            var totalLines = fileReader.GetInt32(2);
            if (!TryLoadIndexedFileLines(path, out _, out _, out var lineMap) || lineMap.Count == 0)
                continue;

            var searchQuery = exact ? ExactSourceSearchNormalizer.Normalize(query, fileLang) : query;
            var fileMatches = 0;
            for (int lineNumber = 1; lineNumber <= totalLines; lineNumber++)
            {
                if (!lineMap.TryGetValue(lineNumber, out var lineText))
                    continue;

                var searchLine = exact ? ExactSourceSearchNormalizer.Normalize(lineText, fileLang) : lineText;
                for (int searchStart = 0; searchStart < searchLine.Length;)
                {
                    var matchColumn = searchLine.IndexOf(searchQuery, searchStart, comparison);
                    if (matchColumn < 0)
                        break;

                    fileMatches++;
                    searchStart = matchColumn + 1;
                }
            }

            if (fileMatches > 0)
            {
                count += fileMatches;
                fileCount++;
            }
        }

        return new QueryCountResult(count, fileCount);
    }

    /// <summary>
    /// Reconstruct one indexed file into an ordered line map.
    /// 1つのインデックス済みファイルを順序付き行マップへ再構成する。
    /// </summary>
    private bool TryLoadIndexedFileLines(string path, out string? lang, out int totalLines, out SortedDictionary<int, string> lineMap, int? startLine = null, int? endLine = null)
    {
        lang = null;
        totalLines = 0;
        lineMap = new SortedDictionary<int, string>();
        if (string.IsNullOrWhiteSpace(path))
            return false;

        using var fileCmd = _conn.CreateCommand();
        fileCmd.CommandText = "SELECT lang, lines FROM files WHERE path = @path";
        fileCmd.Parameters.AddWithValue("@path", path);

        using var fileReader = fileCmd.ExecuteTrackedReader();
        if (!fileReader.TrackedRead())
            return false;

        lang = GetNullableString(fileReader, 0);
        totalLines = fileReader.GetInt32(1);

        using var chunkCmd = _conn.CreateCommand();
        var chunkSql = @"
            SELECT c.start_line, c.end_line, c.content
            FROM chunks c
            JOIN files f ON c.file_id = f.id
            WHERE f.path = @path";
        if (startLine.HasValue)
            chunkSql += " AND c.end_line >= @startLine";
        if (endLine.HasValue)
            chunkSql += " AND c.start_line <= @endLine";
        chunkSql += " ORDER BY c.start_line, c.chunk_index";
        chunkCmd.CommandText = chunkSql;
        chunkCmd.Parameters.AddWithValue("@path", path);
        if (startLine.HasValue)
            chunkCmd.Parameters.AddWithValue("@startLine", startLine.Value);
        if (endLine.HasValue)
            chunkCmd.Parameters.AddWithValue("@endLine", endLine.Value);

        using var chunkReader = chunkCmd.ExecuteTrackedReader();
        while (chunkReader.TrackedRead())
        {
            var chunkStartLine = chunkReader.GetInt32(0);
            var chunkEndLine = chunkReader.GetInt32(1);
            var chunkLines = chunkReader.GetString(2).Split('\n');
            var lineCount = chunkEndLine - chunkStartLine + 1;

            for (int i = 0; i < chunkLines.Length && i < lineCount; i++)
            {
                var absoluteLine = chunkStartLine + i;
                if (!lineMap.ContainsKey(absoluteLine))
                    lineMap[absoluteLine] = chunkLines[i];
            }
        }

        return lineMap.Count > 0;
    }

    /// <summary>
    /// Reconstruct a file excerpt from indexed chunks.
    /// インデックス済みチャンクからファイル抜粋を再構成する。
    /// </summary>
    public FileExcerptResult? GetExcerpt(
        string path,
        int startLine,
        int endLine,
        int before = 0,
        int after = 0,
        int? maxLineWidth = null,
        int? focusLine = null,
        int? focusColumn = null,
        int focusLength = 1)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (startLine <= 0)
            startLine = 1;
        if (endLine < startLine)
            endLine = startLine;
        if (before < 0)
            before = 0;
        if (after < 0)
            after = 0;
        var requestedStart = Math.Max(1, startLine - before);
        if (!TryLoadIndexedFileLines(path, out var lang, out var totalLines, out var lineMap, requestedStart, endLine + after))
            return null;
        var requestedEnd = Math.Min(totalLines, endLine + after);

        var selectedLines = Enumerable.Range(requestedStart, requestedEnd - requestedStart + 1)
            .Where(lineMap.ContainsKey)
            .ToList();

        if (selectedLines.Count == 0)
            return null;

        var contentLines = selectedLines.Select(line => lineMap[line]).ToList();
        var focusLineIndex = focusLine.HasValue ? selectedLines.IndexOf(focusLine.Value) : -1;
        if (focusLineIndex >= 0 && focusColumn.HasValue && focusColumn.Value > contentLines[focusLineIndex].Length)
            return null;
        var clampedContent = maxLineWidth.HasValue
            ? LineWidthFormatter.ClampLines(
                contentLines,
                maxLineWidth.Value,
                focusLineIndex >= 0 ? focusLineIndex : null,
                focusLineIndex >= 0 ? focusColumn : null,
                focusLength)
            : new ClampedTextResult(string.Join("\n", contentLines), false);

        return new FileExcerptResult
        {
            Path = path,
            Lang = lang,
            StartLine = selectedLines[0],
            EndLine = selectedLines[^1],
            Content = clampedContent.Text,
            ContentTruncated = clampedContent.Truncated,
        };
    }

    /// <summary>
    /// Return the length of the focused excerpt line when it is part of the reconstructed range.
    /// 抜粋として再構成される範囲内に focus line が含まれる場合、その行長を返す。
    /// </summary>
    public int? GetExcerptFocusLineLength(
        string path,
        int startLine,
        int endLine,
        int before = 0,
        int after = 0,
        int? focusLine = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !focusLine.HasValue)
            return null;

        if (startLine <= 0)
            startLine = 1;
        if (endLine < startLine)
            endLine = startLine;
        if (before < 0)
            before = 0;
        if (after < 0)
            after = 0;

        var requestedStart = Math.Max(1, startLine - before);
        if (!TryLoadIndexedFileLines(path, out _, out var totalLines, out var lineMap, requestedStart, endLine + after))
            return null;
        var requestedEnd = Math.Min(totalLines, endLine + after);

        if (focusLine.Value < requestedStart || focusLine.Value > requestedEnd)
            return null;

        return lineMap.TryGetValue(focusLine.Value, out var line) ? line.Length : null;
    }

    /// <summary>
    /// Get one indexed file by exact path.
    /// 完全一致パスでインデックス済みファイルを1件取得する。
    /// </summary>
    public FileResult? GetFileByPath(string path)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT f.path, f.lang, f.size, f.lines,
                   (SELECT COUNT(*) FROM symbols WHERE file_id = f.id) AS symbol_count,
                   {ReferenceCountByFileSubquery} AS reference_count,
                   {GetFileColumnSql("checksum")} AS checksum,
                   {GetFileColumnSql("modified")} AS modified,
                   {GetFileColumnSql("indexed_at")} AS indexed_at
            FROM files f
            WHERE f.path = @path";
        cmd.Parameters.AddWithValue("@path", path);

        using var reader = cmd.ExecuteTrackedReader();
        if (!reader.TrackedRead())
            return null;

        return new FileResult
        {
            Path = reader.GetString(0),
            Lang = GetNullableString(reader, 1),
            Size = reader.GetInt64(2),
            Lines = reader.GetInt32(3),
            SymbolCount = reader.GetInt32(4),
            ReferenceCount = reader.GetInt32(5),
            Checksum = GetNullableString(reader, 6),
            Modified = GetNullableDateTime(reader, 7),
            IndexedAt = GetNullableDateTime(reader, 8),
        };
    }

    /// <summary>
    /// Get database statistics.
    /// データベースの統計情報を取得する。
    /// </summary>
    public StatusResult GetStatus()
    {
        // Issue #180: wrap the multi-statement status read in one DEFERRED transaction so
        // every COUNT(*) / freshness / readiness query resolves against the same WAL
        // snapshot. Without this, a concurrent writer that commits between the first and
        // last statement can expose wildly inconsistent counts (e.g. `refs: 0` against a
        // steady-state 44k while an incremental update is mid-flight). DEFERRED avoids
        // acquiring a write lock — the transaction grabs a SHARED lock on the first SELECT
        // and holds one consistent snapshot until Commit releases it.
        // Issue #180: 複数 SELECT を 1 つの DEFERRED transaction で囲み、全 COUNT(*) /
        // freshness / readiness クエリを同じ WAL snapshot で解決する。これが無いと、
        // 並行 writer が途中で commit した際に「refs: 0 なのに files=836」のような不整合
        // が見える。DEFERRED は最初の SELECT で SHARED lock を取るのみで write lock を
        // 握らないため、別 writer を阻害しない。
        using var txn = _conn.BeginTransaction(deferred: true);
        var files = ExecuteScalar("SELECT COUNT(*) FROM files");
        var chunks = ExecuteScalar("SELECT COUNT(*) FROM chunks");
        var symbols = ExecuteScalar("SELECT COUNT(*) FROM symbols");
        var references = _hasReferencesTable ? ExecuteScalar("SELECT COUNT(*) FROM symbol_references") : 0L;
        var freshness = GetWorkspaceFreshness();
        var hasCSharpFiles = ScopeMayIncludeCSharpFiles("csharp", pathPatterns: null, excludePathPatterns: null, excludeTests: false, since: null);
        var csharpSymbolNameReady = !hasCSharpFiles || _csharpSymbolNameContractCurrent;
        // #435 codex review iter 3: mirror `csharp_symbol_name_ready` — the readiness flag
        // only applies when the workspace actually contains C# files, and the column +
        // stamp must match the current contract for the resolver edges to be trusted.
        // This surfaces the same flag we already emit from the CLI `index` JSON so that
        // `status --json` and MCP `status` expose a consistent trust signal (README /
        // CLAUDE.md contract).
        // #435 codex review iter 3: `csharp_symbol_name_ready` と同じ条件で expose する。
        // C# ファイルが 0 なら ready=true、そうでなければ列 + stamp の一致を要求する。
        var csharpMetadataTargetReady = !hasCSharpFiles || _csharpMetadataTargetReady;
        var sqlGraphContractSignal = GetSqlGraphContractSignal(lang: null);
        var hotspotFamilySignal = GetHotspotFamilySignal(lang: null);
        var foldReadyReason = ResolveFoldReadyReason();

        // Language breakdown / 言語別内訳
        // Scope the reader in an inner block so it releases its statement handle before
        // we Commit() the enclosing txn — `SqliteTransaction.Commit()` fails if any
        // reader on the same connection is still open.
        // reader を内側ブロックに閉じ込め、txn.Commit() の前に statement handle を
        // 解放する。SqliteTransaction.Commit() は同じ connection 上で開いている reader
        // があると失敗する。
        var langs = new Dictionary<string, long>();
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT lang, COUNT(*) FROM files WHERE lang IS NOT NULL GROUP BY lang ORDER BY COUNT(*) DESC";
            using var reader = cmd.ExecuteTrackedReader();
            while (reader.TrackedRead())
                langs[reader.GetString(0)] = reader.GetInt64(1);
        }

        var result = new StatusResult
        {
            Files = files,
            Chunks = chunks,
            Symbols = symbols,
            References = references,
            IndexedAt = freshness.IndexedAt,
            LatestModified = freshness.LatestModified,
            Languages = langs,
            GraphTableAvailable = _hasReferencesTable,
            IssuesTableAvailable = _hasIssuesTable,
            HotspotFamilyReady = hotspotFamilySignal.Ready,
            HotspotFamilyDegradedReason = hotspotFamilySignal.DegradedReason,
            CSharpSymbolNameReady = csharpSymbolNameReady,
            CSharpMetadataTargetReady = csharpMetadataTargetReady,
            SqlGraphContractReady = sqlGraphContractSignal.Ready,
            SqlGraphContractDegradedReason = sqlGraphContractSignal.DegradedReason,
            FoldReady = _foldReady,
            FoldReadyReason = foldReadyReason,
        };
        // Commit the read-only snapshot explicitly so the SHARED lock is released promptly.
        // read-only なので rollback でも同じだが、明示 commit して SHARED lock を早期解放する。
        txn.Commit();
        return result;
    }

    /// <summary>
    /// Delegate to RepoMapBuilder for repo-level overview generation.
    /// RepoMapBuilderに委譲してリポジトリ俯瞰情報を生成する。
    /// </summary>
    public RepoMapResult GetRepoMap(int limit = 10, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false)
    {
        var builder = new RepoMapBuilder(_conn, _fileColumns, _hasReferencesTable);
        return builder.Build(limit, lang, pathPatterns, excludePathPatterns, excludeTests, GetWorkspaceFreshness);
    }

    private long ExecuteScalar(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// Return a lightweight freshness hint for zero-result MCP responses.
    /// 0件MCPレスポンス向けの軽量な鮮度ヒントを返す。
    /// </summary>
    public FreshnessHintResult GetFreshnessHint()
    {
        var freshnessAvailable = _fileColumns.Contains("indexed_at");
        var fileCount = ExecuteScalar("SELECT COUNT(*) FROM files");
        var indexedAt = ExecuteNullableDateTime(
            freshnessAvailable ? "SELECT MAX(indexed_at) FROM files" : null);
        return new FreshnessHintResult
        {
            FileCount = fileCount,
            IndexedAt = indexedAt,
            FreshnessAvailable = freshnessAvailable,
            FreshnessDegradedReason = freshnessAvailable ? null : "files.indexed_at column missing in this index",
        };
    }

    private (DateTime? IndexedAt, DateTime? LatestModified) GetWorkspaceFreshness()
    {
        return (
            ExecuteNullableDateTime(_fileColumns.Contains("indexed_at") ? "SELECT MAX(indexed_at) FROM files" : null),
            ExecuteNullableDateTime(_fileColumns.Contains("modified") ? "SELECT MAX(modified) FROM files" : null)
        );
    }

    private DateTime? ExecuteNullableDateTime(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return null;

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        if (value == null || value is DBNull)
            return null;

        return ParseDateTimeValue(value);
    }

    private static string BuildGraphSupportReason(string? graphLanguage, bool? graphSupported)
    {
        return ReferenceExtractor.BuildGraphSupportReason(graphLanguage, graphSupported)
            ?? "Call-graph support could not be determined because no language filter or matching definition was available.";
    }

    private HashSet<string> LoadColumns(string tableName)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName})";

        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
            columns.Add(reader.GetString(1));

        return columns;
    }

    private string ReferenceContextSql(string referenceAlias, string referenceLineAlias = "rl")
        => _canUseReferenceLines
            ? $"COALESCE({referenceAlias}.context, {referenceLineAlias}.context)"
            : $"{referenceAlias}.context";

    private string ReferenceLineJoinSql(string referenceAlias, string referenceLineAlias = "rl")
        => _canUseReferenceLines
            ? $" LEFT JOIN reference_lines {referenceLineAlias} ON {referenceLineAlias}.id = {referenceAlias}.reference_line_id"
            : string.Empty;

    private string GetSymbolColumnSql(string columnName, string? fallbackSql = null, string symbolAlias = "s")
    {
        if (_symbolColumns.Contains(columnName))
        {
            // Older binaries added the column but may have left existing rows with NULL.
            // Coalesce to the fallback so queries don't crash on legacy indexes.
            // 古いバイナリがカラムだけ追加して既存行を NULL のまま残しているケースに備え、
            // fallback と COALESCE してレガシーインデックスでクラッシュしないようにする。
            return fallbackSql != null
                ? $"COALESCE({symbolAlias}.{columnName}, {fallbackSql})"
                : $"{symbolAlias}.{columnName}";
        }

        return fallbackSql ?? "NULL";
    }

    internal string GetFileColumnSql(string columnName, string? fallbackSql = null)
    {
        if (_fileColumns.Contains(columnName))
            return $"f.{columnName}";

        return fallbackSql ?? "NULL";
    }

    // Build the language-aware metadata-target eligibility predicate used by
    // `deps` (target_files / target_ambiguity) and `impact`
    // (IsMetadataTargetUnambiguous). Returns a SQL fragment that evaluates to
    // TRUE when a `(symbols s, files <fileAlias>)` row should be counted as a
    // plausible metadata target (`[Attribute]` / `@Annotation` / `@decorator`).
    // Rules by language:
    //   - C# (`csharp`): only `kind = 'class'` with an inheritance clause
    //     (`signature LIKE '%: %'`). Transitive base-type resolution is not
    //     available at SQL time, so "has any inheritance clause" is the
    //     portable approximation for direct `: Attribute` plus indirect
    //     `: BaseAudit` where `BaseAudit` itself derives from Attribute.
    //     Extractor-driven authoritative `is_metadata_target` classification is
    //     tracked as a follow-up (issue #435) and would let `deps` / `impact`
    //     reject non-attribute classes like `class MyAuditAttribute : BaseService`
    //     that this heuristic cannot distinguish.
    //     For legacy-migration DBs whose `signature` column exists but stores
    //     NULL for individual C# class rows, fall back to the canonical C#
    //     attribute-naming convention (`name LIKE '%Attribute'`). This is
    //     strictly narrower than the previous unconditional NULL-signature
    //     pass-through and prevents every NULL-signature class from being
    //     treated as a plausible metadata target. DBs without any `signature`
    //     column at all degrade to the same naming heuristic.
    //   - JS / TS (`javascript` / `typescript`): decorators target runtime
    //     entities — classes and factory `function` definitions
    //     (e.g. `function sealed(target) {}` used as `@sealed class Foo {}`).
    //     TypeScript `interface` is a compile-time type-only construct and
    //     cannot be a decorator target at runtime; including it would let a
    //     same-name `interface` inject false ambiguity against the real
    //     `function` or `class` provider and silently drop the decorator edge.
    //   - Everything else (Java `@interface`, Kotlin `annotation class`,
    //     Scala annotation classes, etc.): the annotation target is a
    //     class-like declaration, so keep the original class-like candidate
    //     set (`class` / `struct` / `interface`).
    // `deps` と `impact` で共有する言語別 metadata-target 適格性判定。
    // C# は `kind = 'class'` かつ継承節を持つ行を対象とする（直接/間接の Attribute 継承を
    // ポータブルに近似するため）。signature 列は存在するが値が NULL の legacy-migration
    // DB では C# の命名規約 `name LIKE '%Attribute'` にフォールバック — 従来の
    // 無条件許容より厳密で、NULL-signature の全 class を metadata target 扱いしない。
    // signature 列自体が無い旧 DB も同じ命名規約ヒューリスティックを使う。
    // extractor 主導の authoritative な `is_metadata_target` 判定は follow-up（issue #435）
    // として追跡しており、schema 化すれば `class MyAuditAttribute : BaseService` のような
    // 非 attribute 継承も厳密に除外できるが、現状のヒューリスティックでは判別できない。
    // JS / TS は decorator が runtime entity (class / factory function) のみ対象。
    // TypeScript の `interface` は型定義で runtime decorator target にならないため除外し、
    // 同名 `interface` が本物の `function` / `class` provider を曖昧化するのを防ぐ。
    // それ以外は従来どおり class-like を候補にする。
    private string BuildMetadataTargetKindExpr(string fileAlias)
    {
        // C# clause — class only (interface/struct cannot be attribute targets).
        // Non-NULL signature: accept any inheritance clause (`: %`) as the portable
        // approximation of direct/indirect Attribute derivation (see issue #435).
        // NULL signature: require the C# attribute naming convention
        // (`name LIKE '%Attribute'`). This is strictly narrower than the previous
        // unconditional NULL pass-through and prevents arbitrary NULL-signature
        // classes on a legacy-migration DB from being treated as metadata targets.
        // DBs missing the `signature` column entirely degrade to the same naming
        // heuristic.
        // C# は class のみ（interface/struct は attribute target にできない）。
        // 非 NULL signature は従来どおり継承節 `: %` で判定（直接/間接 Attribute の近似）。
        // NULL signature は C# 命名規約 `name LIKE '%Attribute'` に縮退 — 従来の
        // 無条件許容より厳密で、legacy-migration DB で任意の NULL-signature class が
        // metadata target 扱いされるのを防ぐ。signature 列欠落 DB も同じ命名規約を使う。
        // Authoritative column takes precedence once the writer's resolver has stamped the
        // current `metadata_target_version_csharp` version. Drops the `: %` heuristic for C#
        // so non-attribute classes like `class MyAuditAttribute : BaseService` no longer fake
        // ambiguity against a sibling real `class MyAuditAttribute : Attribute`. Issue #435.
        // writer の resolver が current version を stamp 済みの DB では authoritative 列を優先し、
        // `class MyAuditAttribute : BaseService` のような非 Attribute 派生を ambiguity から除外する。
        // Three-way branch keyed off the `is_metadata_target` column presence, not
        // `signature`. Branch (2) (legacy heuristic) must only fire when both the new
        // column and the old signature column are present — a DB missing
        // `is_metadata_target` entirely is truly ancient and must degrade to branch (3).
        // Issue #435 codex review.
        // 3 way 分岐は `is_metadata_target` 列の有無で切り替え、`signature` の有無では判定しない。
        // `is_metadata_target` 列すらない DB は真に古い legacy なので命名規約 fallback (branch 3) に落とす。
        string csharpClause;
        if (_csharpMetadataTargetReady)
        {
            csharpClause = $"({fileAlias}.lang = 'csharp' AND s.kind = 'class' AND s.is_metadata_target = 1)";
        }
        else if (_symbolColumns.Contains("is_metadata_target") && _symbolColumns.Contains("signature"))
        {
            csharpClause = $"({fileAlias}.lang = 'csharp' AND s.kind = 'class' AND ((s.signature IS NOT NULL AND s.signature LIKE '%: %') OR (s.signature IS NULL AND s.name LIKE '%Attribute')))";
        }
        else
        {
            csharpClause = $"({fileAlias}.lang = 'csharp' AND s.kind = 'class' AND s.name LIKE '%Attribute')";
        }
        // JS / TS clause — decorators target runtime entities (classes and factory
        // functions). TS `interface` is a type-only construct that cannot be a
        // decorator target, so excluding it avoids false ambiguity against a
        // real function/class provider sharing the same name.
        // JS / TS: decorator は runtime entity (class / factory function) のみ対象。
        // TS の `interface` は型定義のため除外しないと同名 interface が偽の曖昧さを
        // 発生させる。
        var jsClause = $"({fileAlias}.lang IN ('javascript','typescript') AND s.kind IN ('class','function'))";
        // All other graph-supported languages keep the original class-like set.
        // その他の graph 対応言語は従来どおり class-like を対象にする。
        var otherClause = $"({fileAlias}.lang NOT IN ('csharp','javascript','typescript') AND s.kind IN ('class','struct','interface'))";
        return $"({csharpClause} OR {jsClause} OR {otherClause})";
    }

    // `deps` keeps persisted SQL symbol names qualified (`dbo.fn_X`) but must
    // still join bare SQL reference rows (`fn_X`) back to that definition.
    // Normalize dependency target keys to logical qualified names for SQL while leaving
    // other languages on the stored symbol name. SQL reference rows can still fall back to
    // leaf-only matching at join time when the source site itself is unqualified.
    // SQL の依存 target key は qualified 名 (`dbo.fn_X`) に正規化し、他言語は保存名のまま。
    // SQL の source 側が unqualified (`fn_X`) の場合だけ join 時に leaf fallback を許可する。
    private static string BuildLogicalDependencySymbolNameExpr(string fileAlias, string symbolNameExpr)
        => $"CASE WHEN {fileAlias}.lang = 'sql' THEN sql_normalize_name({symbolNameExpr}) ELSE {symbolNameExpr} END";

    private static string BuildLogicalDependencySymbolSegmentCountExpr(string fileAlias, string symbolNameExpr)
        => $"CASE WHEN {fileAlias}.lang = 'sql' THEN sql_segment_count({symbolNameExpr}) ELSE 1 END";

    private static string BuildLogicalReferenceNameExpr(string langExpr, string symbolNameExpr, string contextExpr, string containerNameExpr, string columnNumberExpr)
        => $@"CASE
                WHEN {langExpr} = 'sql' THEN sql_resolve_reference_name_at({symbolNameExpr}, {contextExpr}, {containerNameExpr}, {columnNumberExpr})
                ELSE {symbolNameExpr}
            END";

    private static string BuildLogicalReferenceSegmentCountExpr(string langExpr, string symbolNameExpr, string contextExpr, string containerNameExpr, string columnNumberExpr)
        => $@"CASE
                WHEN {langExpr} = 'sql' THEN sql_resolve_reference_segment_count_at({symbolNameExpr}, {contextExpr}, {containerNameExpr}, {columnNumberExpr})
                ELSE 1
            END";

    private static string BuildLogicalReferenceLeafFallbackAllowedExpr(string langExpr, string symbolNameExpr, string contextExpr, string containerNameExpr, string columnNumberExpr)
        => $@"CASE
                WHEN {langExpr} = 'sql' THEN sql_allow_leaf_fallback_at({symbolNameExpr}, {contextExpr}, {containerNameExpr}, {columnNumberExpr})
                ELSE 0
            END";

    /// <summary>
    /// Compute file-level dependency edges: which files reference symbols defined in which other files.
    /// ファイル間の依存関係エッジを算出: どのファイルがどのファイルで定義されたシンボルを参照しているか。
    /// </summary>
    public List<FileDependencyResult> GetFileDependencies(int limit = 50, string? lang = null, IReadOnlyList<string>? pathPatterns = null, IReadOnlyList<string>? excludePathPatterns = null, bool excludeTests = false, bool reverse = false)
    {
        lang = NormalizeQueryLanguage(lang);
        if (!_hasReferencesTable) return new List<FileDependencyResult>();
        using var cmd = _conn.CreateCommand();
        var referenceLineJoin = ReferenceLineJoinSql("r");
        var contextSql = ReferenceContextSql("r");
        // Aggregate logical reference sites per source-file/name first, then join that bounded
        // set to distinct target files. This avoids the per-reference × per-symbol explosion that
        // could exhaust SQLite temp-store on large indexes with many same-named symbols.
        // まず source-file/name 単位に logical reference site 数を集約し、その後で distinct な
        // target file と結合することで、大規模 index で SQLite temp-store を枯渇させる
        // per-reference × per-symbol の膨張を防ぐ。
        var sourceFilterAlias = "src";
        var targetFilterAlias = "dst";
        var targetLogicalSymbolNameExpr = BuildLogicalDependencySymbolNameExpr("dst", "s.name");
        var targetLogicalSymbolSegmentCountExpr = BuildLogicalDependencySymbolSegmentCountExpr("dst", "s.name");
        var sqlDependencyTargetMatchExpr = @"(
                    (tf.target_lang != 'sql' AND tf.symbol_name = snc.symbol_name)
                 OR (tf.target_lang = 'sql' AND (
                        (tf.symbol_segment_count = snc.symbol_segment_count AND tf.symbol_name = snc.symbol_name COLLATE NOCASE)
                     OR (sql_segment_count(snc.raw_symbol_name) = 1
                         AND snc.allow_leaf_fallback = 1
                         AND tf.symbol_segment_count > 1
                         AND sql_leaf_name(tf.symbol_name) = snc.raw_symbol_name COLLATE NOCASE
                         AND NOT EXISTS (
                                SELECT 1
                                FROM target_files tf_exact
                                WHERE tf_exact.target_lang = tf.target_lang
                                  AND tf_exact.symbol_segment_count = 1
                                  AND tf_exact.symbol_name = snc.symbol_name COLLATE NOCASE
                            )
                         AND NOT EXISTS (
                                SELECT 1
                                FROM target_files tf_resolved
                                WHERE tf_resolved.target_lang = tf.target_lang
                                  AND tf_resolved.symbol_segment_count = snc.symbol_segment_count
                                  AND tf_resolved.symbol_name = snc.symbol_name COLLATE NOCASE
                            ))
                 ))
                )";
        var sql = @"
            WITH logical_references_primary AS (
                SELECT src.id AS source_file_id,
                       src.path AS source_path,
                       src.lang AS source_lang,
                       r.symbol_name,
                       " + contextSql + @" AS context,
                       r.container_name,
                       r.line,
                       r.column_number,
                       " + GetLogicalReferenceKindSql("r.reference_kind") + @" AS logical_reference_kind
                FROM symbol_references r
                JOIN files src ON r.file_id = src.id" + referenceLineJoin + @"
                WHERE 1 = 1";
        // `deps` intentionally includes metadata-only reference kinds
        // (`attribute` / `annotation`). Same rationale as
        // `GetFileDependencyHintsToResolvedType`: renaming or removing a type that
        // is only referenced via `[JsonConverter(typeof(User))]` or
        // `@Inject(User.class)` still breaks the annotated file at compile time, so
        // file-level dependency analysis must treat those sites as real edges.
        // Call-graph-specific commands (`callers` / `callees`) keep rejecting
        // metadata kinds at the CLI / MCP boundary — that is a separate contract.
        // `deps` は metadata-only 参照 (`attribute` / `annotation`) も意図的に
        // 含める。`GetFileDependencyHintsToResolvedType` と同じ理由で、
        // `[JsonConverter(typeof(User))]` や `@Inject(User.class)` 経由でしか参照
        // されない型でも、rename / 削除すれば annotated ファイルは compile-time
        // で壊れるため、ファイル単位の依存分析では本物の edge として扱う必要が
        // ある。call-graph 専用コマンド (`callers` / `callees`) 側では metadata
        // 種別の拒否を CLI / MCP boundary で引き続き行う — そちらは別契約。
        sql += $" AND {BuildGraphSupportedLanguagePredicate(cmd, "src", "depsLang")}";
        if (lang != null)
            sql += " AND src.lang = @lang";
        if (!reverse && pathPatterns is { Count: > 0 })
        {
            var ors = new List<string>(pathPatterns.Count);
            for (int i = 0; i < pathPatterns.Count; i++)
                ors.Add($"{sourceFilterAlias}.path LIKE @pathPattern{i} ESCAPE '\\'");
            sql += " AND (" + string.Join(" OR ", ors) + ")";
        }
        if (!reverse && excludePathPatterns is { Count: > 0 })
        {
            for (int i = 0; i < excludePathPatterns.Count; i++)
                sql += $" AND {sourceFilterAlias}.path NOT LIKE @excludePath{i} ESCAPE '\\'";
        }
        if (!reverse && excludeTests)
            sql += $" AND NOT {TestPathCondition.Replace("f.path", $"{sourceFilterAlias}.path")}";
        sql += @"
                GROUP BY src.id, src.path, src.lang, r.symbol_name, " + contextSql + @", r.container_name, r.line, r.column_number, logical_reference_kind
            ),
            logical_references AS (
                SELECT source_file_id, source_path, source_lang,
                       " + BuildLogicalReferenceNameExpr("source_lang", "symbol_name", "context", "container_name", "column_number") + @" AS symbol_name,
                       " + BuildLogicalReferenceSegmentCountExpr("source_lang", "symbol_name", "context", "container_name", "column_number") + @" AS symbol_segment_count,
                       " + BuildLogicalReferenceLeafFallbackAllowedExpr("source_lang", "symbol_name", "context", "container_name", "column_number") + @" AS allow_leaf_fallback,
                       symbol_name AS raw_symbol_name,
                       line, column_number, logical_reference_kind,
                       0 AS is_attribute_alias,
                       CASE WHEN logical_reference_kind IN ('attribute', 'annotation') THEN 1 ELSE 0 END AS is_metadata
                FROM logical_references_primary
                UNION ALL
                -- C# attribute suffix alias: [Foo] in source is stored with symbol_name='Foo',
                -- but the defining class is named 'FooAttribute'. Emit the canonical 'Foo' + 'Attribute'
                -- form so deps can match the class file as a target. The alias rows are flagged
                -- so the edges CTE can restrict them to class-like targets and avoid spurious
                -- edges to unrelated functions / properties that happen to be named 'FooAttribute'.
                -- C# 属性のサフィックス別名: ソース上の [Foo] は symbol_name='Foo' で保存されるが、
                -- 定義クラスは 'FooAttribute' 命名になるため、正規形 'Foo' + 'Attribute' を補って
                -- deps がクラス側のファイルを target として join できるようにする。alias 行には
                -- フラグを付け、edges CTE 側で class-like target だけに限定する。これにより、
                -- 偶然 'FooAttribute' という名前を持つ関数やプロパティへの誤ったエッジを防ぐ。
                SELECT source_file_id, source_path, source_lang,
                       symbol_name || 'Attribute' AS symbol_name,
                       1 AS symbol_segment_count,
                       0 AS allow_leaf_fallback,
                       symbol_name || 'Attribute' AS raw_symbol_name,
                       line, column_number, logical_reference_kind,
                       1 AS is_attribute_alias,
                       1 AS is_metadata
                FROM logical_references_primary
                WHERE source_lang = 'csharp'
                  AND logical_reference_kind = 'attribute'
                  AND symbol_name NOT LIKE '%Attribute'
            ),
            source_name_counts AS (
                -- Grouping includes is_metadata so metadata-only groups ([Foo] / @Foo)
                -- can be restricted to class-like targets independently from non-metadata
                -- call-graph groups that share the same symbol_name in the same file
                -- (e.g. `Foo()` call + `[Foo]` attribute both present in the same source).
                -- is_metadata を GROUP BY に含めることで、同じ source file / symbol_name を
                -- 共有する metadata 行と call-graph 行 (例: 同じファイル内の `Foo()` 呼び出し
                -- と `[Foo]` 属性) を別グループとして扱い、metadata 側だけに class-like
                -- target 制限を掛けられるようにする。
                SELECT source_file_id,
                       source_path,
                       source_lang,
                       symbol_name,
                       symbol_segment_count,
                       allow_leaf_fallback,
                       raw_symbol_name,
                       is_attribute_alias,
                       is_metadata,
                       COUNT(*) AS ref_count
                FROM logical_references
                GROUP BY source_file_id, source_path, source_lang, symbol_name, symbol_segment_count, allow_leaf_fallback, raw_symbol_name, is_attribute_alias, is_metadata
            ),
            target_files AS (
                -- Collapse per-symbol rows to one per (target_path, target_lang, symbol_name)
                -- and remember whether any of the same-name symbols is a class-like kind
                -- via MAX. Keeping kind in DISTINCT would split identical (path, lang, name)
                -- rows when one file defines both a class and a same-name function (e.g. a
                -- C# constructor), inflating the deps reference count.
                -- (target_path, target_lang, symbol_name) 単位に集約し、同名のシンボルの
                -- いずれかが class 系であるかを MAX で覚える。kind を DISTINCT に含めると、
                -- 同じ (path, lang, name) でも class と同名 function (C# のコンストラクタ等)
                -- が別行として残り、deps の参照カウントが膨らんでしまう。
                -- has_metadata_target_kind further narrows the class-like set to targets
                -- that can legitimately be referenced as [Attribute] metadata. For C#
                -- we cannot resolve base types transitively at SQL time, so the best
                -- portable approximation is an inheritance-clause check: any class
                -- declared with a base list is a potential attribute type (direct or
                -- indirect Attribute derivation). A plain class FooAttribute with no
                -- base clause is not a valid [Foo] target at compile time.
                -- Other languages keep the original class-like breadth. Legacy DBs
                -- without a signature column degrade to the broad class-like set.
                -- has_metadata_target_kind は [Attribute] metadata target として妥当な
                -- class-like のみに絞る。C# は SQL 時点で基底型を遡れないため、継承節を
                -- 持つクラスを候補とする近似を採る(直接・間接の Attribute 継承を
                -- 取りこぼさない)。他言語は class-like 全体を残す。signature 列が無い
                -- legacy DB では filter を無効化し class-like 全体に戻る。
                SELECT dst.path AS target_path,
                       dst.lang AS target_lang,
                       " + targetLogicalSymbolNameExpr + @" AS symbol_name,
                       " + targetLogicalSymbolSegmentCountExpr + @" AS symbol_segment_count,
                       MAX(CASE WHEN s.kind IN ('class','struct','interface') THEN 1 ELSE 0 END) AS has_class_like_kind,
                       MAX(CASE WHEN " + BuildMetadataTargetKindExpr("dst") + @"
                                THEN 1 ELSE 0 END) AS has_metadata_target_kind
                FROM symbols s
                JOIN files dst ON s.file_id = dst.id
                WHERE 1 = 1";
        sql += $" AND {BuildGraphSupportedLanguagePredicate(cmd, "dst", "depsTargetLang")}";
        if (lang != null)
            sql += " AND dst.lang = @lang";
        if (reverse && pathPatterns is { Count: > 0 })
        {
            var ors = new List<string>(pathPatterns.Count);
            for (int i = 0; i < pathPatterns.Count; i++)
                ors.Add($"{targetFilterAlias}.path LIKE @pathPattern{i} ESCAPE '\\'");
            sql += " AND (" + string.Join(" OR ", ors) + ")";
        }
        if (reverse && excludePathPatterns is { Count: > 0 })
        {
            for (int i = 0; i < excludePathPatterns.Count; i++)
                sql += $" AND {targetFilterAlias}.path NOT LIKE @excludePath{i} ESCAPE '\\'";
        }
        if (reverse && excludeTests)
            sql += $" AND NOT {TestPathCondition.Replace("f.path", $"{targetFilterAlias}.path")}";
        sql += @"
                GROUP BY dst.path, dst.lang, " + targetLogicalSymbolNameExpr + @", " + targetLogicalSymbolSegmentCountExpr + @"
            ),
            metadata_raw_suppression AS (
                -- When a raw C# attribute reference '[Foo]' (stored as symbol_name='Foo',
                -- logical_reference_kind='attribute') also has a synthetic suffix alias
                -- row that resolves to a class-like 'FooAttribute' target, drop the raw
                -- row to avoid creating a duplicate edge to any unrelated 'Foo' symbol
                -- (method, property, local class) that merely shares the bare name.
                -- 生の C# 属性参照 '[Foo]' (symbol_name='Foo', kind='attribute') に対して
                -- 同じ source_file 内で 'FooAttribute' の synthetic alias 行が
                -- class 系 target に解決できる場合、この行自体は落として
                -- 同名の関数/プロパティ/ローカルクラス 'Foo' への誤依存を防ぐ。
                SELECT DISTINCT lrp.source_file_id, lrp.symbol_name
                FROM logical_references_primary lrp
                JOIN target_files tf_alias
                  ON tf_alias.target_lang = lrp.source_lang
                 AND tf_alias.symbol_name = lrp.symbol_name || 'Attribute'
                 AND tf_alias.symbol_segment_count = 1
                 AND tf_alias.has_metadata_target_kind = 1
                WHERE lrp.source_lang = 'csharp'
                  AND lrp.logical_reference_kind = 'attribute'
                  AND lrp.symbol_name NOT LIKE '%Attribute'
            ),
            target_ambiguity AS (
                -- Count class-like definitions at symbol-identity level rather than
                -- file level. Two same-named class-like definitions in the same file
                -- (e.g. `namespace A { class FooAttribute { } } namespace B { class
                -- FooAttribute { } }` both inside one .cs file) collapse to a single
                -- target_files row because target_files is GROUPed by dst.path, so
                -- COUNT(DISTINCT target_path) alone would see count=1 and falsely
                -- treat the metadata target as unambiguous. Joining target_files back
                -- through files + symbols recovers the per-definition row count while
                -- still inheriting target_files' lang / path / graph-supported scope
                -- (since the join only keeps rows whose (path, lang, name) already
                -- appear in target_files).
                -- class-like 定義は path 単位ではなく symbol identity 単位で数える。
                -- 同じ .cs ファイル内に別名前空間で同名 class-like が 2 つあるケースは
                -- target_files (dst.path で GROUP BY) 上では 1 行に潰れており、
                -- COUNT(DISTINCT target_path) だけでは count=1 となり metadata target
                -- が一意と誤判定される。target_files から files + symbols に JOIN し直す
                -- ことで定義単位の件数を復元する。JOIN が target_files 既存行にしか
                -- 当たらないため、lang / path / graph-supported スコープはそのまま継承。
                SELECT tf.target_lang,
                       tf.symbol_name,
                       tf.symbol_segment_count,
                       COUNT(*) AS class_like_target_count
                FROM target_files tf
                JOIN files dst
                  ON dst.path = tf.target_path
                 AND dst.lang = tf.target_lang
                JOIN symbols s
                  ON s.file_id = dst.id
                 AND " + targetLogicalSymbolNameExpr + @" = tf.symbol_name
                 AND " + targetLogicalSymbolSegmentCountExpr + @" = tf.symbol_segment_count
                 -- Same language-aware metadata-eligibility filter as
                 -- target_files: C# restricts to `class` with inheritance
                 -- clause (interface/struct cannot be attribute targets);
                 -- JS/TS additionally accepts `function` (decorator
                 -- factory); others keep the class-like candidate set.
                 -- target_files と同じ言語別 metadata 適格性フィルタ。
                 -- C# は class 限定 + 継承節 (interface/struct は除外)。
                 -- JS/TS は decorator factory 用に function も許容。
                 -- それ以外は class-like 全体を候補にする。
                 AND " + BuildMetadataTargetKindExpr("dst") + @"
                WHERE tf.has_metadata_target_kind = 1
                GROUP BY tf.target_lang, tf.symbol_name, tf.symbol_segment_count
            ),
            edges AS (
                SELECT snc.source_path,
                       tf.target_path,
                       tf.symbol_name,
                       snc.ref_count
                FROM source_name_counts snc
                JOIN target_files tf
                  ON " + sqlDependencyTargetMatchExpr + @"
                 AND tf.target_lang = snc.source_lang
                LEFT JOIN metadata_raw_suppression mrs
                  ON mrs.source_file_id = snc.source_file_id
                 AND mrs.symbol_name = snc.symbol_name
                LEFT JOIN target_ambiguity ta
                  ON ta.target_lang = snc.source_lang
                 AND ta.symbol_name = snc.symbol_name
                 AND ta.symbol_segment_count = snc.symbol_segment_count
                WHERE snc.source_path != tf.target_path
                  -- All metadata references ([Foo] / @Foo) and their synthetic C#
                  -- suffix aliases must only match class-like target kinds; otherwise
                  -- a metadata reference would spuriously depend on any file that
                  -- merely defines a function / property / variable sharing the name.
                  -- Non-metadata call-graph refs keep matching any kind so e.g. a
                  -- constructor call can still tie back to a class definition.
                  -- metadata 参照 ([Foo] / @Foo) と C# の合成 alias 行はいずれも
                  -- class 系の target 種別にのみ一致させる。これを許すと同名の
                  -- 関数/プロパティ/変数を持つだけのファイルまで誤って依存してしまう。
                  -- 非 metadata の call-graph 参照は任意の kind に一致させて構わない
                  -- (コンストラクタ呼び出しがクラス定義に結び付くケースなど)。
                  AND (snc.is_metadata = 0 OR tf.has_metadata_target_kind = 1)
                  -- Drop raw C# '[Foo]' rows when the suffix alias already resolves
                  -- to a class-like 'FooAttribute' target in the same source file.
                  -- 同じ source file で suffix alias が class 系 'FooAttribute' に
                  -- 解決できている C# の raw '[Foo]' 行は落とす。
                  AND NOT (
                        snc.is_metadata = 1
                    AND snc.is_attribute_alias = 0
                    AND snc.source_lang = 'csharp'
                    AND mrs.source_file_id IS NOT NULL
                  )
                  -- Metadata edges only survive when the target symbol resolves to
                  -- a single class-like definition within scope; ambiguous cases
                  -- (multiple same-name attribute / annotation classes) are dropped.
                  -- metadata エッジは同名 class 系 target が 1 つだけのときのみ残す。
                  AND (snc.is_metadata = 0 OR COALESCE(ta.class_like_target_count, 0) <= 1)
            )
            SELECT source_path,
                   target_path,
                   SUM(ref_count) AS reference_count,
                   GROUP_CONCAT(symbol_name) AS symbols
            FROM edges
            GROUP BY source_path, target_path
            ORDER BY reference_count DESC, source_path, target_path
            LIMIT @limit";

        cmd.CommandText = sql;
        if (lang != null)
            cmd.Parameters.AddWithValue("@lang", lang);
        if (pathPatterns is { Count: > 0 })
        {
            for (int i = 0; i < pathPatterns.Count; i++)
                cmd.Parameters.AddWithValue($"@pathPattern{i}", BuildPathLikePattern(pathPatterns[i]));
        }
        if (excludePathPatterns is { Count: > 0 })
        {
            for (int i = 0; i < excludePathPatterns.Count; i++)
                cmd.Parameters.AddWithValue($"@excludePath{i}", BuildPathLikePattern(excludePathPatterns[i]));
        }
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<FileDependencyResult>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            results.Add(new FileDependencyResult
            {
                SourcePath = reader.GetString(0),
                TargetPath = reader.GetString(1),
                ReferenceCount = reader.GetInt32(2),
                Symbols = reader.GetString(3),
            });
        }
        return results;
    }

    internal static void AppendPathFilters(ref string sql, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests)
    {
        if (pathPatterns != null && pathPatterns.Count > 0)
        {
            // Multiple --path values are OR'd together / 複数の --path 値は OR で結合する。
            // Plain text keeps the old substring behavior, while glob tokens
            // (`*` / `?`) are translated to SQL LIKE wildcards.
            var ors = new List<string>(pathPatterns.Count);
            for (int i = 0; i < pathPatterns.Count; i++)
                ors.Add($"f.path LIKE @pathPattern{i} ESCAPE '\\'");
            sql += " AND (" + string.Join(" OR ", ors) + ")";
        }

        if (excludePathPatterns != null)
        {
            for (int i = 0; i < excludePathPatterns.Count; i++)
                sql += $" AND f.path NOT LIKE @excludePathPattern{i} ESCAPE '\\'";
        }

        if (excludeTests)
            sql += $" AND NOT {TestPathCondition}";
    }

    internal static void AddPathFilterParameters(SqliteCommand cmd, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns)
    {
        if (pathPatterns != null)
        {
            for (int i = 0; i < pathPatterns.Count; i++)
                cmd.Parameters.AddWithValue($"@pathPattern{i}", BuildPathLikePattern(pathPatterns[i]));
        }

        if (excludePathPatterns != null)
        {
            for (int i = 0; i < excludePathPatterns.Count; i++)
                cmd.Parameters.AddWithValue($"@excludePathPattern{i}", BuildPathLikePattern(excludePathPatterns[i]));
        }
    }

    internal static string BuildPathFiltersSql(string fileAlias, IReadOnlyList<string>? pathPatterns, IReadOnlyList<string>? excludePathPatterns, bool excludeTests)
    {
        var sql = string.Empty;
        if (pathPatterns != null && pathPatterns.Count > 0)
        {
            // Multiple --path values are OR'd together / 複数の --path 値は OR で結合する。
            // Plain text keeps the old substring behavior, while glob tokens
            // (`*` / `?`) are translated to SQL LIKE wildcards.
            var ors = new List<string>(pathPatterns.Count);
            for (int i = 0; i < pathPatterns.Count; i++)
                ors.Add($"{fileAlias}.path LIKE @pathPattern{i} ESCAPE '\\'");
            sql += " AND (" + string.Join(" OR ", ors) + ")";
        }

        if (excludePathPatterns != null)
        {
            for (int i = 0; i < excludePathPatterns.Count; i++)
                sql += $" AND {fileAlias}.path NOT LIKE @excludePathPattern{i} ESCAPE '\\'";
        }

        if (excludeTests)
            sql += $" AND NOT {TestPathCondition.Replace("f.path", $"{fileAlias}.path")}";

        return sql;
    }

    internal static DateTime? GetNullableDateTime(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;

        return ParseDateTimeValue(reader.GetValue(ordinal));
    }

    // Nullable-column reader helpers. Older DBs migrated in place may leave some
    // symbol columns nullable, so every read path has to guard. Centralizing this
    // avoids the #58/#60 class of bug where a single missed IsDBNull crashes queries.
    // 旧DBをその場移行すると一部カラムがNULL可のまま残るため、全読み取り経路でガードが必要。
    // #58/#60 のような IsDBNull 漏れによるクラッシュを構造的に防ぐためヘルパーに集約する。
    internal static string? GetNullableString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    internal static int? GetNullableInt32(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    internal static int GetInt32OrFallback(SqliteDataReader reader, int ordinal, int fallbackOrdinal)
        => reader.IsDBNull(ordinal) ? reader.GetInt32(fallbackOrdinal) : reader.GetInt32(ordinal);

    private static DateTime? ParseDateTimeValue(object value)
    {
        return value switch
        {
            DateTime dateTime => dateTime.Kind == DateTimeKind.Utc ? dateTime : DateTime.SpecifyKind(dateTime, DateTimeKind.Utc),
            string text when DateTime.TryParse(text, out var parsed) => parsed.Kind == DateTimeKind.Utc ? parsed : DateTime.SpecifyKind(parsed, DateTimeKind.Utc),
            _ => null,
        };
    }

    /// <summary>
    /// Get all file validation issues from the index.
    /// インデックスから全ファイル検証問題を取得する。
    /// </summary>
    public List<Models.FileIssue> GetIssues(string? kind = null, IReadOnlyList<string>? pathPatterns = null)
    {
        if (!_hasIssuesTable) return new List<Models.FileIssue>();
        using var cmd = _conn.CreateCommand();
        var sql = @"
            SELECT f.path, i.kind, i.line, i.message
            FROM file_issues i
            JOIN files f ON i.file_id = f.id
            WHERE 1=1";
        if (kind != null)
            sql += " AND i.kind = @kind";
        if (pathPatterns is { Count: > 0 })
        {
            // OR multiple path filters / 複数パスフィルタを OR で結合
            var ors = new List<string>(pathPatterns.Count);
            for (int i = 0; i < pathPatterns.Count; i++)
                ors.Add($"f.path LIKE @pathPattern{i} ESCAPE '\\'");
            sql += " AND (" + string.Join(" OR ", ors) + ")";
        }
        sql += " ORDER BY f.path, i.line";

        cmd.CommandText = sql;
        if (kind != null)
            cmd.Parameters.AddWithValue("@kind", kind);
        if (pathPatterns is { Count: > 0 })
        {
            for (int i = 0; i < pathPatterns.Count; i++)
                cmd.Parameters.AddWithValue($"@pathPattern{i}", BuildPathLikePattern(pathPatterns[i]));
        }

        var results = new List<Models.FileIssue>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
        {
            results.Add(new Models.FileIssue
            {
                Path = reader.GetString(0),
                Kind = reader.GetString(1),
                Line = reader.GetInt32(2),
                Message = reader.GetString(3),
            });
        }
        return results;
    }
}

// Result DTOs are in Models/QueryResults.cs / 結果DTOは Models/QueryResults.cs に分離
