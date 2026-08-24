namespace CodeIndex.Models;

internal static class StatusMetadataLimits
{
    internal const int MaxRawUtf8Bytes = 512 * 1024;
    internal const int MaxJsonDepth = 16;
    internal const int MaxFileErrors = 50;
    internal const int MaxReferenceCapHitFiles = 50;
    internal const int MaxReferenceReasons = 16;
    internal const int MaxPathCharacters = 32 * 1024;
    internal const int MaxCodeCharacters = 128;
    internal const int MaxDetailCharacters = 4 * 1024;
    internal const int MaxDecodedStringCharacters = 256 * 1024;
}
