using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text.Json;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private static void StampIndexedHeadMetadata(DbWriter writer, string projectRoot, List<string>? diagnostics, CancellationToken cancellationToken)
    {
        try
        {
            var headSha = GitHelper.TryGetHeadCommit(projectRoot, cancellationToken);
            var headBranch = GitHelper.TryGetHeadBranch(projectRoot, cancellationToken);
            StampIndexedHeadMetadata(writer, headSha, headBranch);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Best-effort metadata only; never fail an otherwise-successful index run.
            // best-effort であり、stamp の失敗で index 全体を失敗扱いにしない。
            RecordIndexRunDiagnostic(diagnostics, "indexed_head_metadata_write_failed", ex);
        }
        StampWorkspacePathCaseSensitivity(writer, projectRoot, diagnostics, cancellationToken);
    }

    private static void StampIndexedSymlinkPolicy(DbWriter writer, FileIndexer.SymlinkPolicy symlinkPolicy, List<string>? diagnostics)
    {
        try
        {
            writer.SetMeta(
                DbContext.IndexedFollowSymlinksPolicyMetaKey,
                symlinkPolicy.ToString().ToLowerInvariant());
        }
        catch (Exception ex)
        {
            // Best-effort metadata only; never fail an otherwise-successful index run.
            // best-effort のみ。stamp 失敗で index 全体を落とさない。
            RecordIndexRunDiagnostic(diagnostics, "indexed_symlink_policy_metadata_write_failed", ex);
        }
    }

    private static void StampIndexedHeadMetadata(DbWriter writer, string? headSha, string? headBranch)
    {
        var timestamp = headSha != null
            ? GetUtcNow().ToString("o", System.Globalization.CultureInfo.InvariantCulture)
            : null;
        writer.SetMetaValues(
            (DbContext.IndexedHeadShaMetaKey, headSha),
            (DbContext.IndexedHeadBranchMetaKey, headBranch),
            (DbContext.IndexedHeadTimestampMetaKey, timestamp));
    }

    private static void TryStampIndexedHeadMetadata(DbWriter writer, string? headSha, string? headBranch, List<string>? diagnostics)
    {
        try
        {
            StampIndexedHeadMetadata(writer, headSha, headBranch);
        }
        catch (Exception ex)
        {
            // Best-effort metadata only; never fail an otherwise-successful index run.
            // best-effort であり、stamp の失敗で index 全体を失敗扱いにしない。
            RecordIndexRunDiagnostic(diagnostics, "indexed_head_metadata_write_failed", ex);
        }
    }

    private static void StampCommitScopedFreshHeadMetadata(
        DbWriter writer,
        string? priorWorkspaceVerifiedHead,
        string? currentHeadCommit,
        bool workspaceHeadCoverageVerified,
        List<string>? diagnostics)
    {
        try
        {
            var verifiedHead = workspaceHeadCoverageVerified
                && !string.IsNullOrWhiteSpace(currentHeadCommit)
                    ? currentHeadCommit
                    : priorWorkspaceVerifiedHead;
            writer.SetMeta(DbContext.WorkspaceVerifiedHeadShaMetaKey, verifiedHead);
            var coveredHead = !string.IsNullOrWhiteSpace(currentHeadCommit)
                && string.Equals(verifiedHead, currentHeadCommit, StringComparison.OrdinalIgnoreCase)
                    ? currentHeadCommit
                    : null;
            writer.SetMeta(DbContext.CommitScopedFreshHeadShaMetaKey, coveredHead);
        }
        catch (Exception ex)
        {
            // Best-effort metadata only; never fail an otherwise-successful index run.
            // best-effort のみ。stamp 失敗で index 全体を落とさない。
            RecordIndexRunDiagnostic(diagnostics, "commit_scoped_head_metadata_write_failed", ex);
        }
    }

    private static bool GitRefCoversCurrentHead(
        string projectRoot,
        string refName,
        string currentHeadCommit,
        CancellationToken cancellationToken)
    {
        if (currentHeadCommit.StartsWith(refName, StringComparison.OrdinalIgnoreCase))
            return true;

        var resolvedRef = GitHelper.TryResolveCommit(projectRoot, refName, cancellationToken);
        return string.Equals(resolvedRef, currentHeadCommit, StringComparison.OrdinalIgnoreCase);
    }

    // Issue #1546: capture the actual case-sensitivity of the workspace filesystem so
    // `cdidx status` can diagnose phantom path collapses on case-sensitive APFS / WSL
    // NTFS / ReFS volumes (where the OS-keyed heuristic would mismatch reality). Probed
    // via the same `core.ignorecase` + filesystem probe used by FileIndexer, then
    // persisted as "true" / "false" alongside the HEAD stamp. Failures are swallowed so
    // an unwritable git config / temp probe never blocks an otherwise-successful index.
    // #1546: workspace FS の大小区別を実プローブして codeindex_meta に保存する。
    // probe 失敗時は黙って null stamp にして index 本体は成功扱いのままとする。
    private static void StampWorkspacePathCaseSensitivity(DbWriter writer, string projectRoot, List<string>? diagnostics, CancellationToken cancellationToken)
    {
        try
        {
            var ignoreCase = GitHelper.ResolveIgnoreCase(projectRoot, cancellationToken);
            PathCasing.SeedFromWorkspace(projectRoot, ignoreCase);
            var caseSensitive = (!ignoreCase).ToString(System.Globalization.CultureInfo.InvariantCulture);
            writer.SetMeta(DbContext.WorkspacePathCaseSensitiveMetaKey, caseSensitive);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Best-effort metadata only; never fail an otherwise-successful index run.
            // best-effort のみ。stamp 失敗で index 全体を落とさない。
            RecordIndexRunDiagnostic(diagnostics, "path_case_sensitivity_metadata_write_failed", ex);
        }
    }
}
