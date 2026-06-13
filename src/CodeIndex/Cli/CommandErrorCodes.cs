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

    /// <summary>Filesystem case-sensitivity probing failed before a safe path-casing policy could be selected.</summary>
    public const string FileSystemCaseProbeFailed = "E014_FS_CASE_PROBE_FAILED";
}
