namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    internal const int BatchMaxLineChars = 1024 * 1024;
    internal const int BatchMaxLineUtf8Bytes = BatchMaxLineChars * 4;
    internal const int BatchMaxArgumentCount = 256;
    internal const int BatchMaxArgumentChars = 8192;
    internal const int BatchMaxJsonDepth = 32;
    internal const int BatchDefaultInputLines = 1024;
    internal const int BatchMaxInputLines = 64 * 1024;
    internal const int BatchDefaultTotalOutputChars = JsonEnvelopeWrapper.MaxCapturedOutputChars;
    internal const int BatchMinTotalOutputChars = 4096;
    internal const int BatchMaxTotalOutputChars = 64 * 1024 * 1024;
    internal const int BatchMaxParallelism = 16;
    internal const int BatchTerminalOutputReserveChars = 4096;
}
