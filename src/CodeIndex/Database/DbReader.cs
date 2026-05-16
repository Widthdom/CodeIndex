using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;
using System.Globalization;
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

internal readonly record struct IndexedFileSnapshot(string Path, string? Checksum);

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
    // Issue #1515: forward-compat sentinel. `_indexWriterVersion` is the cdidx version
    // string the writer last stamped (null on legacy DBs). `_indexNewerThanReader` is
    // true when any persisted numeric contract version exceeds this binary's compiled
    // constants, signaling the DB was written by a newer cdidx and the existing
    // string.Equals(stored, current) gates are silently degrading. `_indexNewerThanReaderReason`
    // names the contracts that exceed so status output can tell the user why.
    // Issue #1515: 「より新しい cdidx が書いた DB を旧 cdidx が開いた」状態の検知用。
    internal readonly string? _indexWriterVersion;
    internal readonly bool _indexNewerThanReader;
    internal readonly string? _indexNewerThanReaderReason;
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
    // Bucket ordering for files whose symbols exactly / prefix-match the ranking query.
    // Issue #1520: previously each bucket embedded a correlated `EXISTS (... lower(name) = lower(@q))`
    // subquery inside ORDER BY. SQLite re-evaluated it per FTS hit, and `lower(col)` defeated the
    // `idx_symbols_name_nocase` / `idx_symbols_name_folded` indexes, producing an O(N*M) plan that
    // degraded `cdidx search` latency on large indexes. The bucket value is now sourced from a
    // dedicated LEFT JOIN against a derived table that pre-aggregates matching `file_id`s exactly
    // once per query, using SARGable `name COLLATE NOCASE` / `name LIKE ... COLLATE NOCASE`
    // predicates so the planner can use the existing indexes.
    // バケット順位は LEFT JOIN 結果の NULL 判定だけで決まり、`f.id` ごとの再評価は不要。
    internal const string ExactSymbolMatchOrder =
        "CASE WHEN exact_symbol_match.file_id IS NULL THEN 1 ELSE 0 END";
    internal const string PrefixSymbolMatchOrder =
        "CASE WHEN prefix_symbol_match.file_id IS NULL THEN 1 ELSE 0 END";

    // Derived-table joins that supply the per-file boolean buckets referenced by the ranking
    // ORDER BY constants above. SELECT DISTINCT keeps SQLite from flattening the subqueries
    // back into the outer query, so each predicate runs once per query instead of once per row.
    // 派生テーブルの SELECT DISTINCT により SQLite はサブクエリをフラット化せず、述語は 1 回だけ
    // 評価される。
    internal const string SearchSymbolMatchJoinsSql = @"
        LEFT JOIN (
            SELECT DISTINCT file_id FROM symbols
            WHERE name = @rankingQuery COLLATE NOCASE
        ) AS exact_symbol_match ON exact_symbol_match.file_id = f.id
        LEFT JOIN (
            SELECT DISTINCT file_id FROM symbols
            WHERE name LIKE @rankingQueryPrefix ESCAPE '\' COLLATE NOCASE
        ) AS prefix_symbol_match ON prefix_symbol_match.file_id = f.id";
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
        // SQL user functions are registered once per connection by `DbContext` when the
        // connection is opened. Re-registering on every `DbReader` construction wasted CPU
        // on hot MCP/CLI paths that build a short-lived reader per request (#1564).
        // SQL ユーザー関数は接続オープン時に `DbContext` が一度だけ登録するため、
        // ここでの再登録は不要 (#1564)。
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

        // Issue #1515: read the writer-version audit string and compute the forward-compat
        // sentinel from every numeric contract version persisted in the DB. The existing
        // string.Equals(stored, current) gates degrade silently on a "stored > current"
        // mismatch (older cdidx opens a DB written by a newer cdidx); this comparison
        // surfaces the asymmetry so status output can warn loudly instead.
        // Issue #1515: 旧 cdidx が新 cdidx 製 DB を開いたケースを明示的に検知する。
        _indexWriterVersion = TryGetMetaString(_conn, DbContext.CdidxWriterVersionMetaKey);
        (_indexNewerThanReader, _indexNewerThanReaderReason) = DetectNewerThanReaderContracts(_conn, userVersion);
    }

    private static (bool Newer, string? Reason) DetectNewerThanReaderContracts(SqliteConnection conn, int userVersion)
    {
        var newerContracts = new List<string>();
        // Numeric contract stamps. Each pair maps the persisted meta key to the binary's
        // current compiled max. "stored > current" means this binary cannot fully read
        // the contract a newer cdidx wrote.
        // 数値で stamp される contract version は「stored > current」のときだけ未来 DB と判断する。
        AppendIfStoredGreater(conn, DbContext.GetMetadataTargetVersionMetaKey("csharp"), DbContext.MetadataTargetVersion, "metadata_target_version_csharp", newerContracts);
        AppendIfStoredGreater(conn, DbContext.CSharpSymbolNameContractVersionMetaKey, DbContext.CSharpSymbolNameContractVersion, "csharp_symbol_name_contract_version", newerContracts);
        AppendIfStoredGreater(conn, DbContext.SqlGraphContractVersionMetaKey, DbContext.SqlGraphContractVersion, "sql_graph_contract_version", newerContracts);
        AppendIfStoredGreater(conn, "fold_key_version", NameFold.Version, "fold_key_version", newerContracts);
        foreach (var lang in FileIndexer.GetHotspotFamilyMarkerLanguages())
            AppendIfStoredGreater(conn, DbContext.GetHotspotFamilyVersionMetaKey(lang), DbContext.HotspotFamilyVersion, $"hotspot_family_version_{lang}", newerContracts);

        // PRAGMA user_version is a bitmap of readiness flags. A bit outside the known
        // `CurrentSchemaVersion` mask means a newer cdidx introduced a readiness flag this
        // binary does not understand, so any subsystem gated on that bit is invisible here.
        // CurrentSchemaVersion マスク外の bit は未知の readiness なので未来 DB として扱う。
        var unknownReadyBits = userVersion & ~DbContext.CurrentSchemaVersion;
        if (unknownReadyBits != 0)
            newerContracts.Add($"user_version_bits=0x{unknownReadyBits:X}");

        if (newerContracts.Count == 0)
            return (false, null);
        return (true, "DB was written by a newer cdidx than this binary; contracts newer than reader: " + string.Join(", ", newerContracts) + ". Upgrade cdidx, or rebuild the index with the current binary to clear the warning.");
    }

    private static void AppendIfStoredGreater(SqliteConnection conn, string metaKey, int currentMax, string label, List<string> sink)
    {
        var raw = TryGetMetaString(conn, metaKey);
        if (raw is null)
            return;
        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var stored))
            return;
        if (stored > currentMax)
            sink.Add($"{label}={stored}>{currentMax}");
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

    /// <summary>
    /// Read a metadata string from `codeindex_meta`. Returns null on missing key or read errors
    /// (legacy / read-only DBs where the table doesn't exist yet).
    /// codeindex_meta から文字列を読み出すヘルパー。未登録キーや legacy DB の場合は null。
    /// </summary>
    internal string? GetMetaString(string key) => TryGetMetaString(_conn, key);

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
        AddQueryLanguageAlias(aliases, "blazor", "csharp");
        AddQueryLanguageAlias(aliases, "c++", "cpp");
        AddQueryLanguageAlias(aliases, "cplusplus", "cpp");
        AddQueryLanguageAlias(aliases, "f#", "fsharp");
        AddQueryLanguageAlias(aliases, "fs", "fsharp");
        AddQueryLanguageAlias(aliases, "jav", "java");
        AddQueryLanguageAlias(aliases, "py", "python");
        AddQueryLanguageAlias(aliases, "python3", "python");
        AddQueryLanguageAlias(aliases, "py3", "python");
        AddQueryLanguageAlias(aliases, "rb", "ruby");
        AddQueryLanguageAlias(aliases, "vb.net", "vb");
        AddQueryLanguageAlias(aliases, "vbnet", "vb");
        AddQueryLanguageAlias(aliases, "vbs", "vb");
        AddQueryLanguageAlias(aliases, "vbscript", "vb");
        AddQueryLanguageAlias(aliases, "visual basic", "vb");
        AddQueryLanguageAlias(aliases, "visual-basic", "vb");
        AddQueryLanguageAlias(aliases, "visual_basic", "vb");
        AddQueryLanguageAlias(aliases, "visualbasic", "vb");
        AddQueryLanguageAlias(aliases, "sqlserver", "sql");
        AddQueryLanguageAlias(aliases, "mssql", "sql");
        AddQueryLanguageAlias(aliases, "transactsql", "sql");
        AddQueryLanguageAlias(aliases, "assembler", "assembly");
        AddQueryLanguageAlias(aliases, "gas", "assembly");
        AddQueryLanguageAlias(aliases, "gnuasm", "assembly");
        AddQueryLanguageAlias(aliases, "gnu assembler", "assembly");

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

    internal List<IndexedFileSnapshot> GetIndexedFileSnapshots()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT f.path, {GetFileColumnSql("checksum")} AS checksum
            FROM files f
            ORDER BY f.path
            """;

        var results = new List<IndexedFileSnapshot>();
        using var reader = cmd.ExecuteTrackedReader();
        while (reader.TrackedRead())
            results.Add(new IndexedFileSnapshot(reader.GetString(0), GetNullableString(reader, 1)));
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

    // Offsetless timestamps stored by SQLite (e.g. CURRENT_TIMESTAMP, or a UTC DateTime written
    // through Microsoft.Data.Sqlite) are treated as UTC. Parsing offsetless input via
    // DateTime.TryParse defaults to AssumeLocal, so without AssumeUniversal|AdjustToUniversal
    // the value would silently shift by the local TZ offset before being re-stamped as UTC,
    // which is the cross-TZ drift Issue #1545 describes.
    // SQLiteが保存するオフセットなしのタイムスタンプはUTC扱い。AssumeUniversal|AdjustToUniversalを
    // 付けないとローカル時刻として解釈→UTCにリラベルで暗黙にズレるため、Issue #1545対応として明示する。
    private static DateTime? ParseDateTimeValue(object value)
    {
        return value switch
        {
            DateTime dateTime => dateTime.Kind == DateTimeKind.Utc ? dateTime : DateTime.SpecifyKind(dateTime, DateTimeKind.Utc),
            string text when DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) => parsed,
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
