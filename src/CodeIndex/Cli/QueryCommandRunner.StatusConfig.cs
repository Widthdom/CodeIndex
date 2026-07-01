namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    internal const string StaleAfterEnvironmentVariable = "CDIDX_STALE_AFTER";
    internal static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromHours(24);
    internal static readonly TimeSpan MaxStaleAfter = TimeSpan.FromDays(30);
    internal const string MaxStaleAfterDisplay = "30d";
    internal const int MaxStatusSymbolKindEntries = 32;
    internal const int MaxStatusSymbolKindNameLength = 64;
    internal const int MaxStatusCheckScopesCsvLength = 256;
    internal const int MaxStatusCheckScopesCsvEntries = 16;
}
