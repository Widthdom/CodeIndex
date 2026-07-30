using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

public static partial class DbCommandRunner
{
    private static int RunPrune(DbCommandOptions options, JsonSerializerOptions jsonOptions, string dbPath, bool isUri, CancellationToken cancellationToken)
    {
        if (!options.PruneApply && !options.PruneDryRun)
            return WriteCommandError(
                options.Json,
                jsonOptions,
                "db prune requires --dry-run or --apply",
                CommandExitCodes.UsageError,
                "Use `cdidx db prune --dry-run` to inspect stale rows, then `cdidx db prune --apply` to delete them.",
                CommandErrorCodes.UsageError);

        if (options.PruneApply && options.PruneDryRun)
            return WriteCommandError(
                options.Json,
                jsonOptions,
                "db prune accepts only one of --dry-run or --apply",
                CommandExitCodes.UsageError,
                "Choose `--dry-run` or `--apply`.",
                CommandErrorCodes.UsageError);

        if (isUri && DbPathResolver.UriRequestsReadOnly(dbPath))
            return WriteCommandError(
                options.Json,
                jsonOptions,
                $"database must be writable for prune: {dbPath}",
                CommandExitCodes.DatabaseError,
                "Point `--db` at a writable filesystem path, or omit read-only URI parameters such as `immutable=1` / `mode=ro`.",
                CommandErrorCodes.DbNotWritable);

        try
        {
            ReportMaintenanceProgress("prune", "start", dbPath);
            var result = PruneOrphans(dbPath, apply: options.PruneApply, cancellationToken);
            ReportMaintenanceProgress("prune", "complete", dbPath);
            var fullPath = DbPathResolver.FormatDbPathForDisplay(dbPath);
            if (options.Json)
            {
                var jsonContext = CliJsonSerializerContextFactory.Create(jsonOptions);
                Console.WriteLine(JsonSerializer.Serialize(
                    new DbPruneJsonResult(
                        "success",
                        fullPath,
                        options.PruneDryRun,
                        result.OrphanSymbolReferences,
                        result.OrphanReferenceLines,
                        result.OrphanSymbols,
                        result.Total,
                        result.Warnings),
                    jsonContext.DbPruneJsonResult));
            }
            else
            {
                Console.WriteLine(options.PruneApply ? "Pruned database stale rows." : "Database prune dry run.");
                Console.WriteLine($"  database                 : {fullPath}");
                Console.WriteLine($"  orphan symbol_references : {result.OrphanSymbolReferences:N0}");
                Console.WriteLine($"  orphan reference_lines   : {result.OrphanReferenceLines:N0}");
                Console.WriteLine($"  orphan symbols           : {result.OrphanSymbols:N0}");
                Console.WriteLine($"  total                    : {result.Total:N0}");
                foreach (var warning in result.Warnings)
                    CommandErrorWriter.WriteStderr($"Warning [{warning.Code}]: {warning.Message}");
            }

            return CommandExitCodes.Success;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (JsonOutputFailure.TryHandle(ex, out var exitCode))
                return exitCode;

            return WriteCommandError(
                options.Json,
                jsonOptions,
                $"failed to prune database: {CommandErrorWriter.FormatSanitizedExceptionMessage(ex)}",
                CommandExitCodes.DatabaseError,
                "Ensure no other writer is holding the database lock, then retry `cdidx db prune --dry-run`.",
                CommandErrorCodes.DbError);
        }
    }

