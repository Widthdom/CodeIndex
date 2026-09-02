namespace CodeIndex.Cli;

/// <summary>
/// Stable machine-readable error-code taxonomy emitted alongside CLI / MCP error messages.
/// Codes are appended to human-readable output as `Error [Exxx]: ...` and surfaced in
/// `--json` envelopes as `error_code`. Once published, codes must not be renamed or reused;
/// retire by leaving the constant in place and stopping new emissions.
/// CLI / MCP のエラー出力に付ける安定した機械可読エラーコードの分類。
/// 一度公開したコードは renaming / 使い回しをせず、新規 emission を止めるだけにする。
/// </summary>
internal static class CommandErrorCodes
{
    /// <summary>Database file or directory does not exist on disk (or `--db` URI cannot be opened).</summary>
    public const string DbNotFound = "E001_DB_NOT_FOUND";

    /// <summary>SQLite reported BUSY/LOCKED, or `cdidx index` could not acquire the per-database file lock.</summary>
    public const string DbLocked = "E002_DB_LOCKED";

    /// <summary>
    /// Reserved for hard read failures caused by an index written by a newer cdidx than the
    /// reader can interpret. Today the same condition is surfaced softly via
    /// `status --json` (`index_newer_than_reader: true`); future binaries that hit a hard
    /// open-time failure on an unknown schema stamp must emit this code.
    /// </summary>
    public const string SchemaTooNew = "E003_SCHEMA_TOO_NEW";

    /// <summary>`--db` points at a read-only target but the command requires write access.</summary>
    public const string DbNotWritable = "E004_DB_NOT_WRITABLE";

    /// <summary>`PRAGMA integrity_check` returned diagnostic rows instead of `ok`.</summary>
    public const string DbIntegrityFailed = "E005_DB_INTEGRITY_FAILED";

    /// <summary>`--fts` raw FTS5 query string failed to parse.</summary>
    public const string FtsQuerySyntax = "E006_FTS_QUERY_SYNTAX";

    /// <summary>SQLite reported SQLITE_FULL (13) — typically temp-store exhausted while planning a heavy query.</summary>
    public const string TempStoreExhausted = "E007_TEMP_STORE_EXHAUSTED";

    /// <summary>Generic database error fallback (used when no more specific code applies).</summary>
    public const string DbError = "E008_DB_ERROR";

    /// <summary>Requested feature is unavailable in this build (e.g. `--json` on a trimmed/AOT build).</summary>
    public const string FeatureUnavailable = "E009_FEATURE_UNAVAILABLE";

    /// <summary>Argument parse error, conflicting flags, or unknown subcommand.</summary>
    public const string UsageError = "E010_USAGE_ERROR";

    /// <summary>Project / target directory does not exist on disk.</summary>
    public const string DirectoryNotFound = "E011_DIRECTORY_NOT_FOUND";

    /// <summary>The user interrupted the command with Ctrl-C / SIGINT.</summary>
    public const string Interrupted = "E012_INTERRUPTED";

    /// <summary>Index extraction made no forward progress within the bounded stall timeout.</summary>
    public const string IndexExtractionStalled = "E013_INDEX_EXTRACTION_STALLED";

    /// <summary>A user-supplied regular expression exceeded the bounded match timeout while executing.</summary>
    public const string RegexMatchTimeout = "E014_REGEX_MATCH_TIMEOUT";

    /// <summary>Filesystem case-sensitivity probing failed before a safe path-casing policy could be selected.</summary>
    public const string FileSystemCaseProbeFailed = "E015_FS_CASE_PROBE_FAILED";

    /// <summary>Requested database checkpoint name does not exist.</summary>
    public const string CheckpointNotFound = "E016_CHECKPOINT_NOT_FOUND";

    /// <summary>Workspace manifest JSON was found but failed schema or safety validation.</summary>
    public const string WorkspaceManifestInvalid = "E017_WORKSPACE_MANIFEST_INVALID";

    /// <summary>A query that requires at least one result did not match an indexed entity.</summary>
    public const string QueryNotFound = "E018_QUERY_NOT_FOUND";

    /// <summary>The requested indexed file path does not exist in the active index.</summary>
    public const string FileNotFound = "E019_FILE_NOT_FOUND";

    /// <summary>A requested source line falls outside the indexed file's 1-based line range.</summary>
    public const string LineOutOfRange = "E020_LINE_OUT_OF_RANGE";

    /// <summary>Suggestion sidecar storage could not be resolved, created, read, or written safely.</summary>
    public const string SuggestionStoreUnavailable = "E021_SUGGESTION_STORE_UNAVAILABLE";

    /// <summary>Indexing committed a usable but incomplete generation because one or more files failed.</summary>
    public const string IndexPartial = "E022_INDEX_PARTIAL";

    /// <summary>A command failed without a more specific published error code.</summary>
    public const string CommandFailed = "E023_COMMAND_FAILED";

    /// <summary>A discovered cdidx configuration file failed validation.</summary>
    public const string ConfigInvalid = "E024_CONFIG_INVALID";

    /// <summary>A Git hook operation failed because its platform or filesystem contract was not satisfied.</summary>
    public const string HookOperationFailed = "E025_HOOK_OPERATION_FAILED";

    /// <summary>The requested Git hook operation did not target a Git repository.</summary>
    public const string NotGitRepository = "E026_NOT_GIT_REPOSITORY";

    /// <summary>SQLite rejected the file as not being a database, or CodeIndex validation rejected its format.</summary>
    public const string DbNotDatabase = "E027_DB_NOT_DATABASE";

    /// <summary>A requested JSON response byte budget cannot fit the minimum complete payload or envelope.</summary>
    public const string ResponseBudgetTooSmall = "E028_RESPONSE_BUDGET_TOO_SMALL";

    /// <summary>A query matched multiple entities and requires explicit narrowing or an all-results option.</summary>
    public const string QueryAmbiguous = "E029_QUERY_AMBIGUOUS";
}
