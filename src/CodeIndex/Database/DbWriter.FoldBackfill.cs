using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbWriter
{
    private const string FoldBackfillPhaseMetaKey = "fold_backfill_phase";
    private const string FoldBackfillLastSymbolIdMetaKey = "fold_backfill_last_symbol_id";
    private const string FoldBackfillLastReferenceIdMetaKey = "fold_backfill_last_reference_id";

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
        var symbols = RentCommand(
            "SELECT name, name_folded FROM symbols WHERE name IS NOT NULL",
            static _ => { });
        try
        {
            using var reader = symbols.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                var expected = NameFold.Fold(reader.GetString(0));
                var actual = reader.IsDBNull(1) ? null : reader.GetString(1);
                if (!string.Equals(actual, expected, StringComparison.Ordinal))
                    return false;
            }
        }
        finally
        {
            ReleaseCommand(symbols);
        }

        var references = RentCommand(
            @"
                SELECT symbol_name, symbol_name_folded, container_name, container_name_folded
                FROM symbol_references
                WHERE symbol_name IS NOT NULL OR container_name IS NOT NULL",
            static _ => { });
        try
        {
            using var reader = references.ExecuteTrackedReader();
            while (reader.TrackedRead())
            {
                if (!reader.IsDBNull(0))
                {
                    var expected = NameFold.Fold(reader.GetString(0));
                    var actual = reader.IsDBNull(1) ? null : reader.GetString(1);
                    if (!string.Equals(actual, expected, StringComparison.Ordinal))
                        return false;
                }

                if (!reader.IsDBNull(2))
                {
                    var expected = NameFold.Fold(reader.GetString(2));
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
            return !SymbolExtractor.RequiresExplicitReferenceGraphContractStamp(lang);

        var current = SymbolExtractor.GetContractVersion(lang).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return stored == current;
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
        cancellationToken.ThrowIfCancellationRequested();
        var foldBackfillPhase = rewriteAll ? GetMetaString(FoldBackfillPhaseMetaKey) : null;
        var symbols = BackfillSymbolFoldedRows(rewriteAll, cancellationToken);
        if (rewriteAll && foldBackfillPhase != "references")
        {
            SetMeta(FoldBackfillPhaseMetaKey, "references");
            SetMeta(FoldBackfillLastReferenceIdMetaKey, "0");
        }

        var symbolReferences = BackfillReferenceFoldedRows(rewriteAll, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (rewriteAll)
            ClearFoldBackfillCheckpoint();

        return (symbols, symbolReferences);
    }

    public (int Symbols, int SymbolReferences) CountBackfillFoldedColumns(bool rewriteAll = false)
    {
        var phase = rewriteAll ? GetMetaString(FoldBackfillPhaseMetaKey) : null;
        var lastSymbolId = rewriteAll ? GetFoldBackfillCheckpoint(FoldBackfillLastSymbolIdMetaKey) : 0;
        var lastReferenceId = rewriteAll ? GetFoldBackfillCheckpoint(FoldBackfillLastReferenceIdMetaKey) : 0;

        var symbolsSql = rewriteAll && phase != "references"
            ? "SELECT COUNT(*) FROM symbols WHERE name IS NOT NULL AND id > @lastSymbolId"
            : rewriteAll
            ? "SELECT 0"
            : "SELECT COUNT(*) FROM symbols WHERE name IS NOT NULL AND name_folded IS NULL";
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

    private int BackfillSymbolFoldedRows(bool rewriteAll, CancellationToken cancellationToken)
    {
        var phase = rewriteAll ? GetMetaString(FoldBackfillPhaseMetaKey) : null;
        if (phase == "references")
            return 0;

        var lastSymbolId = rewriteAll ? GetFoldBackfillCheckpoint(FoldBackfillLastSymbolIdMetaKey) : 0;
        var rows = new List<(long Id, string Name)>();
        var selectSql = rewriteAll
            ? "SELECT id, name FROM symbols WHERE name IS NOT NULL AND id > @lastSymbolId ORDER BY id"
            : "SELECT id, name FROM symbols WHERE name IS NOT NULL AND name_folded IS NULL";
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
                rows.Add((reader.GetInt64(0), reader.GetString(1)));
            }
        }
        finally
        {
            ReleaseCommand(select);
        }

        if (rows.Count == 0)
            return 0;

        var update = RentCommand(
            "UPDATE symbols SET name_folded = @folded WHERE id = @id",
            static c =>
            {
                c.Parameters.Add("@folded", SqliteType.Text);
                c.Parameters.Add("@id", SqliteType.Integer);
            });
        try
        {
            var pFolded = update.Parameters["@folded"];
            var pId = update.Parameters["@id"];
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pFolded.Value = (object?)NameFold.Fold(row.Name) ?? DBNull.Value;
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

    private int BackfillReferenceFoldedRows(bool rewriteAll, CancellationToken cancellationToken)
    {
        var lastReferenceId = rewriteAll ? GetFoldBackfillCheckpoint(FoldBackfillLastReferenceIdMetaKey) : 0;
        var rows = new List<(long Id, string? SymbolName, string? ContainerName)>();
        var selectSql = rewriteAll
            ? @"SELECT id, symbol_name, container_name
                    FROM symbol_references
                    WHERE id > @lastReferenceId
                      AND (symbol_name IS NOT NULL OR container_name IS NOT NULL)
                    ORDER BY id"
            : @"SELECT id, symbol_name, container_name
                    FROM symbol_references
                    WHERE (symbol_name IS NOT NULL AND symbol_name_folded IS NULL)
                       OR (container_name IS NOT NULL AND container_name_folded IS NULL)";
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
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
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
                pSymbolNameFolded.Value = (object?)NameFold.Fold(row.SymbolName) ?? DBNull.Value;
                pContainerNameFolded.Value = (object?)NameFold.Fold(row.ContainerName) ?? DBNull.Value;
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
