using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbWriter
{
    private const string FoldBackfillPhaseMetaKey = "fold_backfill_phase";
    private const string FoldBackfillLastSymbolIdMetaKey = "fold_backfill_last_symbol_id";
    private const string FoldBackfillLastReferenceIdMetaKey = "fold_backfill_last_reference_id";
    internal const string FoldBackfillGraphRefreshPendingMetaKey = "fold_backfill_graph_refresh_pending";

    private static readonly AsyncLocal<Action?> ScopedFoldBackfillRowUpdatedForTesting = new();
    private static readonly AsyncLocal<Action?> ScopedFoldBackfillVerificationForTesting = new();

    internal static Action? FoldBackfillRowUpdatedForTesting
    {
        get => ScopedFoldBackfillRowUpdatedForTesting.Value;
        set => ScopedFoldBackfillRowUpdatedForTesting.Value = value;
    }

    internal static Action? FoldBackfillVerificationForTesting
    {
        get => ScopedFoldBackfillVerificationForTesting.Value;
        set => ScopedFoldBackfillVerificationForTesting.Value = value;
    }

    /// <summary>
    /// A pre-v3 C# naming contract can be upgraded without reparsing only when every symbol kind
    /// that may represent an explicit-interface member still has its declaration signature.
    /// Without that source evidence, a short legacy name cannot be distinguished from a qualified
    /// explicit implementation, so stamping v3 would make the readiness signal untrustworthy.
    ///
    /// v3 より前の C# naming contract を再解析なしで更新できるのは、明示的 interface member
    /// になり得る全 symbol kind に宣言 signature が残っている場合だけである。source evidence
    /// がなければ短い legacy 名と修飾済み実装を区別できず、v3 stamp が不正確になる。
    /// </summary>
    public bool CanReconstructCSharpExplicitInterfaceIdentitiesFromPersistedRows()
    {
        var command = RentCommand(
            """
            SELECT COUNT(*)
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE f.lang = 'csharp'
              AND s.kind IN ('function', 'property', 'event')
              AND (s.signature IS NULL OR trim(s.signature) = '')
            """,
            static _ => { });
        try
        {
            var raw = command.ExecuteScalar();
            var missing = raw is long value ? value : Convert.ToInt64(raw ?? 0);
            return missing == 0;
        }
        finally
        {
            ReleaseCommand(command);
        }
    }

    /// <summary>
    /// True only when every existing row in symbols / symbol_references has a populated folded
    /// value for each source name that is itself non-NULL. Callers use this before stamping
    /// `FoldReadyFlag` on a full scan because the default incremental path skips unchanged files
    /// — their pre-#86 rows still carry NULL folded columns, so a naive stamp would flip readers
    /// onto the folded equality path and silently miss those legacy rows. Codex #86 review.
    /// full scan 成功時でも、incremental で skip された legacy 行が NULL のまま残っていれば
    /// fold-ready にしてはならない。stamp 前にこの実検証を通す。
    /// </summary>
    public bool AllFoldedColumnsBackfilled(
        bool requireCurrentSymbolExtractorVersions = false,
        bool requireCurrentFoldKeys = false)
    {
        if (IsInTransaction())
            return AllFoldedColumnsBackfilledCore(requireCurrentSymbolExtractorVersions, requireCurrentFoldKeys);

        bool ownTransaction = true;
        Execute("BEGIN DEFERRED");
        try
        {
            var result = AllFoldedColumnsBackfilledCore(requireCurrentSymbolExtractorVersions, requireCurrentFoldKeys);
            Execute("COMMIT");
            ownTransaction = false;
            return result;
        }
        catch
        {
            if (ownTransaction)
            {
                try { Execute("ROLLBACK"); }
                catch (SqliteException) { /* best effort */ }
            }

            throw;
        }
    }

    private bool AllFoldedColumnsBackfilledCore(
        bool requireCurrentSymbolExtractorVersions,
        bool requireCurrentFoldKeys)
    {
        if (requireCurrentSymbolExtractorVersions && !SymbolExtractorVersionsMatchCurrent())
            return false;

        FoldBackfillVerificationForTesting?.Invoke();
        var cmd = RentCommand(
            @"
            SELECT
                (SELECT COUNT(*) FROM symbols WHERE name_folded IS NULL)
              + (SELECT COUNT(*)
                 FROM symbols s
                 JOIN files f ON f.id = s.file_id
                 WHERE f.lang = 'csharp'
                   AND s.name_folded <> codeindex_name_fold(s.name)
                   AND s.display_name_folded IS NULL)
              + (SELECT COUNT(*) FROM symbol_references WHERE symbol_name IS NOT NULL AND symbol_name_folded IS NULL)
              + (SELECT COUNT(*) FROM symbol_references WHERE container_name IS NOT NULL AND container_name_folded IS NULL)",
            static _ => { });
        try
        {
            var raw = cmd.ExecuteScalar();
            long missing = raw is long l ? l : (raw is int i ? i : 0);
            if (missing != 0)
                return false;
        }
        finally
        {
            ReleaseCommand(cmd);
        }

        return !requireCurrentFoldKeys || AllFoldedColumnValuesMatchCurrentFold();
    }

    public bool AllFoldedColumnValuesMatchCurrentFold()
    {
        var markdownSymbolIdentityFolds = BuildMarkdownSymbolIdentityFoldMap();
        var symbols = RentCommand(
            """
            SELECT s.id, s.name, s.name_folded, s.display_name_folded,
                   f.lang, s.kind, s.signature
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE s.name IS NOT NULL
            """,
            static _ => { });
        try
        {
            using var reader = symbols.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                var expected = FoldPersistedSymbolName(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    markdownSymbolIdentityFolds);
                var actual = reader.IsDBNull(2) ? null : reader.GetString(2);
                if (!string.Equals(actual, expected, StringComparison.Ordinal))
                    return false;
                var foldedDisplay = DbReader.FoldNameForLanguage(
                    reader.GetString(1),
                    reader.IsDBNull(4) ? null : reader.GetString(4));
                var expectedDisplay =
                    string.Equals(
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        "csharp",
                        StringComparison.Ordinal)
                    && !string.Equals(expected, foldedDisplay, StringComparison.Ordinal)
                        ? foldedDisplay
                        : null;
                var actualDisplay = reader.IsDBNull(3) ? null : reader.GetString(3);
                if (!string.Equals(actualDisplay, expectedDisplay, StringComparison.Ordinal))
                    return false;
            }
        }
        finally
        {
            ReleaseCommand(symbols);
        }

        var references = RentCommand(
            @"
                SELECT r.symbol_name, r.symbol_name_folded,
                       r.container_name, r.container_name_folded,
                       f.lang, r.reference_kind
                FROM symbol_references r
                JOIN files f ON f.id = r.file_id
                WHERE r.symbol_name IS NOT NULL OR r.container_name IS NOT NULL",
            static _ => { });
        try
        {
            using var reader = references.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                if (!reader.IsDBNull(0))
                {
                    var expected = FoldPersistedReferenceName(
                        reader.GetString(0),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.GetString(5));
                    var actual = reader.IsDBNull(1) ? null : reader.GetString(1);
                    if (!string.Equals(actual, expected, StringComparison.Ordinal))
                        return false;
                }

                if (!reader.IsDBNull(2))
                {
                    var expected = DbReader.FoldNameForLanguage(
                        reader.GetString(2),
                        reader.IsDBNull(4) ? null : reader.GetString(4));
                    var actual = reader.IsDBNull(3) ? null : reader.GetString(3);
                    if (!string.Equals(actual, expected, StringComparison.Ordinal))
                        return false;
                }
            }
        }
        finally
        {
            ReleaseCommand(references);
        }

        return true;
    }

    public bool AllFoldedColumnsBackfilled(IReadOnlyCollection<string> requireCurrentSymbolExtractorLanguages)
    {
        if (IsInTransaction())
            return AllFoldedColumnsBackfilledCore(requireCurrentSymbolExtractorLanguages);

        bool ownTransaction = true;
        Execute("BEGIN DEFERRED");
        try
        {
            var result = AllFoldedColumnsBackfilledCore(requireCurrentSymbolExtractorLanguages);
            Execute("COMMIT");
            ownTransaction = false;
            return result;
        }
        catch
        {
            if (ownTransaction)
            {
                try { Execute("ROLLBACK"); }
                catch (SqliteException) { /* best effort */ }
            }

            throw;
        }
    }

    private bool AllFoldedColumnsBackfilledCore(IReadOnlyCollection<string> requireCurrentSymbolExtractorLanguages)
    {
        if (requireCurrentSymbolExtractorLanguages.Count > 0
            && !SymbolExtractorVersionsMatchCurrent(requireCurrentSymbolExtractorLanguages))
        {
            return false;
        }

        return AllFoldedColumnsBackfilledCore(
            requireCurrentSymbolExtractorVersions: false,
            requireCurrentFoldKeys: false);
    }

    public bool SymbolExtractorVersionsMatchCurrent()
    {
        foreach (var lang in GetIndexedLanguages())
        {
            if (!SymbolExtractorVersionMatchesCurrent(lang))
                return false;
        }

        return true;
    }

    public bool SymbolExtractorVersionsMatchCurrent(IEnumerable<string> languages)
    {
        foreach (var lang in languages)
        {
            if (!SymbolExtractorVersionMatchesCurrent(lang))
                return false;
        }

        return true;
    }

    private bool SymbolExtractorVersionMatchesCurrent(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang))
            return true;

        var stored = GetMetaString(DbContext.GetSymbolExtractorVersionMetaKey(lang));
        if (stored == null)
            return true;

        var current = SymbolExtractor.GetContractVersion(lang).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return stored == current;
    }

    private bool ExtractorContractsMatchCurrentForReuse(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang))
            return true;
        if (!SymbolExtractorVersionMatchesCurrent(lang))
            return false;
        if (!SymbolExtractor.RequiresExplicitReferenceGraphContractStamp(lang))
            return true;

        var storedGraphContract = GetMetaString(
            DbContext.GetDynamicReferenceGraphContractVersionMetaKey(lang));
        var currentGraphContract = SymbolExtractor.GetReferenceGraphContractVersion(lang).ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        return storedGraphContract == currentGraphContract;
    }

    /// <summary>
    /// Recompute persisted folded-name keys from existing symbol / reference rows without
    /// reparsing source files. This is used to upgrade legacy DBs (NULL folded columns) and
    /// to refresh stored keys after a future <see cref="NameFold.Version"/> bump.
    /// ソース再解析なしで既存行から folded key を再計算する。legacy DB の NULL 埋めと、
    /// 将来の <see cref="NameFold.Version"/> 変更時の key 再生成に使う。
    /// </summary>
    /// <param name="rewriteAll">
    /// When true, rewrite every non-null source name even if the folded column is already
    /// populated. Needed when the stored fold metadata does not match the current binary/runtime.
    /// true のとき、既に埋まっている folded 列も含めて全行再計算する（fold metadata 不一致時）。
    /// </param>
    /// <returns>Counts of symbol rows and reference rows rewritten.</returns>
    public (int Symbols, int SymbolReferences) BackfillFoldedColumns(
        bool rewriteAll = false,
        CancellationToken cancellationToken = default)
    {
        rewriteAll = ResolveFoldBackfillRewriteAll(rewriteAll);
        cancellationToken.ThrowIfCancellationRequested();
        var graphRefreshPending = string.Equals(
            GetMetaString(FoldBackfillGraphRefreshPendingMetaKey),
            "1",
            StringComparison.Ordinal);
        var pendingRows = CountBackfillFoldedColumns(rewriteAll);
        if (!graphRefreshPending && (pendingRows.Symbols > 0 || pendingRows.SymbolReferences > 0))
        {
            // Persist this before the first row mutation so cancellation after the rewrite but
            // before graph refresh cannot make a retry mistake the operation for a no-op.
            // 最初の行を書き換える前に pending を永続化し、書換え後から graph refresh
            // までの中断を retry が no-op と誤認しないようにする。
            SetMeta(FoldBackfillGraphRefreshPendingMetaKey, "1");
            graphRefreshPending = true;
        }

        var foldBackfillPhase = rewriteAll ? GetMetaString(FoldBackfillPhaseMetaKey) : null;
        var symbols = BackfillSymbolFoldedRows(rewriteAll, cancellationToken);
        if (rewriteAll && foldBackfillPhase != "references")
        {
            SetMeta(FoldBackfillPhaseMetaKey, "references");
            SetMeta(FoldBackfillLastReferenceIdMetaKey, "0");
        }

        var symbolReferences = BackfillReferenceFoldedRows(rewriteAll, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (graphRefreshPending)
        {
            // Candidate membership and resolved identities depend on the persisted folded keys.
            // Refresh them before advertising the rewritten rows as current.
            // candidate と解決済み identity は永続化 folded key に依存するため、
            // 書換え後の key を current と公開する前に graph を再解決する。
            RefreshMutualRecursionFlags(cancellationToken);
            SetMeta(FoldBackfillGraphRefreshPendingMetaKey, null);
        }
        if (rewriteAll)
            ClearFoldBackfillCheckpoint();

        return (symbols, symbolReferences);
    }

    public (int Symbols, int SymbolReferences) CountBackfillFoldedColumns(bool rewriteAll = false)
    {
        rewriteAll = ResolveFoldBackfillRewriteAll(rewriteAll);
        var phase = rewriteAll ? GetMetaString(FoldBackfillPhaseMetaKey) : null;
        var lastSymbolId = rewriteAll ? GetFoldBackfillCheckpoint(FoldBackfillLastSymbolIdMetaKey) : 0;
        var lastReferenceId = rewriteAll ? GetFoldBackfillCheckpoint(FoldBackfillLastReferenceIdMetaKey) : 0;

        var symbolsSql = rewriteAll && phase != "references"
            ? "SELECT COUNT(*) FROM symbols WHERE name IS NOT NULL AND id > @lastSymbolId"
            : rewriteAll
            ? "SELECT 0"
            : """
              SELECT COUNT(*)
              FROM symbols s
              JOIN files f ON f.id = s.file_id
              WHERE s.name IS NOT NULL
                AND (s.name_folded IS NULL
                     OR (f.lang = 'csharp'
                         AND s.name_folded <> codeindex_name_fold(s.name)
                         AND s.display_name_folded IS NULL))
              """;
        var symbolsUsesCheckpoint = rewriteAll && phase != "references";
        var symbols = RentCommand(
            symbolsSql,
            symbolsUsesCheckpoint
                ? static c => c.Parameters.Add("@lastSymbolId", SqliteType.Integer)
                : static _ => { });

        var referencesSql = rewriteAll
            ? @"SELECT COUNT(*)
                FROM symbol_references
                WHERE id > @lastReferenceId
                  AND (symbol_name IS NOT NULL OR container_name IS NOT NULL)"
            : @"SELECT COUNT(*)
                FROM symbol_references
                WHERE (symbol_name IS NOT NULL AND symbol_name_folded IS NULL)
                   OR (container_name IS NOT NULL AND container_name_folded IS NULL)";
        var references = RentCommand(
            referencesSql,
            rewriteAll
                ? static c => c.Parameters.Add("@lastReferenceId", SqliteType.Integer)
                : static _ => { });

        try
        {
            if (symbolsUsesCheckpoint)
                symbols.Parameters["@lastSymbolId"].Value = lastSymbolId;
            if (rewriteAll)
                references.Parameters["@lastReferenceId"].Value = phase == "references" ? lastReferenceId : 0;

            return (ToInt32Count(symbols.ExecuteScalar()), ToInt32Count(references.ExecuteScalar()));
        }
        finally
        {
            ReleaseCommand(references);
            ReleaseCommand(symbols);
        }
    }

    private static int ToInt32Count(object? value)
    {
        var count = value is long l ? l : (value is int i ? i : 0);
        return count > int.MaxValue ? int.MaxValue : (int)count;
    }

    internal bool ResolveFoldBackfillRewriteAll(bool rewriteAll)
    {
        if (rewriteAll)
            return true;

        var currentCSharpContract = DbContext.CSharpSymbolNameContractVersion.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        return !string.Equals(
            GetMetaString(DbContext.CSharpSymbolNameContractVersionMetaKey),
            currentCSharpContract,
            StringComparison.Ordinal);
    }

    private int BackfillSymbolFoldedRows(bool rewriteAll, CancellationToken cancellationToken)
    {
        var phase = rewriteAll ? GetMetaString(FoldBackfillPhaseMetaKey) : null;
        if (phase == "references")
            return 0;

        var markdownSymbolIdentityFolds = BuildMarkdownSymbolIdentityFoldMap();
        var lastSymbolId = rewriteAll ? GetFoldBackfillCheckpoint(FoldBackfillLastSymbolIdMetaKey) : 0;
        var rows = new List<(long Id, string Name, string? Lang, string Kind, string? Signature)>();
        var selectSql = rewriteAll
            ? """
              SELECT s.id, s.name, f.lang, s.kind, s.signature
              FROM symbols s
              JOIN files f ON f.id = s.file_id
              WHERE s.name IS NOT NULL AND s.id > @lastSymbolId
              ORDER BY s.id
              """
            : """
              SELECT s.id, s.name, f.lang, s.kind, s.signature
              FROM symbols s
              JOIN files f ON f.id = s.file_id
              WHERE s.name IS NOT NULL
                AND (s.name_folded IS NULL
                     OR (f.lang = 'csharp'
                         AND s.name_folded <> codeindex_name_fold(s.name)
                         AND s.display_name_folded IS NULL))
              """;
        var select = RentCommand(
            selectSql,
            rewriteAll
                ? static c => c.Parameters.Add("@lastSymbolId", SqliteType.Integer)
                : static _ => { });
        try
        {
            if (rewriteAll)
                select.Parameters["@lastSymbolId"].Value = lastSymbolId;
            using var reader = select.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add((
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }
        finally
        {
            ReleaseCommand(select);
        }

        if (rows.Count == 0)
            return 0;

        var update = RentCommand(
            """
            UPDATE symbols
            SET name_folded = @folded,
                display_name_folded = @displayFolded
            WHERE id = @id
            """,
            static c =>
            {
                c.Parameters.Add("@folded", SqliteType.Text);
                c.Parameters.Add("@displayFolded", SqliteType.Text);
                c.Parameters.Add("@id", SqliteType.Integer);
            });
        try
        {
            var pFolded = update.Parameters["@folded"];
            var pDisplayFolded = update.Parameters["@displayFolded"];
            var pId = update.Parameters["@id"];
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var foldedIdentity = FoldPersistedSymbolName(
                    row.Id,
                    row.Name,
                    row.Lang,
                    row.Kind,
                    row.Signature,
                    markdownSymbolIdentityFolds);
                var foldedDisplay = DbReader.FoldNameForLanguage(row.Name, row.Lang);
                pFolded.Value = foldedIdentity;
                pDisplayFolded.Value =
                    string.Equals(row.Lang, "csharp", StringComparison.Ordinal)
                    && !string.Equals(
                        foldedIdentity,
                        foldedDisplay,
                        StringComparison.Ordinal)
                        ? foldedDisplay
                        : DBNull.Value;
                pId.Value = row.Id;
                update.ExecuteNonQuery();
                if (rewriteAll)
                    SetMeta(FoldBackfillLastSymbolIdMetaKey, row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                FoldBackfillRowUpdatedForTesting?.Invoke();
            }
        }
        finally
        {
            ReleaseCommand(update);
        }

        return rows.Count;
    }

    private Dictionary<long, string> BuildMarkdownSymbolIdentityFoldMap()
    {
        var identities = new Dictionary<long, string>();
        var usedHeadingIdentitiesByFile = new Dictionary<long, HashSet<string>>();
        var select = RentCommand(
            """
            SELECT s.id, s.file_id, s.kind, s.name
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE f.lang = 'markdown'
              AND s.kind IN ('heading', 'anchor')
              AND s.name IS NOT NULL
            ORDER BY s.file_id, COALESCE(s.start_line, s.line), s.id
            """,
            static _ => { });
        try
        {
            using var reader = select.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                var symbolId = reader.GetInt64(0);
                var fileId = reader.GetInt64(1);
                var kind = reader.GetString(2);
                var name = reader.GetString(3);
                if (kind == "heading")
                {
                    if (!usedHeadingIdentitiesByFile.TryGetValue(fileId, out var usedHeadingIdentities))
                    {
                        usedHeadingIdentities = new HashSet<string>(StringComparer.Ordinal);
                        usedHeadingIdentitiesByFile.Add(fileId, usedHeadingIdentities);
                    }

                    identities.Add(
                        symbolId,
                        MarkdownAnchorIdentity.CreateUniqueHeadingIdentity(name, usedHeadingIdentities));
                }
                else
                {
                    identities.Add(symbolId, MarkdownAnchorIdentity.NormalizeExplicitAnchorDefinition(name));
                }
            }
        }
        finally
        {
            ReleaseCommand(select);
        }

        return identities;
    }

    private static string FoldPersistedSymbolName(
        long symbolId,
        string name,
        string? lang,
        string kind,
        string? signature,
        IReadOnlyDictionary<long, string> markdownSymbolIdentityFolds)
    {
        if (lang == "markdown"
            && (kind == "heading" || kind == "anchor")
            && markdownSymbolIdentityFolds.TryGetValue(symbolId, out var identity))
        {
            return identity;
        }

        if (lang == "csharp")
        {
            var explicitInterfaceIdentity =
                CSharpSymbolNameNormalizer.BuildExplicitInterfaceIdentityNameFolded(
                    name,
                    signature,
                    kind);
            if (explicitInterfaceIdentity != null)
                return explicitInterfaceIdentity;
        }

        return DbReader.FoldNameForLanguage(name, lang);
    }

    private static string FoldPersistedReferenceName(string name, string? lang, string referenceKind)
    {
        if (lang == "markdown" && referenceKind == "reference")
            return MarkdownAnchorIdentity.NormalizeHeadingFragment(name);

        return DbReader.FoldNameForLanguage(name, lang);
    }

    private int BackfillReferenceFoldedRows(bool rewriteAll, CancellationToken cancellationToken)
    {
        var lastReferenceId = rewriteAll ? GetFoldBackfillCheckpoint(FoldBackfillLastReferenceIdMetaKey) : 0;
        var rows = new List<(long Id, string? SymbolName, string? ContainerName, string? Lang, string ReferenceKind)>();
        var selectSql = rewriteAll
            ? """
              SELECT r.id, r.symbol_name, r.container_name, f.lang, r.reference_kind
              FROM symbol_references r
              JOIN files f ON f.id = r.file_id
              WHERE r.id > @lastReferenceId
                AND (r.symbol_name IS NOT NULL OR r.container_name IS NOT NULL)
              ORDER BY r.id
              """
            : """
              SELECT r.id, r.symbol_name, r.container_name, f.lang, r.reference_kind
              FROM symbol_references r
              JOIN files f ON f.id = r.file_id
              WHERE (r.symbol_name IS NOT NULL AND r.symbol_name_folded IS NULL)
                 OR (r.container_name IS NOT NULL AND r.container_name_folded IS NULL)
              """;
        var select = RentCommand(
            selectSql,
            rewriteAll
                ? static c => c.Parameters.Add("@lastReferenceId", SqliteType.Integer)
                : static _ => { });
        try
        {
            if (rewriteAll)
                select.Parameters["@lastReferenceId"].Value = lastReferenceId;
            using var reader = select.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add((
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4)));
            }
        }
        finally
        {
            ReleaseCommand(select);
        }

        if (rows.Count == 0)
            return 0;

        var update = RentCommand(
            @"UPDATE symbol_references
                               SET symbol_name_folded = @symbolNameFolded,
                                   container_name_folded = @containerNameFolded
                               WHERE id = @id",
            static c =>
            {
                c.Parameters.Add("@symbolNameFolded", SqliteType.Text);
                c.Parameters.Add("@containerNameFolded", SqliteType.Text);
                c.Parameters.Add("@id", SqliteType.Integer);
            });
        try
        {
            var pSymbolNameFolded = update.Parameters["@symbolNameFolded"];
            var pContainerNameFolded = update.Parameters["@containerNameFolded"];
            var pId = update.Parameters["@id"];
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pSymbolNameFolded.Value = row.SymbolName == null
                    ? DBNull.Value
                    : FoldPersistedReferenceName(row.SymbolName, row.Lang, row.ReferenceKind);
                pContainerNameFolded.Value = row.ContainerName == null
                    ? DBNull.Value
                    : DbReader.FoldNameForLanguage(row.ContainerName, row.Lang);
                pId.Value = row.Id;
                update.ExecuteNonQuery();
                if (rewriteAll)
                    SetMeta(FoldBackfillLastReferenceIdMetaKey, row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                FoldBackfillRowUpdatedForTesting?.Invoke();
            }
        }
        finally
        {
            ReleaseCommand(update);
        }

        return rows.Count;
    }

    private long GetFoldBackfillCheckpoint(string key)
    {
        var value = GetMetaString(key);
        return long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    private void ClearFoldBackfillCheckpoint()
    {
        SetMeta(FoldBackfillPhaseMetaKey, null);
        SetMeta(FoldBackfillLastSymbolIdMetaKey, null);
        SetMeta(FoldBackfillLastReferenceIdMetaKey, null);
    }
}
