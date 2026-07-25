using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Indexer.Hooks;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

public static partial class IndexCommandRunner
{
    private static bool PathsEqual(string? left, string? right)
    {
        if (left == null || right == null)
            return false;

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(left, right, comparison);
    }

    private static string GetFoldReadyReason(bool backfillReady, bool foldVersionMatchesCurrent, bool foldFingerprintMatchesCurrent)
    {
        if (!backfillReady)
            return DegradationReasonCodes.MissingFoldBackfill;

        if (!foldVersionMatchesCurrent)
            return DegradationReasonCodes.StaleFoldKeyVersion;

        if (!foldFingerprintMatchesCurrent)
            return DegradationReasonCodes.StaleFoldKeyFingerprint;

        return DegradationReasonCodes.FoldRowsNotRestamped;
    }

    private static string BuildFoldNotReadyExplanation(string? foldReadyReason)
        => DegradationReasonCodes.BuildFoldNotReadyExplanation(foldReadyReason);

    private static string BuildFoldBackfillCommand(string resolvedDbPath)
        => $"cdidx backfill-fold --db {QuoteCommandArgument(resolvedDbPath)}";

    private static string BuildFoldRebuildCommand(string projectRoot, string resolvedDbPath)
        => $"cdidx index {QuoteCommandArgument(projectRoot)} --db {QuoteCommandArgument(resolvedDbPath)} --rebuild";

    private static IReadOnlyList<ChunkRecord> ReassignChunkFileIds(IReadOnlyList<ChunkRecord> chunks, long fileId)
    {
        foreach (var chunk in chunks)
            chunk.FileId = fileId;
        return chunks;
    }

    private static IReadOnlyList<SymbolRecord> ReassignSymbolFileIds(IReadOnlyList<SymbolRecord> symbols, long fileId)
    {
        foreach (var symbol in symbols)
            symbol.FileId = fileId;
        return symbols;
    }

    private static IReadOnlyList<ReferenceRecord> ReassignReferenceFileIds(IReadOnlyList<ReferenceRecord> references, long fileId)
    {
        foreach (var reference in references)
            reference.FileId = fileId;
        return references;
    }

    private static IList<T> AsMutableList<T>(IReadOnlyList<T> records)
    {
        if (records is IList<T> mutable)
            return mutable;

        throw new InvalidOperationException("Post-extraction hooks require mutable extraction result lists.");
    }

    private static IReadOnlyList<FileIssue> RequireWorkItemIssues(FullScanFileWorkItem item)
    {
        return item.Issues ?? throw new InvalidOperationException("Full-scan work item does not carry precomputed validation issues.");
    }

    private static int AddPostExtractionHookWarnings(PostExtractionHookRunner? runner, List<CliJsonMessage> warningList)
    {
        if (runner == null)
            return 0;

        var added = 0;
        foreach (var diagnostic in runner.Diagnostics)
        {
            warningList.Add(new CliJsonMessage(
                string.IsNullOrWhiteSpace(diagnostic.TypeName) ? diagnostic.AssemblyPath : diagnostic.TypeName,
                diagnostic.Message));
            added++;
        }

        return added;
    }

    private static FoldOnlyRemediation? BuildFoldOnlyReadinessRemediation(
        bool graphTableAvailable,
        bool issuesTableAvailable,
        bool sqlGraphContractReady,
        bool hotspotFamilyReady,
        bool csharpSymbolNameReady,
        bool csharpMetadataTargetReady,
        bool foldReady,
        string? foldReadyReason,
        string projectRoot,
        string resolvedDbPath)
    {
        if (!IsFoldOnlyReadinessDegraded(
                graphTableAvailable,
                issuesTableAvailable,
                sqlGraphContractReady,
                hotspotFamilyReady,
                csharpSymbolNameReady,
                csharpMetadataTargetReady,
                foldReady))
        {
            return null;
        }

        return new FoldOnlyRemediation(
            BuildFoldNotReadyExplanation(foldReadyReason),
            BuildFoldBackfillCommand(resolvedDbPath),
            BuildFoldRebuildCommand(projectRoot, resolvedDbPath));
    }

    private static bool IsFoldOnlyReadinessDegraded(
        bool graphTableAvailable,
        bool issuesTableAvailable,
        bool sqlGraphContractReady,
        bool hotspotFamilyReady,
        bool csharpSymbolNameReady,
        bool csharpMetadataTargetReady,
        bool foldReady)
        => !foldReady
           && graphTableAvailable
           && issuesTableAvailable
           && sqlGraphContractReady
           && hotspotFamilyReady
           && csharpSymbolNameReady
           && csharpMetadataTargetReady;

    private static string GetIndexReadinessWarning(bool graphTableAvailable, bool issuesTableAvailable, bool sqlGraphContractReady, bool hotspotFamilyReady, bool csharpSymbolNameReady, bool csharpMetadataTargetReady, bool foldReady, string? foldReadyReason, string projectRoot, string resolvedDbPath)
    {
        var foldOnlyRemediation = BuildFoldOnlyReadinessRemediation(
            graphTableAvailable,
            issuesTableAvailable,
            sqlGraphContractReady,
            hotspotFamilyReady,
            csharpSymbolNameReady,
            csharpMetadataTargetReady,
            foldReady,
            foldReadyReason,
            projectRoot,
            resolvedDbPath);
        if (foldOnlyRemediation != null)
        {
            return $"Index completed with fold-only degraded readiness (fold_ready=false). {foldOnlyRemediation.DegradedReason} Run `{foldOnlyRemediation.RecommendedAction}` to restamp folded-name columns in place, or `{foldOnlyRemediation.AlternativeAction}` for a full rebuild.";
        }

        var degradedParts = new List<string>();
        if (!graphTableAvailable)
            degradedParts.Add(DegradationReasonCodes.GraphTableMissing);
        if (!issuesTableAvailable)
            degradedParts.Add(DegradationReasonCodes.IssuesTableMissing);
        if (!sqlGraphContractReady)
            degradedParts.Add(DegradationReasonCodes.SqlGraphContractNotReady);
        if (!hotspotFamilyReady)
            degradedParts.Add(DegradationReasonCodes.HotspotFamilyNotReady);
        if (!csharpSymbolNameReady)
            degradedParts.Add(DegradationReasonCodes.CSharpSymbolNameNotReady);
        if (!csharpMetadataTargetReady)
            degradedParts.Add(DegradationReasonCodes.CSharpMetadataTargetNotReady);
        if (!foldReady)
            degradedParts.Add(DegradationReasonCodes.FoldReadyNotReady);

        return $"Index completed with degraded readiness ({string.Join(", ", degradedParts)}). Run `cdidx status --db \"{resolvedDbPath}\" --json` to inspect the current DB state.";
    }

    private static string QuoteCommandArgument(string value)
    {
        var fullPath = DbPathResolver.NormalizeDbPath(value);
        if (!fullPath.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            fullPath = Path.GetFullPath(fullPath);

        return fullPath.IndexOfAny([' ', '\t', '"']) >= 0
            ? $"\"{fullPath.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : fullPath;
    }
}
