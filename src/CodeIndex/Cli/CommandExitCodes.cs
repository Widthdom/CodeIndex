namespace CodeIndex.Cli;

/// <summary>
/// Shared process exit codes for CLI commands.
/// CLIコマンド用の共通終了コード。
/// </summary>
public static class CommandExitCodes
{
    public const int Success = 0;
    public const int UsageError = 1;
    public const int NotFound = 2;
    public const int DatabaseError = 3;
    public const int FeatureUnavailable = 4;
    public const int StaleIndex = 5;
    public const int TransientDatabaseError = 6;
    public const int InvalidArgument = 7;
    public const int CancelledBySignal = 8;
    public const int Interrupted = CancelledBySignal;
    public const int LegacyInterrupted = 130;
}
