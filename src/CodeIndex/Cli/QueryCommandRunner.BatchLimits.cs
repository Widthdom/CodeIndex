namespace CodeIndex.Cli;

public static partial class QueryCommandRunner
{
    internal const int BatchMaxLineChars = 1024 * 1024;
    internal const int BatchMaxLineUtf8Bytes = BatchMaxLineChars * 4;
    internal const int BatchMaxArgumentCount = 256;
    internal const int BatchMaxArgumentChars = 8192;
    internal const int BatchMaxJsonDepth = 32;
    internal const int BatchMaxInputLines = 1024;
    internal const int BatchMaxTotalOutputChars = JsonEnvelopeWrapper.MaxCapturedOutputChars;
    internal const int BatchTerminalOutputReserveChars = 4096;
}
