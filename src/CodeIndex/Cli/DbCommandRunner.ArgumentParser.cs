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
    private sealed class DbArgumentParser
    {
        private string dbPath = Path.Combine(".cdidx", "codeindex.db");
        private bool json;
        private bool showPaths;
        private bool integrityCheck;
        private bool integrityAliasUsed;
        private bool schema;
        private string? explicitSubcommand;
        private bool prune;
        private bool pruneDryRun;
        private bool pruneApply;
        private bool checkpoint;
        private bool listCheckpoints;
        private bool restore;
        private bool restoreBackups;
        private bool checkpointsList;
        private bool checkpointsDelete;
        private bool checkpointsPrune;
        private int checkpointsKeep = DefaultRestoreBackupKeepCount;
        private bool restoreBackupsList;
        private bool restoreBackupsPrune;
        private bool restoreBackupsRestore;
        private bool noBackup;
        private bool keepOptionSeen;
        private int restoreBackupsKeep = DefaultRestoreBackupKeepCount;
        private bool schemaSummaryOnly;
        private int schemaEntryLimit = SchemaEntryLimit;
        private int schemaSqlTextLimit = SchemaSqlTextLimit;
        private bool? schemaIncludeInternal;
        private bool schemaSpecificOptionSeen;
        private string? parsedSchemaType;
        private string? parsedSchemaName;
        private string? name;
        private string? parseError;

        internal DbCommandOptions Parse(string[] args)
        {
            for (var i = 0; i < args.Length; i++)
            {
                var immediateResult = ParseArgument(args, ref i);
                if (immediateResult != null)
                    return immediateResult;
                if (parseError != null)
                    break;
            }

            ValidateOptionCombinations();
            return BuildOptions();
        }

        private DbCommandOptions? ParseArgument(string[] args, ref int i)
        {
            switch (args[i])
            {
                case "--db" when i + 1 < args.Length:
                    dbPath = args[++i];
                    break;
                case "--db":
                    parseError = "--db requires a value";
                    break;
                case var argument when argument.StartsWith("--db=", StringComparison.Ordinal):
                    dbPath = argument["--db=".Length..];
                    if (string.IsNullOrWhiteSpace(dbPath))
                        parseError = "--db requires a value";
                    break;
                case "--json":
                    json = true;
                    break;
                case "--show-paths":
                    showPaths = true;
                    break;
                case "--integrity-check":
                    integrityCheck = true;
                    integrityAliasUsed = true;
                    break;
                case "integrity":
                    integrityCheck = true;
                    explicitSubcommand = "integrity";
                    break;
                case "schema":
                    schema = true;
                    explicitSubcommand = "schema";
                    break;
                case "--type" when i + 1 < args.Length:
                    schemaSpecificOptionSeen = true;
                    var schemaType = args[++i].Trim().ToLowerInvariant();
                    if (!SchemaObjectTypes.Contains(schemaType, StringComparer.Ordinal))
                        parseError = "--type must be one of table, index, trigger, or view";
                    else
                        parsedSchemaType = schemaType;
                    break;
                case "--type":
                    parseError = "--type requires a value";
                    break;
                case "--name" when i + 1 < args.Length:
                    schemaSpecificOptionSeen = true;
                    parsedSchemaName = args[++i];
                    break;
                case "--name":
                    parseError = "--name requires a value";
                    break;
                case "--summary-only":
                    schemaSpecificOptionSeen = true;
                    schemaSummaryOnly = true;
                    break;
                case "--limit" when i + 1 < args.Length:
                    schemaSpecificOptionSeen = true;
                    if (!int.TryParse(args[++i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out schemaEntryLimit)
                        || schemaEntryLimit < 0
                        || schemaEntryLimit > SchemaEntryLimit)
                    {
                        parseError = $"--limit must be an integer from 0 to {SchemaEntryLimit}";
                    }
                    break;
                case "--limit":
                    parseError = "--limit requires a value";
                    break;
                case "--max-sql-chars" when i + 1 < args.Length:
                    schemaSpecificOptionSeen = true;
                    if (!int.TryParse(args[++i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out schemaSqlTextLimit)
                        || schemaSqlTextLimit < 0
                        || schemaSqlTextLimit > SchemaSqlTextLimit)
                    {
                        parseError = $"--max-sql-chars must be an integer from 0 to {SchemaSqlTextLimit}";
                    }
                    break;
                case "--max-sql-chars":
                    parseError = "--max-sql-chars requires a value";
                    break;
                case "--include-internal":
                    schemaSpecificOptionSeen = true;
                    if (schemaIncludeInternal == false)
                        parseError = "--include-internal and --exclude-internal cannot be combined";
                    else
                        schemaIncludeInternal = true;
                    break;
                case "--exclude-internal":
                    schemaSpecificOptionSeen = true;
                    if (schemaIncludeInternal == true)
                        parseError = "--include-internal and --exclude-internal cannot be combined";
                    else
                        schemaIncludeInternal = false;
                    break;
                case "prune":
                    prune = true;
                    break;
                case "checkpoint":
                    checkpoint = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                        name = args[++i];
                    break;
                case "checkpoints":
                    listCheckpoints = true;
                    break;
                case "restore":
                    restore = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                        name = args[++i];
                    else
                        parseError = "restore requires a checkpoint name";
                    break;
                case "restore-backups":
                    restoreBackups = true;
                    break;
                case "--dry-run":
                    pruneDryRun = true;
                    break;
                case "--no-backup":
                    noBackup = true;
                    break;
                case "--restore" when i + 1 < args.Length
                    && !args[i + 1].StartsWith("-", StringComparison.Ordinal):
                    if (!restoreBackups)
                    {
                        parseError = "--restore is only valid with `cdidx db restore-backups --restore <id>`";
                        break;
                    }

                    restoreBackupsRestore = true;
                    name = args[++i];
                    break;
                case "--restore":
                    parseError = "--restore requires a managed restore backup ID";
                    break;
                case "--apply":
                    pruneApply = true;
                    break;
                case "--prune":
                    if (restoreBackups)
                        restoreBackupsPrune = true;
                    else if (listCheckpoints)
                        checkpointsPrune = true;
                    else
                        parseError = "--prune is only valid with `cdidx db checkpoints --prune` or `cdidx db restore-backups --prune`";
                    break;
                case "--delete" when i + 1 < args.Length
                    && !args[i + 1].StartsWith("-", StringComparison.Ordinal):
                    if (!listCheckpoints)
                    {
                        parseError = "--delete is only valid with `cdidx db checkpoints --delete <name>`";
                        break;
                    }

                    checkpointsDelete = true;
                    name = args[++i];
                    break;
                case "--delete":
                    parseError = "--delete requires a checkpoint name";
                    break;
                case "--keep" when i + 1 < args.Length:
                    keepOptionSeen = true;
                    if (!restoreBackups && !checkpointsPrune)
                    {
                        parseError = "--keep is only valid with checkpoint or restore-backup pruning";
                        break;
                    }

                    if (!int.TryParse(args[++i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsedKeep)
                        || parsedKeep < 0
                        || parsedKeep > MaxRestoreBackupKeepCount)
                    {
                        parseError = $"--keep must be an integer from 0 to {MaxRestoreBackupKeepCount}";
                    }
                    else if (restoreBackups)
                    {
                        restoreBackupsKeep = parsedKeep;
                    }
                    else
                    {
                        checkpointsKeep = parsedKeep;
                    }
                    break;
                case "--keep":
                    parseError = "--keep requires a value";
                    break;
                case "--list":
                    if (listCheckpoints)
                    {
                        checkpointsList = true;
                        break;
                    }
                    if (restoreBackups)
                    {
                        restoreBackupsList = true;
                        break;
                    }

                    parseError = "--list is only valid with `cdidx db checkpoints --list`";
                    break;
                case "--help" or "-h":
                    return new DbCommandOptions { ShowHelp = true, DbPath = dbPath, Json = json, ShowPaths = showPaths };
                default:
                    if (args[i].StartsWith('-'))
                        parseError = $"db does not support option: '{args[i]}'";
                    else
                        parseError = $"unknown db command or argument: '{args[i]}'";
                    break;
            }

            return null;
        }

        private void ValidateOptionCombinations()
        {
            if (parseError is null && restoreBackups && pruneApply)
                parseError = "--apply is not supported with `cdidx db restore-backups`; `--prune` is the explicit mutation opt-in.";
            if (parseError is null && pruneDryRun && restoreBackups && !restoreBackupsPrune && !restoreBackupsRestore)
                parseError = "--dry-run is only valid with `cdidx db restore-backups --prune` or `--restore <id>`.";
            if (parseError is null && keepOptionSeen && restoreBackups && !restoreBackupsPrune)
                parseError = "--keep is only valid with `cdidx db restore-backups --prune`.";
            if (parseError is null && pruneDryRun && listCheckpoints && !checkpointsDelete && !checkpointsPrune)
                parseError = "--dry-run is only valid with checkpoint deletion or pruning.";
            if (parseError is null && !schema && schemaSpecificOptionSeen)
                parseError = "--type, --name, --summary-only, --limit, --max-sql-chars, --include-internal, and --exclude-internal are only valid with `cdidx db schema`.";
            if (parseError is null && pruneDryRun && !prune && !checkpoint && !restore && !restoreBackups && !listCheckpoints)
                parseError = "--dry-run is only valid with a supported preview operation.";
            if (parseError is null && pruneApply && !prune)
                parseError = "--apply is only valid with `cdidx db prune --apply`.";
            if (parseError is null && noBackup && !restore && !(restoreBackups && restoreBackupsRestore))
                parseError = "--no-backup is only valid with `cdidx db restore <name>` or `cdidx db restore-backups --restore <id>`.";
            if (parseError is null
                && showPaths
                && (schema || prune || checkpoint || listCheckpoints || restore || restoreBackups))
            {
                parseError = "--show-paths is only valid with `cdidx db integrity` or `cdidx db --integrity-check`.";
            }
        }

        private DbCommandOptions BuildOptions()
        {
            return new DbCommandOptions
            {
                DbPath = dbPath,
                Json = json,
                ShowPaths = showPaths,
                IntegrityCheck = integrityCheck,
                IntegrityAliasUsed = integrityAliasUsed,
                Schema = schema,
                ExplicitSubcommand = explicitSubcommand,
                Prune = prune,
                PruneDryRun = pruneDryRun,
                PruneApply = pruneApply,
                Checkpoint = checkpoint,
                ListCheckpoints = listCheckpoints,
                CheckpointsList = checkpointsList,
                CheckpointsDelete = checkpointsDelete,
                CheckpointsPrune = checkpointsPrune,
                CheckpointsKeep = checkpointsKeep,
                CheckpointsDryRun = listCheckpoints && pruneDryRun,
                Restore = restore,
                RestoreDryRun = restore && pruneDryRun,
                RestoreBackups = restoreBackups,
                RestoreBackupsList = restoreBackupsList,
                RestoreBackupsPrune = restoreBackupsPrune,
                RestoreBackupsRestore = restoreBackupsRestore,
                RestoreBackupsKeep = restoreBackupsKeep,
                RestoreBackupsDryRun = restoreBackups && pruneDryRun,
                NoBackup = noBackup,
                SchemaSummaryOnly = schemaSummaryOnly,
                SchemaEntryLimit = schemaEntryLimit,
                SchemaSqlTextLimit = schemaSqlTextLimit,
                SchemaIncludeInternal = schemaIncludeInternal ?? true,
                SchemaType = parsedSchemaType,
                SchemaName = parsedSchemaName,
                CheckpointDryRun = checkpoint && pruneDryRun,
                Name = name,
                ParseError = parseError,
            };
        }
    }
}
