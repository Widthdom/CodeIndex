using Microsoft.Data.Sqlite;

namespace CodeIndex.Database;

public partial class DbWriter
{
    // End-of-successful-index trust markers. The ready bits live in PRAGMA user_version so
    // that a reader can tell which subset of the index has been fully populated:
    //   bit 0 (GraphReadyFlag)  — symbol_references fully backfilled
    //   bit 1 (IssuesReadyFlag) — file_issues produced by ValidateContent
    //   bit 2 (FoldReadyFlag)   — name_folded columns populated for Unicode --exact (#86)
    // CLI and MCP full-scan indexing set graph + fold; CLI additionally sets issues (MCP
    // now persists file_issues too after bdbb2bd, so both can stamp it). The index runner
    // ClearReadyFlags() first so partial / aborted runs demote trust until a successful
    // end-of-run commit. Fold is only stamped after a full scan because a partial update
    // leaves legacy rows without folded values.
    // CLI / MCP 共に full-scan で graph + fold を立てる。fold は部分更新では立てない。
    public void MarkGraphReady() => SetReadyBit(DbContext.GraphReadyFlag);
    public void MarkIssuesReady() => SetReadyBit(DbContext.IssuesReadyFlag);

    /// <summary>
    /// Stamp FoldReadyFlag AND write the current <see cref="NameFold.Version"/> plus the
    /// runtime-sensitive <see cref="NameFold.Fingerprint"/> into `codeindex_meta`.
    /// Readers require the bit, a version match, and a fingerprint match before trusting
    /// folded columns, so both intentional fold changes and runtime ICU / invariant-casing
    /// drift degrade safely to NOCASE until `--rebuild`. Issue #97.
    ///
    /// Re-verifies <see cref="AllFoldedColumnsBackfilled"/> inside a BEGIN IMMEDIATE
    /// transaction so a concurrent writer cannot insert NULL-folded rows between the
    /// caller's pre-check and this stamp. Returns false (and writes nothing) when the
    /// re-verify fails, so callers can surface a friendly retry message instead of
    /// silently advertising fold-trust to readers. Issue #1535.
    /// FoldReady bit + fold_key_version + fold_key_fingerprint を書く。runtime drift を含む
    /// silent mismatch を防ぎ、ズレた場合は `--rebuild` まで NOCASE fallback に降格する。
    /// BEGIN IMMEDIATE で囲んだうえで再検証し、concurrent writer による NULL 行差し込みで
    /// fold_ready が嘘になるのを防ぐ。Issue #1535。
    /// </summary>
    /// <returns>True when the bit was actually stamped; false when re-verification failed.</returns>
    public bool MarkFoldReady(
        bool stampCurrentSymbolExtractorVersions = false,
        IReadOnlyCollection<string>? symbolExtractorLanguagesToStamp = null)
    {
        var gateLease = EnterTransactionGate();
        try
        {
            bool ownTransaction = !IsInTransaction();
            if (ownTransaction)
                Execute("BEGIN IMMEDIATE");
            try
            {
                if (stampCurrentSymbolExtractorVersions)
                    StampSymbolExtractorVersions(symbolExtractorLanguagesToStamp);

                if (!AllFoldedColumnsBackfilledCore(
                        requireCurrentSymbolExtractorVersions: false,
                        requireCurrentFoldKeys: true))
                {
                    if (ownTransaction)
                    {
                        Execute("COMMIT");
                        ownTransaction = false;
                    }
                    return false;
                }

                ApplyReadyBitToUserVersion(DbContext.FoldReadyFlag, ownTransaction ? null : _activeTransaction);

                SetMetaValues(
                    ("fold_key_version", NameFold.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("fold_key_fingerprint", NameFold.Fingerprint()));
                StampSymbolExtractorVersions(symbolExtractorLanguagesToStamp);

                if (ownTransaction)
                {
                    Execute("COMMIT");
                    ownTransaction = false;
                }
                return true;
            }
            catch (Exception)
            {
                if (ownTransaction)
                {
                    try { Execute("ROLLBACK"); } catch (SqliteException) { /* best effort */ }
                }
                throw;
            }
        }
        finally
        {
            gateLease.Dispose();
        }
    }

    public void ClearReadyFlags() => Execute("PRAGMA user_version = 0");

    private void SetReadyBit(int flag)
    {
        var gateLease = EnterTransactionGate();
        try
        {
            // The ready bits share a single PRAGMA user_version word, so two parallel
            // cdidx writers (e.g. CI + a local rebuild) can each read the same prior
            // value, OR in their own flag, and the slower writer's PRAGMA write clobbers
            // the faster writer's flag. Wrap the read-modify-write in BEGIN IMMEDIATE so
            // SQLite's reserved write lock serialises it across processes (issue #1513).
            // Use raw BEGIN/COMMIT instead of a provider-managed transaction object here:
            // PRAGMA user_version updates are connection-level metadata, and keeping this
            // path to plain SQL avoids provider transaction state leaking across pooled
            // connections under highly parallel release tests.
            bool ownTransaction = !IsInTransaction();
            bool beganTransaction = false;
            if (ownTransaction)
            {
                Execute("BEGIN IMMEDIATE");
                beganTransaction = true;
            }
            var transaction = ownTransaction ? null : _activeTransaction;
            try
            {
                ApplyReadyBitToUserVersion(flag, transaction);
                if (ownTransaction)
                {
                    Execute("COMMIT");
                    beganTransaction = false;
                }
            }
            catch (Exception)
            {
                if (beganTransaction)
                {
                    try { Execute("ROLLBACK"); } catch (SqliteException) { /* best effort */ }
                }
                throw;
            }
        }
        finally
        {
            gateLease.Dispose();
        }
    }

    private void ApplyReadyBitToUserVersion(int flag, SqliteTransaction? transaction)
    {
        int current;
        using (var read = _conn.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "PRAGMA user_version";
            var raw = read.ExecuteScalar();
            current = raw is long l ? (int)l : (raw is int i ? i : 0);
        }

        int next = current | flag;
        if (next != current)
            Execute($"PRAGMA user_version = {next}", transaction);
    }
}
