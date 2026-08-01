using System.Text.Json.Nodes;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Models;

namespace CodeIndex.Mcp;

public partial class McpServer
{
    private async Task<JsonNode> ExecuteBackfillFoldAsync(JsonNode? id, JsonNode? args, JsonNode? progressToken = null)
    {
        var requestToken = _currentRequestToken.Value;
        var dryRun = args?["dry_run"]?.GetValue<bool>() ?? args?["dryRun"]?.GetValue<bool>() ?? false;
        if (!DbContext.TryValidateExistingCodeIndexDb(
                _dbPath,
                requireWritable: !dryRun,
                requireSupportedUserVersion: false,
                out var validationMessage,
                out var isNotFound,
                out _,
                requestToken))
        {
            var detail = isNotFound
                ? $"Database not found: {_dbPath}. Run 'cdidx index <projectPath>' first."
                : $"Database is not an existing CodeIndex DB: {_dbPath}. Run 'cdidx index <projectPath>' first.";
            if (validationMessage.StartsWith("database must be writable", StringComparison.Ordinal))
                detail = $"Database must be writable for backfill_fold: {_dbPath}.";
            return CreateToolErrorResponse(id, detail);
        }

        try
        {
            // Reuse the per-session DbContext (issue #1494). InitializeSchema is idempotent
            // and remains correct on a long-lived connection.
            // セッション共有 DbContext を再利用する（#1494）。InitializeSchema は冪等。
            var db = GetOrOpenSharedDb(dryRun ? DbOpenIntent.QueryOnly : DbOpenIntent.Migration);
            if (!dryRun)
                db.InitializeSchema();
            var writer = new DbWriter(db);
            if (writer.TryGetNewerCSharpSymbolNameContractVersion(out var newerCSharpContract))
            {
                return CreateToolErrorResponse(
                    id,
                    $"C# symbol-name contract version {newerCSharpContract} is newer than supported version {DbContext.CSharpSymbolNameContractVersion}. Use the same or a newer CodeIndex version that wrote this database; this version will not rewrite or downgrade its C# identities.");
            }
            var userVersionBefore = db.GetUserVersion();
            var foldReadyBefore = (userVersionBefore & DbContext.FoldReadyFlag) != 0;
            var currentFoldVersion = NameFold.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var currentFoldFingerprint = NameFold.Fingerprint();
            var storedFoldVersion = db.GetMetaString("fold_key_version");
            var storedFoldFingerprint = db.GetMetaString("fold_key_fingerprint");
            var foldMetadataCurrentBefore = storedFoldVersion == currentFoldVersion
                && storedFoldFingerprint == currentFoldFingerprint;
            var csharpSymbolNameContractUpgradeRequired =
                writer.HasAnyFilesWithLanguage("csharp")
                && !string.Equals(
                db.GetMetaString(DbContext.CSharpSymbolNameContractVersionMetaKey),
                DbContext.CSharpSymbolNameContractVersion.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
            if (csharpSymbolNameContractUpgradeRequired
                && !writer.CanReconstructCSharpExplicitInterfaceIdentitiesFromPersistedRows())
            {
                return CreateToolErrorResponse(
                    id,
                    "C# explicit-interface identities cannot be reconstructed because legacy symbol signatures are missing. Refresh the C# files with the index tool (or rebuild the index), then retry backfill_fold.");
            }
            foldReadyBefore = foldReadyBefore && foldMetadataCurrentBefore;
            var force = args?["force"]?.GetValue<bool>() ?? false;
            var rewriteAll = writer.ResolveFoldBackfillRewriteAll(
                force
                || !foldMetadataCurrentBefore
                || writer.HasFoldBackfillRewriteCheckpoint());
            (var totalSymbols, var totalSymbolReferences) =
                writer.CountBackfillFoldedColumns(rewriteAll);
            if (!rewriteAll
                && (totalSymbols > 0 || totalSymbolReferences > 0)
                && !writer.AllPresentFoldedColumnValuesMatchCurrentFold())
            {
                // Keep MCP aligned with the CLI: mixed missing and non-current folded
                // values require one full repair rather than a partial pass that cannot verify.
                // CLI と同様に、missing と non-current の folded 値が混在する場合は
                // 検証不能な partial pass ではなく1回の全行修復へ昇格する。
                rewriteAll = true;
                (totalSymbols, totalSymbolReferences) =
                    writer.CountBackfillFoldedColumns(rewriteAll);
            }
            var symbols = 0;
            var symbolReferences = 0;
            var verified = false;
            var userVersionAfter = userVersionBefore;
            if (dryRun)
            {
                symbols = totalSymbols;
                symbolReferences = totalSymbolReferences;
            }
            else
            {
                await EmitProgressNotificationAsync(progressToken, 0, null, "Backfilling folded-name keys.").ConfigureAwait(false);
                (symbols, symbolReferences) = writer.BackfillFoldedColumns(rewriteAll);
                await EmitProgressNotificationAsync(progressToken, symbols + symbolReferences, totalSymbols + totalSymbolReferences, "Verifying folded-name keys.").ConfigureAwait(false);
                // Row rewrites are intentionally committed before the final FoldReady stamp so
                // interrupted MCP backfills can resume from the remaining rows.
                // 行更新は FoldReady stamp より前に永続化し、中断後に残り行から再開できるようにする。
                using var transaction = writer.BeginTransaction();
                verified = writer.MarkFoldReady();
                if (!verified)
                    return CreateToolErrorResponse(id, "Folded-name backfill verification failed: some rows still have NULL folded values. Re-run backfill_fold.");
                writer.MarkCSharpSymbolNameContractReady();

                transaction.Commit();
                userVersionAfter = db.GetUserVersion();
                await EmitProgressNotificationAsync(progressToken, symbols + symbolReferences, symbols + symbolReferences, "Folded-name backfill complete.").ConfigureAwait(false);
            }

            var foldMetadataCurrentAfter = dryRun
                ? foldMetadataCurrentBefore
                : true;
            var foldReadyAfter = (userVersionAfter & DbContext.FoldReadyFlag) != 0
                && foldMetadataCurrentAfter;
            var wasAlreadyComplete = foldReadyBefore && !rewriteAll && symbols == 0 && symbolReferences == 0;

            var payload = new JsonObject
            {
                ["symbols"] = symbols,
                ["symbol_references"] = symbolReferences,
                ["rewrite_all"] = rewriteAll,
                ["dry_run"] = dryRun,
                ["force"] = force,
                ["was_already_complete"] = wasAlreadyComplete,
                ["fold_ready_before"] = foldReadyBefore,
                ["fold_ready_after"] = foldReadyAfter,
                ["verified"] = verified,
                ["user_version_before"] = userVersionBefore,
                ["user_version_after"] = userVersionAfter,
                ["fold_ready"] = foldReadyAfter,
                ["fold_key_version_before"] = storedFoldVersion,
                ["fold_key_version_after"] = dryRun ? storedFoldVersion : currentFoldVersion,
                ["fold_key_fingerprint_before"] = storedFoldFingerprint,
                ["fold_key_fingerprint_after"] = dryRun ? storedFoldFingerprint : currentFoldFingerprint,
                ["progress"] = BuildBackfillProgressJson(symbols + symbolReferences, totalSymbols + totalSymbolReferences),
            };

            var summary = dryRun
                ? "Folded-name backfill preview complete."
                : rewriteAll
                ? "Folded-name keys refreshed and FoldReady stamped."
                : "Missing folded-name keys backfilled and FoldReady stamped.";
            return CreateToolResult(id, summary, payload);
        }
        catch (Exception ex)
        {
            var dbDebugDump = Database.DbDebug.CaptureDump(ex);
            DeferFrameLog(() =>
            {
                WriteMcpLogLine(BuildToolErrorLog("backfill_fold", ex));
                Database.DbDebug.WriteCapturedDumpToStderr(dbDebugDump);
            });
            var classification = McpErrorEnvelope.ClassifyException(ex);
            return CreateToolErrorResponse(id, BuildSanitizedToolErrorMessage("backfill_fold", ex),
                category: classification.Category,
                suggestion: classification.Suggestion,
                retrySafe: classification.RetrySafe,
                extraData: new JsonObject
                {
                    ["tool"] = "backfill_fold",
                    ["exception_type"] = ex.GetType().Name,
                });
        }
    }

    private static JsonObject BuildBackfillProgressJson(int rowsDone, int rowsTotal)
    {
        var fraction = rowsTotal <= 0 ? 1.0 : Math.Min(1.0, rowsDone / (double)rowsTotal);
        return new JsonObject
        {
            ["rows_done"] = rowsDone,
            ["rows_total"] = rowsTotal,
            ["fraction"] = fraction,
        };
    }


}
