using System.Globalization;

namespace CodeIndex.Cli;

internal static class EnvironmentOptionParser
{
    internal const string SourceKindDefault = "default";
    internal const string SourceKindEnvironment = "environment";
    internal const string SourceKindConfig = "config";
    internal const string StatusUnset = "unset";
    internal const string StatusParsed = "parsed";
    internal const string StatusInvalid = "invalid";
    internal const string StatusBelowMinimum = "below_minimum";
    internal const string StatusAboveMaximum = "above_maximum";

    internal static EnvironmentOptionParseResult<int> ReadInt32(
        string name,
        int fallback,
        int minimum,
        int maximum)
    {
        if (minimum > maximum)
            throw new ArgumentOutOfRangeException(nameof(minimum), minimum, "Minimum must not exceed maximum.");

        var rawValue = CdidxEnvironment.GetEnvironmentVariable(name);
        var sourceDetail = CdidxEnvironment.GetConfigSource(name);
        var source = ResolveSource(name, rawValue, sourceDetail);

        if (rawValue is null)
            return new EnvironmentOptionParseResult<int>(
                name,
                rawValue,
                fallback,
                fallback,
                minimum,
                maximum,
                source.SourceKind,
                source.Source,
                sourceDetail,
                StatusUnset,
                UsedFallback: true);

        if (int.TryParse(rawValue, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            if (parsed >= minimum && parsed <= maximum)
            {
                return new EnvironmentOptionParseResult<int>(
                    name,
                    rawValue,
                    parsed,
                    fallback,
                    minimum,
                    maximum,
                    source.SourceKind,
                    source.Source,
                    sourceDetail,
                    StatusParsed,
                    UsedFallback: false);
            }

            return new EnvironmentOptionParseResult<int>(
                name,
                rawValue,
                fallback,
                fallback,
                minimum,
                maximum,
                source.SourceKind,
                source.Source,
                sourceDetail,
                parsed < minimum ? StatusBelowMinimum : StatusAboveMaximum,
                UsedFallback: true);
        }

        return new EnvironmentOptionParseResult<int>(
            name,
            rawValue,
            fallback,
            fallback,
            minimum,
            maximum,
            source.SourceKind,
            source.Source,
            sourceDetail,
            StatusInvalid,
            UsedFallback: true);
    }

    internal static EnvironmentOptionParseResult<long> ReadInt64(
        string name,
        long fallback,
        long minimum,
        long maximum)
    {
        if (minimum > maximum)
            throw new ArgumentOutOfRangeException(nameof(minimum), minimum, "Minimum must not exceed maximum.");

        var rawValue = CdidxEnvironment.GetEnvironmentVariable(name);
        var sourceDetail = CdidxEnvironment.GetConfigSource(name);
        var source = ResolveSource(name, rawValue, sourceDetail);

        if (rawValue is null)
            return new EnvironmentOptionParseResult<long>(
                name,
                rawValue,
                fallback,
                fallback,
                minimum,
                maximum,
                source.SourceKind,
                source.Source,
                sourceDetail,
                StatusUnset,
                UsedFallback: true);

        if (long.TryParse(rawValue, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            if (parsed >= minimum && parsed <= maximum)
            {
                return new EnvironmentOptionParseResult<long>(
                    name,
                    rawValue,
                    parsed,
                    fallback,
                    minimum,
                    maximum,
                    source.SourceKind,
                    source.Source,
                    sourceDetail,
                    StatusParsed,
                    UsedFallback: false);
            }

            return new EnvironmentOptionParseResult<long>(
                name,
                rawValue,
                fallback,
                fallback,
                minimum,
                maximum,
                source.SourceKind,
                source.Source,
                sourceDetail,
                parsed < minimum ? StatusBelowMinimum : StatusAboveMaximum,
                UsedFallback: true);
        }

        return new EnvironmentOptionParseResult<long>(
            name,
            rawValue,
            fallback,
            fallback,
            minimum,
            maximum,
            source.SourceKind,
            source.Source,
            sourceDetail,
            StatusInvalid,
            UsedFallback: true);
    }

    private static (string SourceKind, string Source) ResolveSource(
        string name,
        string? rawValue,
        string? sourceDetail)
    {
        if (rawValue is null)
            return (SourceKindDefault, SourceKindDefault);

        return sourceDetail is null
            ? (SourceKindEnvironment, name)
            : (SourceKindConfig, sourceDetail);
    }
}

internal readonly record struct EnvironmentOptionParseResult<T>(
    string Name,
    string? RawValue,
    T Value,
    T Fallback,
    T Minimum,
    T Maximum,
    string SourceKind,
    string Source,
    string? SourceDetail,
    string Status,
    bool UsedFallback);
