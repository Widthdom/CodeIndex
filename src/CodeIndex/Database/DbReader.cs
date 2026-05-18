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
    private readonly PreparedCommandCache? _commandCache;
    private readonly bool _isReadOnly;
    private readonly DbSchemaCache? _schemaCache;
    private readonly CancellationToken _cancellation;
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
    private readonly Dictionary<string, Dictionary<int, HashSet<string>>> _activeCSharpTypeNamespacesByPathLine = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<int, List<CSharpContainingTypeScope>>> _activeCSharpContainingTypeScopesByPathLine = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<int, HashSet<string>>> _activeCSharpUsingStaticTargetsByPathLine = new(StringComparer.Ordinal);
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
    private const string EventReferenceKindsSql = "('subscribe', 'unsubscribe', 'razor_event_binding')";
    private const string ImpactAnchorReferenceKindsSql = "('call', 'instantiate', 'subscribe', 'unsubscribe', 'razor_event_binding')";
    // Reference kinds that participate in the call-graph (callers/callees/hotspots). Metadata
    // kinds such as `attribute` / `annotation` are excluded so they do not inflate the graph
    // with non-call edges (issue #293); React `consumes_hook` and C++ `friend` edges are retained
    // because users expect them in dependency-oriented graph queries.
    // call-graph (callers/callees/hotspots) に参加する reference kind。`attribute` / `annotation`
    // のようなメタデータ kind は非呼び出しエッジなのでここから除外する (issue #293)。
    // Razor の `razor_event_binding`、React の `consumes_hook`、C++ の `friend` は依存関係 graph query に含める。
    internal const string CallGraphReferenceKindsSql = "('augmentation', 'call', 'instantiate', 'subscribe', 'unsubscribe', 'razor_event_binding', 'friend', 'consumes_hook')";
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
    private sealed record CSharpNamespaceScope(string QualifiedName, int ScopeStartLine, int ScopeEndLine);
    private sealed record CSharpContainingTypeScope(string Path, string Kind, string QualifiedName, string? Visibility, string? Signature, int DeclarationLine, int ScopeStartLine, int ScopeEndLine);
    private sealed record CSharpUsingStaticScope(string TargetQualifiedName, int Line, int ScopeStartLine, int ScopeEndLine);
    private sealed record CSharpUsingNamespaceScope(string TargetQualifiedName, int Line, int ScopeStartLine, int ScopeEndLine);
    private sealed record CSharpUsingAliasScope(string AliasName, string TargetQualifiedName, int Line, int ScopeStartLine, int ScopeEndLine, bool TargetsType);
    private sealed record CSharpTypeNamespaceCandidate(string QualifiedName, string Path, bool IsFileLocal);
    private sealed record CSharpContainingTypeCandidate(string QualifiedName, bool AccessibleFromDerivedType);

    /// <summary>
    /// Visibility ranking: public symbols first, then protected, internal, private, unknown last.
    /// 可視性ランキング: public を最優先、次に protected、internal、private、不明は最後。
    /// </summary>
    internal string VisibilityOrder => GetVisibilityRankSql(GetSymbolColumnSql("visibility"));

    private static string GetVisibilityRankSql(string visibilitySql) => $@"
        CASE lower({visibilitySql})
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

    private static string GetVisibilityLabelSql(string visibilityRankSql) => $@"
        CASE {visibilityRankSql}
            WHEN 0 THEN 'public'
            WHEN 1 THEN 'protected'
            WHEN 2 THEN 'internal'
            WHEN 3 THEN 'private'
            ELSE NULL
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

    // Derived-table joins that supply the per-file match and visibility buckets referenced by
    // search ranking. GROUP BY keeps the symbol predicates out of the outer per-hit scan while
    // preserving the best visibility rank among matching symbols in each file.
    // 検索順位で参照するファイル単位の一致・可視性 bucket を派生テーブルで供給する。GROUP BY により
    // symbol 述語を外側の hit ごとの scan から切り離しつつ、ファイル内の一致シンボルの最良 visibility を保持する。
    internal string SearchSymbolMatchJoinsSql
    {
        get
        {
            var exactVisibilityRankSql = GetVisibilityRankSql(GetSymbolColumnSql("visibility", symbolAlias: "s_exact"));
            var prefixVisibilityRankSql = GetVisibilityRankSql(GetSymbolColumnSql("visibility", symbolAlias: "s_prefix"));
            return $@"
        LEFT JOIN (
            SELECT
                file_id,
                MIN({exactVisibilityRankSql}) AS visibility_order,
                {GetVisibilityLabelSql($"MIN({exactVisibilityRankSql})")} AS visibility
            FROM symbols s_exact
            WHERE name = @rankingQuery COLLATE NOCASE
            GROUP BY file_id
        ) AS exact_symbol_match ON exact_symbol_match.file_id = f.id
        LEFT JOIN (
            SELECT
                file_id,
                MIN({prefixVisibilityRankSql}) AS visibility_order,
                {GetVisibilityLabelSql($"MIN({prefixVisibilityRankSql})")} AS visibility
            FROM symbols s_prefix
            WHERE name LIKE @rankingQueryPrefix ESCAPE '\' COLLATE NOCASE
            GROUP BY file_id
        ) AS prefix_symbol_match ON prefix_symbol_match.file_id = f.id";
        }
    }
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
        => $"CASE WHEN {referenceKindSql} IN {InvokeReferenceKindsSql} THEN 'invoke' " +
           $"WHEN {referenceKindSql} IN {EventReferenceKindsSql} THEN 'event' " +
           $"ELSE {referenceKindSql} END";

    private static string GetRawReferenceKindSql(string referenceKindSql)
        => referenceKindSql;

    private static string GetPreferredReferenceKindSql(string referenceKindSql)
        => $"CASE WHEN SUM(CASE WHEN {referenceKindSql} = 'instantiate' THEN 1 ELSE 0 END) > 0 THEN 'instantiate' ELSE MIN({referenceKindSql}) END";

    private static string GetPreferredLogicalReferenceKindSql(string referenceKindSql)
        => $"CASE WHEN SUM(CASE WHEN {referenceKindSql} IN {InvokeReferenceKindsSql} THEN 1 ELSE 0 END) > 0 THEN 'invoke' " +
           $"WHEN SUM(CASE WHEN {referenceKindSql} IN {EventReferenceKindsSql} THEN 1 ELSE 0 END) > 0 THEN 'event' " +
           $"ELSE MIN({referenceKindSql}) END";

    private static string GetGroupedCallerReferenceKindSql(string referenceKindSql)
        => $"CASE WHEN SUM(CASE WHEN {referenceKindSql} = 'instantiate' THEN 1 ELSE 0 END) > 0 THEN 'instantiate' " +
           $"WHEN SUM(CASE WHEN {referenceKindSql} = 'subscribe' THEN 1 ELSE 0 END) > 0 THEN 'subscribe' " +
           $"WHEN SUM(CASE WHEN {referenceKindSql} = 'unsubscribe' THEN 1 ELSE 0 END) > 0 THEN 'unsubscribe' " +
           $"ELSE MIN({referenceKindSql}) END";

    private static string GetGroupedCallerLogicalReferenceKindSql(string referenceKindSql)
        => GetPreferredLogicalReferenceKindSql(referenceKindSql);

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
        : this(connection, isReadOnly, schemaCache: null, cancellation: CancellationToken.None)
    {
    }

    /// <summary>
    /// Reuse a connection-scoped <see cref="DbSchemaCache"/> so callers that
    /// share a single `DbContext` across multiple reader constructions pay
    /// `PRAGMA table_info` / `PRAGMA index_list` exactly once per session
    /// instead of once per reader (issue #1565).
    /// </summary>
    public DbReader(DbContext context)
        : this(context?.Connection ?? throw new ArgumentNullException(nameof(context)),
               context.IsReadOnly,
               context.SchemaCache,
               CancellationToken.None,
               context.PreparedCommands)
    {
    }

    /// <summary>
    /// Combined constructor for callers that need both the connection-scoped
    /// schema cache (#1565) and per-request cancellation (#1567) — i.e. the
    /// MCP `WithDbReader` hot path that runs against the shared
    /// <see cref="DbContext"/> while a client request is in flight.
    /// </summary>
    public DbReader(DbContext context, CancellationToken cancellation)
        : this(context?.Connection ?? throw new ArgumentNullException(nameof(context)),
               context.IsReadOnly,
               context.SchemaCache,
               cancellation,
               context.PreparedCommands)
    {
    }

    public DbReader(SqliteConnection connection, bool isReadOnly, DbSchemaCache? schemaCache)
        : this(connection, isReadOnly, schemaCache, CancellationToken.None)
    {
    }

    // Per-request CancellationToken plumbed in from MCP / CLI callers so long-running
    // queries can be cancelled when the client disconnects or the server is shutting
    // down (#1567). Defaults to `CancellationToken.None` so existing call sites stay
    // source-compatible — only the MCP server wires a live token today.
    // MCP / CLI 呼び出し側から渡される per-request CancellationToken。クライアント切断や
    // サーバー shutdown 時にクエリを中断できるようにする (#1567)。既定値 None で既存の
    // 呼び出し元はそのまま動く。
    public DbReader(SqliteConnection connection, bool isReadOnly, CancellationToken cancellation)
        : this(connection, isReadOnly, schemaCache: null, cancellation: cancellation)
    {
    }

    public DbReader(SqliteConnection connection, bool isReadOnly, DbSchemaCache? schemaCache, CancellationToken cancellation)
        : this(connection, isReadOnly, schemaCache, cancellation, commandCache: null)
    {
    }

    private DbReader(SqliteConnection connection, bool isReadOnly, DbSchemaCache? schemaCache, CancellationToken cancellation, PreparedCommandCache? commandCache)
    {
        _conn = connection;
        _commandCache = commandCache;
        // SQL user functions are registered once per connection by `DbContext` when the
        // connection is opened. Re-registering on every `DbReader` construction wasted CPU
        // on hot MCP/CLI paths that build a short-lived reader per request (#1564).
        // SQL ユーザー関数は接続オープン時に `DbContext` が一度だけ登録するため、
        // ここでの再登録は不要 (#1564)。
        _isReadOnly = isReadOnly;
        _schemaCache = schemaCache;
        _cancellation = cancellation;
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

    /// <summary>
    /// Per-request CancellationToken plumbed in by the caller (e.g. the MCP server). Methods
    /// that loop over SQLite rows can check this between batches to bail out promptly on
    /// shutdown or client disconnect (#1567).
    /// 呼び出し側から渡される per-request CancellationToken (#1567)。SQLite 行を反復処理する
    /// メソッドはバッチ境界でこれを参照し、shutdown / 切断時に速やかに中断する。
    /// </summary>
    public CancellationToken Cancellation => _cancellation;

    /// <summary>
    /// Convenience wrapper for <c>_cancellation.ThrowIfCancellationRequested()</c> so partial
    /// classes do not need to reach into the private field (#1567).
    /// `_cancellation.ThrowIfCancellationRequested()` の薄いラッパ (#1567)。partial class から
    /// プライベートフィールドに触れずに済むようにする。
    /// </summary>
    public void ThrowIfCancellationRequested() => _cancellation.ThrowIfCancellationRequested();

    private SqliteCommand RentCommand(string sql, Action<SqliteCommand> configureSchema)
    {
        if (_commandCache != null)
            return _commandCache.GetOrAdd(sql, configureSchema);

        var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        configureSchema(cmd);
        return cmd;
    }

    private void ReleaseCommand(SqliteCommand cmd)
    {
        if (_commandCache == null)
            cmd.Dispose();
    }

    private static void SetParameter(SqliteCommand cmd, string name, object? value)
    {
        cmd.Parameters[name].Value = value ?? DBNull.Value;
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
                    : DegradationReasonCodes.BuildHotspotFamilyLanguageDegradedReason(lang));
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
            DegradedReason: DegradationReasonCodes.BuildHotspotFamilyLanguagesDegradedReason(unreadyLanguages));
    }

    private HashSet<string> LoadIndexes(string tableName)
    {
        if (_schemaCache != null)
            return _schemaCache.GetIndexes(tableName);
        return DbSchemaCache.LoadIndexes(_conn, tableName, DbSchemaCache.QueryHasTable(_conn, tableName));
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
            return DegradationReasonCodes.MissingFoldBackfill;
        if (storedVersion != NameFold.Version)
            return DegradationReasonCodes.StaleFoldKeyVersion;
        if (!string.Equals(storedFingerprint, NameFold.Fingerprint(), StringComparison.Ordinal))
            return DegradationReasonCodes.StaleFoldKeyFingerprint;
        return DegradationReasonCodes.FoldRowsNotRestamped;
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

    // Script-style top-level code emits reference rows without a container symbol.
    // Graph readers should surface those rows as a synthetic `<top-level>` caller only for
    // languages where such rows naturally represent executable top-level statements. Java and
    // other class/module-only languages stay excluded so unknown containers do not become
    // false call-graph roots.
    // script 形式の top-level code は container symbol なしの参照行を出す。実行可能な top-level
    // 文として自然に解釈できる言語だけを合成 `<top-level>` caller として扱い、Java などの
    // class/module 中心言語では unknown container を偽の call-graph root にしない。
    private const string SyntheticTopLevelCallerLanguagesSql = "('csharp', 'javascript', 'typescript', 'python')";

    private static string BuildCallerContainerPredicate(string fileAlias, string referenceAlias) =>
        $"({referenceAlias}.container_name IS NOT NULL OR ({fileAlias}.lang IN {SyntheticTopLevelCallerLanguagesSql} AND {referenceAlias}.container_name IS NULL))";

    private static string BuildCallerKindProjectionSql(string referenceAlias) =>
        $"CASE WHEN {referenceAlias}.container_name IS NULL THEN '{SyntheticTopLevelCallerKind}' ELSE {referenceAlias}.container_kind END";

    private static string BuildCallerNameProjectionSql(string referenceAlias) =>
        $"CASE WHEN {referenceAlias}.container_name IS NULL THEN '{SyntheticTopLevelCallerName}' ELSE {referenceAlias}.container_name END";

    private bool HasTable(string tableName)
    {
        if (_schemaCache != null)
            return _schemaCache.HasTable(tableName);
        return DbSchemaCache.QueryHasTable(_conn, tableName);
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
            DegradedReason: DegradationReasonCodes.BuildSqlGraphContractDegradedReason());
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
        var escaped = false;

        foreach (var ch in input)
        {
            if (escaped)
            {
                if (IsEscapablePathGlobCharacter(ch))
                {
                    AppendLikeLiteral(builder, ch);
                }
                else
                {
                    builder.Append("\\\\");
                    AppendLikeLiteral(builder, ch);
                }
                escaped = false;
                continue;
            }

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
                    escaped = true;
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
        if (escaped)
            builder.Append("\\\\");

        var pattern = builder.ToString();
        return hasWildcard ? pattern : $"%{pattern}%";
    }

    private static void AppendLikeLiteral(StringBuilder builder, char ch)
    {
        switch (ch)
        {
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

    private static bool IsEscapablePathGlobCharacter(char ch)
        => ch is '*' or '?' or '[' or ']' or '\\' or '%' or '_';

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



    private HashSet<string> LoadColumns(string tableName)
    {
        if (_schemaCache != null)
            return _schemaCache.GetColumns(tableName);
        return DbSchemaCache.LoadColumns(_conn, tableName);
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
