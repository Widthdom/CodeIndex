using CodeIndex.Cli;

namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    internal static bool TryParseMaxFileSizeBytes(string? value, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        var splitAt = trimmed.Length;
        while (splitAt > 0 && char.IsLetter(trimmed[splitAt - 1]))
            splitAt--;

        var numberPart = trimmed[..splitAt].Trim();
        var suffix = trimmed[splitAt..].Trim().ToLowerInvariant();
        if (!long.TryParse(numberPart, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number) || number <= 0)
            return false;

        long multiplier = suffix switch
        {
            "" or "b" or "byte" or "bytes" => 1,
            "k" or "kb" or "kib" => 1024L,
            "m" or "mb" or "mib" => 1024L * 1024L,
            "g" or "gb" or "gib" => 1024L * 1024L * 1024L,
            _ => 0,
        };
        if (multiplier == 0)
            return false;

        if (number > int.MaxValue / multiplier)
            return false;

        bytes = number * multiplier;
        return true;
    }

    private static long ResolveMaxFileSizeBytes(long? explicitMaxFileSizeBytes)
    {
        if (explicitMaxFileSizeBytes is > 0 and <= int.MaxValue)
            return explicitMaxFileSizeBytes.Value;

        var envValue = CdidxEnvironment.GetEnvironmentVariable(MaxFileSizeEnvironmentVariable);
        return TryParseMaxFileSizeBytes(envValue, out var envBytes)
            ? envBytes
            : DefaultMaxFileSizeBytes;
    }
}