    private static int RunCheckpoint(DbCommandOptions options, JsonSerializerOptions jsonOptions)
    {
        if (!ValidateWritableFileDb(options, jsonOptions, "checkpoint", out var fullDbPath, out var validationExitCode))
            return validationExitCode;

        try
        {
            var plan = PlanCheckpoint(fullDbPath, options.Name ?? MakeTimestampCheckpointName());
            if (options.CheckpointDryRun)
            {
                if (options.Json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(
                        new DbCheckpointJsonResult(
                            "dry_run",
                            fullDbPath,
                            plan.Name,
                            plan.CheckpointPath,
                            plan.SourceFiles.Select(source => source.OutputName).ToList(),
                            plan.SourceFilesTruncated,
                            CheckpointFileInspectLimit,
                            plan.Diagnostics.ToList(),
                            DryRun: true,
                            Bytes: plan.SourceBytes,
                            SourceFiles: plan.SourceFiles.Select(source => source.OutputName).ToList(),
                            SourceBytes: plan.SourceBytes,
                            PlannedOutputFiles: plan.PlannedOutputFiles.ToList(),
                            EstimatedOutputBytes: plan.EstimatedOutputBytes,
                            Ready: plan.Ready,
                            DestinationExists: plan.DestinationExists,
                            DestinationPolicy: plan.DestinationPolicy,
                            ConflictPolicy: plan.ConflictPolicy,
                            Uncertainty: plan.Uncertainty,
                            ManifestSchema: plan.ManifestSchema,
                            ManifestContents: plan.ManifestContents,
                            ManifestSha256: plan.ManifestSha256,
                            SidecarPolicy: plan.SidecarPolicy,
                            Compression: plan.CompressionPolicy,
                            MetadataPolicy: plan.MetadataPolicy),
                        CliJsonSerializerContextFactory.Create(jsonOptions).DbCheckpointJsonResult));
                }
                else
                {
                    Console.WriteLine("Database checkpoint dry run.");
                    Console.WriteLine($"  database  : {fullDbPath}");
                    WriteCheckpointPlan(plan);
                    Console.WriteLine("  side effect: none");
                }

                foreach (var diagnostic in plan.Diagnostics)
                    CommandErrorWriter.WriteStderr($"Warning [{diagnostic.Code}]: {diagnostic.Message}");

                return CommandExitCodes.Success;
            }

            var result = CreateCheckpoint(plan);
            if (options.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    new DbCheckpointJsonResult(
                        "success",
                        fullDbPath,
                        result.Name,
                        result.CheckpointPath,
                        result.Files,
                        result.FilesTruncated,
                        CheckpointFileInspectLimit,
                        result.Diagnostics,
                        Bytes: result.Bytes,
                        SourceFiles: plan.SourceFiles.Select(source => source.OutputName).ToList(),
                        SourceBytes: plan.SourceBytes,
                        PlannedOutputFiles: plan.PlannedOutputFiles.ToList(),
                        EstimatedOutputBytes: plan.EstimatedOutputBytes,
                        FinalOutputBytes: plan.EstimatedOutputBytes,
                        Ready: plan.Ready,
                        DestinationExists: plan.DestinationExists,
                        DestinationPolicy: plan.DestinationPolicy,
                        ConflictPolicy: plan.ConflictPolicy,
                        Uncertainty: plan.Uncertainty,
                        ManifestSchema: plan.ManifestSchema,
                        ManifestContents: plan.ManifestContents,
                        ManifestSha256: plan.ManifestSha256,
                        SidecarPolicy: plan.SidecarPolicy,
                        Compression: plan.CompressionPolicy,
                        MetadataPolicy: plan.MetadataPolicy),
                    CliJsonSerializerContextFactory.Create(jsonOptions).DbCheckpointJsonResult));
            }
            else
            {
                Console.WriteLine("Created database checkpoint.");
                Console.WriteLine($"  database  : {fullDbPath}");
                WriteCheckpointPlan(plan);
                Console.WriteLine($"  final files: {ConsoleUi.Counted(result.Files.Count, "file")}{(result.FilesTruncated ? " (truncated)" : string.Empty)}");
                Console.WriteLine($"  final bytes: {plan.EstimatedOutputBytes:N0}");
            }

            foreach (var diagnostic in result.Diagnostics)
                CommandErrorWriter.WriteStderr($"Warning [{diagnostic.Code}]: {diagnostic.Message}");

            return CommandExitCodes.Success;
        }
        catch (DbCheckpointPlanDriftException)
        {
            return WriteCommandError(
                options.Json,
                jsonOptions,
                "checkpoint plan drift detected; no checkpoint was published",
                CommandExitCodes.DatabaseError,
                "Stop database writers and retry `cdidx db checkpoint`; execution creates a fresh plan and refuses changes before publish.",
                CommandErrorCodes.DbError,
                category: "checkpoint_plan_drift");
        }
        catch (Exception ex)
        {
            var isInputError = ex is ArgumentException;
            var safeMessage = isInputError
                ? CommandErrorWriter.FormatSanitizedExceptionMessage(ex)
                : $"failed to create database checkpoint: {CommandErrorWriter.FormatSanitizedException(ex)}";
            return WriteCommandError(
                options.Json,
                jsonOptions,
                safeMessage,
                isInputError ? CommandExitCodes.UsageError : CommandExitCodes.DatabaseError,
                isInputError
                    ? $"Use a non-blank single file name of at most {MaxCheckpointNameLength} characters; do not use `.` or `..`, directory separators, or characters invalid in file names on this operating system."
                    : "Ensure the database and checkpoint directory are writable, then retry `cdidx db checkpoint`.",
                isInputError ? CommandErrorCodes.UsageError : CommandErrorCodes.DbError,
                category: isInputError ? null : DiagnosticRedactor.ClassifyException(ex));
        }
    }

    private static void WriteCheckpointPlan(DbCheckpointPlan plan)
    {
        Console.WriteLine($"  name      : {plan.Name}");
        Console.WriteLine($"  checkpoint: {plan.CheckpointPath}");
        Console.WriteLine($"  ready     : {(plan.Ready ? "yes" : "no")}");
        Console.WriteLine($"  destination exists: {(plan.DestinationExists ? "yes" : "no")}");
        Console.WriteLine($"  destination policy: {FormatCheckpointPolicy(plan.DestinationPolicy)}");
        Console.WriteLine($"  conflict policy: {FormatCheckpointPolicy(plan.ConflictPolicy)}");
        Console.WriteLine($"  source files: {ConsoleUi.Counted(plan.SourceFiles.Count, "file")}{(plan.SourceFilesTruncated ? " (truncated)" : string.Empty)}");
        foreach (var source in plan.SourceFiles)
            Console.WriteLine($"    {source.OutputName} ({source.Bytes:N0} bytes)");
        Console.WriteLine($"  source bytes: {plan.SourceBytes:N0}");
        Console.WriteLine($"  planned outputs: {ConsoleUi.Counted(plan.PlannedOutputFiles.Count, "file")}");
        foreach (var output in plan.PlannedOutputFiles)
            Console.WriteLine($"    {output}");
        Console.WriteLine($"  estimated output bytes: {plan.EstimatedOutputBytes:N0}");
        Console.WriteLine($"  manifest schema: {plan.ManifestSchema}");
        Console.WriteLine("  manifest contents:");
        foreach (var line in plan.ManifestContents.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
            Console.WriteLine($"    {line}");
        Console.WriteLine($"  manifest sha256: {plan.ManifestSha256}");
        Console.WriteLine($"  sidecar policy: {FormatCheckpointPolicy(plan.SidecarPolicy)}");
        Console.WriteLine($"  compression: {FormatCheckpointPolicy(plan.CompressionPolicy)}");
        Console.WriteLine($"  metadata policy: {FormatCheckpointPolicy(plan.MetadataPolicy)}");
        Console.WriteLine($"  uncertainty: {FormatCheckpointPolicy(plan.Uncertainty)}");
    }

    private static string FormatCheckpointPolicy(string value)
        => value.Replace('_', ' ').Replace(";", "; ", StringComparison.Ordinal);

    private static int RunCheckpoints(DbCommandOptions options, JsonSerializerOptions jsonOptions)
    {
        var actionCount = (options.CheckpointsList ? 1 : 0)
            + (options.CheckpointsDelete ? 1 : 0)
            + (options.CheckpointsPrune ? 1 : 0);
        if (actionCount == 0)
            return WriteCommandError(
                options.Json,
                jsonOptions,
                "checkpoints requires --list, --delete <name>, or --prune --keep <n>",
                CommandExitCodes.UsageError,
                "Use `cdidx db checkpoints --list`, `cdidx db checkpoints --delete <name> [--dry-run]`, or `cdidx db checkpoints --prune --keep <n> [--dry-run]`.",
                CommandErrorCodes.UsageError);
        if (actionCount > 1)
            return WriteCommandError(
                options.Json,
                jsonOptions,
                "checkpoints accepts exactly one of --list, --delete, or --prune",
                CommandExitCodes.UsageError,
                "Choose one checkpoint maintenance action.",
                CommandErrorCodes.UsageError);

        if (!TryResolveFileDb(options.DbPath, out var fullDbPath, out var error))
            return WriteCommandError(options.Json, jsonOptions, error, CommandExitCodes.DatabaseError, "Use a filesystem database path, not a SQLite URI.", CommandErrorCodes.DbError);

        if (options.CheckpointsDelete)
            return RunDeleteCheckpoint(options, jsonOptions, fullDbPath);
        if (options.CheckpointsPrune)
            return RunPruneCheckpoints(options, jsonOptions, fullDbPath);

        var result = ListCheckpoints(fullDbPath, CheckpointListEntryLimit);
        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new DbCheckpointListJsonResult(
                    fullDbPath,
                    result.Entries,
                    result.Truncated,
                    CheckpointListEntryLimit,
                    CheckpointFileInspectLimit,
                    result.Diagnostics),
                CliJsonSerializerContextFactory.Create(jsonOptions).DbCheckpointListJsonResult));
        }
        else
        {
            Console.WriteLine("Database checkpoints");
            Console.WriteLine($"  database: {fullDbPath}");
            if (result.Truncated)
                Console.WriteLine($"  truncated: yes (checkpoint directory limit {CheckpointListEntryLimit:N0}, file limit {CheckpointFileInspectLimit:N0} per checkpoint)");
            if (result.Entries.Count == 0)
            {
                Console.WriteLine("  checkpoints: none");
            }
            else
            {
                foreach (var entry in result.Entries)
                    Console.WriteLine($"  {entry.Name}  {entry.CreatedAtUtc}  {entry.Bytes:N0} bytes{(entry.FilesTruncated ? " (files truncated)" : string.Empty)}");
            }

            foreach (var diagnostic in result.Diagnostics)
                CommandErrorWriter.WriteStderr($"Warning [{diagnostic.Code}]: {diagnostic.Message}");
        }

        return CommandExitCodes.Success;
    }

    private static int RunDeleteCheckpoint(
        DbCommandOptions options,
        JsonSerializerOptions jsonOptions,
        string fullDbPath)
    {
        if (string.IsNullOrWhiteSpace(options.Name))
            return WriteCommandError(
                options.Json,
                jsonOptions,
                "checkpoint deletion requires a checkpoint name",
                CommandExitCodes.UsageError,
                "Use `cdidx db checkpoints --delete <name> [--dry-run]`.",
                CommandErrorCodes.UsageError);

        string checkpointPath;
        try
        {
            checkpointPath = GetCheckpointPath(fullDbPath, options.Name);
        }
        catch (ArgumentException ex)
        {
            return WriteCommandError(
                options.Json,
                jsonOptions,
                CommandErrorWriter.FormatSanitizedExceptionMessage(ex),
                CommandExitCodes.UsageError,
                $"Use a non-blank single file name of at most {MaxCheckpointNameLength} characters.",
                CommandErrorCodes.UsageError);
        }

        if (!Directory.Exists(LongPath.EnsureWindowsPrefix(checkpointPath)))
            return WriteCommandError(
                options.Json,
                jsonOptions,
                $"checkpoint not found: {FormatCheckpointNameForDiagnostic(options.Name)}",
                CommandExitCodes.NotFound,
                "Run `cdidx db checkpoints --list` to see available checkpoints.",
                CommandErrorCodes.CheckpointNotFound);

        var diagnostics = new List<DbDiagnosticJsonResult>();
        var deletedPaths = new List<string>();
        var retainedPaths = new List<string>();
        if (options.CheckpointsDryRun)
        {
            if (TryValidateCheckpointDirectoryTarget(fullDbPath, checkpointPath, out _, out var validationFailure))
                deletedPaths.Add(checkpointPath);
            else
            {
                diagnostics.Add(new DbDiagnosticJsonResult(
                    "checkpoint_delete_skipped",
                    $"Checkpoint deletion would be skipped: {validationFailure}.",
                    ConsoleUi.FormatBoundedValue(checkpointPath)));
                retainedPaths.Add(checkpointPath);
            }
        }
        else if (TryDeleteCheckpointDirectory(fullDbPath, checkpointPath, diagnostics))
        {
            deletedPaths.Add(checkpointPath);
        }
        else
        {
            retainedPaths.Add(checkpointPath);
        }

        var failed = retainedPaths.Count > 0;
        var result = new DbCheckpointCleanupResult(
            deletedPaths.Count,
            retainedPaths.Count,
            deletedPaths,
            retainedPaths,
            Truncated: false,
            diagnostics);
        WriteCheckpointCleanupResult(
            options,
            jsonOptions,
            fullDbPath,
            "delete",
            options.Name,
            keep: null,
            result,
            status: failed ? "error" : options.CheckpointsDryRun ? "dry_run" : "success");
        return failed ? CommandExitCodes.DatabaseError : CommandExitCodes.Success;
    }

    private static int RunPruneCheckpoints(
        DbCommandOptions options,
        JsonSerializerOptions jsonOptions,
        string fullDbPath)
    {
        var listed = ListCheckpoints(fullDbPath, CheckpointPruneScanLimit);
        var diagnostics = listed.Diagnostics;
        if (listed.DirectoryEnumerationTruncated)
        {
            diagnostics.Add(new DbDiagnosticJsonResult(
                "checkpoint_prune_truncated",
                "Checkpoint pruning was skipped because checkpoint enumeration reached the scan limit.",
                ConsoleUi.FormatBoundedValue(GetCheckpointRoot(fullDbPath))));
            var skipped = new DbCheckpointCleanupResult(
                Deleted: 0,
                Retained: listed.Entries.Count,
                DeletedPaths: [],
                RetainedPaths: listed.Entries.Select(entry => entry.CheckpointPath).ToList(),
                Truncated: true,
                diagnostics);
            WriteCheckpointCleanupResult(
                options,
                jsonOptions,
                fullDbPath,
                "prune",
                name: null,
                options.CheckpointsKeep,
                skipped,
                status: options.CheckpointsDryRun ? "dry_run" : "success");
            return CommandExitCodes.Success;
        }

        var retainableEntries = listed.Entries
            .Select(entry => new
            {
                Entry = entry,
                Retainable = TryGetCheckpointRetentionTimestamp(
                    fullDbPath,
                    entry.Name,
                    entry.CheckpointPath,
                    diagnostics,
                    out var createdAtUtc),
                CreatedAtUtc = createdAtUtc,
            })
            .Where(candidate => candidate.Retainable)
            .OrderByDescending(candidate => candidate.CreatedAtUtc)
            .ThenBy(candidate => candidate.Entry.Name, StringComparer.Ordinal)
            .Select(candidate => candidate.Entry)
            .ToList();
        var retainedPaths = retainableEntries
            .Take(options.CheckpointsKeep)
            .Select(entry => entry.CheckpointPath)
            .ToList();
        var retainedPathSet = retainedPaths.ToHashSet(StringComparer.Ordinal);
        var candidatePaths = listed.Entries
            .Where(entry => !retainedPathSet.Contains(entry.CheckpointPath))
            .Select(entry => entry.CheckpointPath)
            .ToList();
        var deletedPaths = new List<string>();
        if (options.CheckpointsDryRun)
        {
            foreach (var path in candidatePaths)
            {
                if (TryValidateCheckpointDirectoryTarget(fullDbPath, path, out _, out var validationFailure))
                    deletedPaths.Add(path);
                else
                {
                    diagnostics.Add(new DbDiagnosticJsonResult(
                        "checkpoint_delete_skipped",
                        $"Checkpoint deletion would be skipped: {validationFailure}.",
                        ConsoleUi.FormatBoundedValue(path)));
                    retainedPaths.Add(path);
                }
            }
        }
        else
        {
            foreach (var path in candidatePaths)
            {
                if (TryDeleteCheckpointDirectory(fullDbPath, path, diagnostics))
                    deletedPaths.Add(path);
                else
                    retainedPaths.Add(path);
            }
        }

        var result = new DbCheckpointCleanupResult(
            deletedPaths.Count,
            retainedPaths.Count,
            deletedPaths,
            retainedPaths,
            listed.Truncated,
            diagnostics);
        WriteCheckpointCleanupResult(
            options,
            jsonOptions,
            fullDbPath,
            "prune",
            name: null,
            options.CheckpointsKeep,
            result,
            status: options.CheckpointsDryRun ? "dry_run" : "success");
        return CommandExitCodes.Success;
    }

    private static void WriteCheckpointCleanupResult(
        DbCommandOptions options,
        JsonSerializerOptions jsonOptions,
        string fullDbPath,
        string operation,
        string? name,
        int? keep,
        DbCheckpointCleanupResult result,
        string status)
    {
        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new DbCheckpointCleanupJsonResult(
                    status,
                    fullDbPath,
                    operation,
                    name,
                    keep,
                    options.CheckpointsDryRun,
                    result.Deleted,
                    result.Retained,
                    result.DeletedPaths,
                    result.RetainedPaths,
                    result.Truncated,
                    CheckpointPruneScanLimit,
                    result.Diagnostics),
                CliJsonSerializerContextFactory.Create(jsonOptions).DbCheckpointCleanupJsonResult));
            return;
        }

        Console.WriteLine(options.CheckpointsDryRun
            ? "Database checkpoint cleanup dry run."
            : "Database checkpoint cleanup complete.");
        Console.WriteLine($"  database : {fullDbPath}");
        Console.WriteLine($"  operation: {operation}");
        if (name is not null)
            Console.WriteLine($"  name     : {name}");
        if (keep is not null)
            Console.WriteLine($"  keep     : {keep.Value:N0}");
        Console.WriteLine($"  side effect: {(options.CheckpointsDryRun ? "none" : "requested checkpoint directories removed")}");
        Console.WriteLine($"  {(options.CheckpointsDryRun ? "would delete" : "deleted"),-12}: {result.Deleted:N0}");
        foreach (var path in result.DeletedPaths)
            Console.WriteLine($"    {path}");
        Console.WriteLine($"  retained : {result.Retained:N0}");
        foreach (var path in result.RetainedPaths)
            Console.WriteLine($"    {path}");
        if (result.Truncated)
            Console.WriteLine($"  truncated: yes (checkpoint scan limit {CheckpointPruneScanLimit:N0})");
        foreach (var diagnostic in result.Diagnostics)
            CommandErrorWriter.WriteStderr($"Warning [{diagnostic.Code}]: {diagnostic.Message}");
    }

}
