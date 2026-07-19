using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    private const int AmbiguousLanguageProbeByteLimit = 64 * 1024;
    private const int AmbiguousProjectMarkerEntryLimit = 256;

    private static readonly Regex ObjectiveCContentMarker = new(
        @"^\s*(?:#\s*import\b|@(?:interface|implementation|protocol)\b)",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex MatlabContentMarker = new(
        @"^\s*(?:function\b|classdef\b)",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex PerlContentMarker = new(
        @"^\s*(?:use\s+(?:strict|warnings)\s*;|package\s+[A-Za-z_]\w*(?:::\w+)*\s*;|(?:my|our|state)\s+[$@%][A-Za-z_]\w*|sub\s+[A-Za-z_]\w*)",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex PrologContentMarker = new(
        @"^\s*(?::-\s*(?:module|use_module|dynamic|multifile|discontiguous|initialization)\b|\?-\s*|[a-z][A-Za-z0-9_]*(?:\s*\([^\r\n.]*\))?\s*(?::-|-->))",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static LanguageDetectionResult TryDetectAmbiguousExtensionLanguage(
        string filePath,
        string extension,
        string? content,
        string? projectRoot,
        FileProbeStatus? knownIndexability,
        Func<string, FileStream>? openReadForIndexContent,
        Func<string, IEnumerable<string>>? enumerateFileSystemEntries)
    {
        var ambiguityBucket = string.Equals(extension, ".m", StringComparison.OrdinalIgnoreCase)
            ? "ambiguous_m"
            : "ambiguous_pl";

        if (content == null)
        {
            var readResult = TryReadAmbiguousLanguagePrefix(
                filePath,
                knownIndexability,
                openReadForIndexContent,
                out content);
            if (readResult != FileProbeStatus.Supported)
            {
                if (!knownIndexability.HasValue && readResult == FileProbeStatus.Missing)
                    return new LanguageDetectionResult(FileProbeStatus.Supported, ambiguityBucket, "ambiguous");
                return new LanguageDetectionResult(readResult, null);
            }
        }

        var firstFamily = string.Equals(extension, ".m", StringComparison.OrdinalIgnoreCase)
            ? "objc"
            : "perl";
        var secondFamily = string.Equals(extension, ".m", StringComparison.OrdinalIgnoreCase)
            ? "matlab"
            : "prolog";
        var firstContentMarker = string.Equals(extension, ".m", StringComparison.OrdinalIgnoreCase)
            ? ObjectiveCContentMarker.IsMatch(content)
            : PerlContentMarker.IsMatch(content);
        var secondContentMarker = string.Equals(extension, ".m", StringComparison.OrdinalIgnoreCase)
            ? MatlabContentMarker.IsMatch(content)
            : PrologContentMarker.IsMatch(content);

        if (firstContentMarker != secondContentMarker)
        {
            return new LanguageDetectionResult(
                FileProbeStatus.Supported,
                firstContentMarker ? firstFamily : secondFamily,
                "content");
        }

        if (firstContentMarker)
            return new LanguageDetectionResult(FileProbeStatus.Supported, ambiguityBucket, "ambiguous");

        var (firstProjectMarker, secondProjectMarker) = ProbeAmbiguousLanguageProjectMarkers(
            filePath,
            projectRoot,
            string.Equals(extension, ".m", StringComparison.OrdinalIgnoreCase),
            enumerateFileSystemEntries);
        if (firstProjectMarker != secondProjectMarker)
        {
            return new LanguageDetectionResult(
                FileProbeStatus.Supported,
                firstProjectMarker ? firstFamily : secondFamily,
                "project");
        }

        return new LanguageDetectionResult(FileProbeStatus.Supported, ambiguityBucket, "ambiguous");
    }

    private static FileProbeStatus TryReadAmbiguousLanguagePrefix(
        string filePath,
        FileProbeStatus? knownIndexability,
        Func<string, FileStream>? openReadForIndexContent,
        out string content)
    {
        content = string.Empty;
        var indexability = knownIndexability ?? GetFileIndexability(filePath, SymlinkPolicy.None, projectRoot: null);
        if (indexability != FileProbeStatus.Supported)
            return indexability;

        try
        {
            using var stream = openReadForIndexContent?.Invoke(filePath)
                ?? BoundedFile.OpenReadForPrefixProbe(filePath);
            var buffer = new byte[AmbiguousLanguageProbeByteLimit];
            var bytesRead = 0;
            while (bytesRead < buffer.Length)
            {
                var read = stream.Read(buffer, bytesRead, buffer.Length - bytesRead);
                if (read == 0)
                    break;
                bytesRead += read;
            }

            var bytes = buffer.AsSpan(0, bytesRead);
            if (bytes.StartsWith(Encoding.Unicode.GetPreamble()))
                content = DecodeBoundedPrefix(StrictShebangUtf16LittleEndianEncoding, bytes[2..]);
            else if (bytes.StartsWith(Encoding.BigEndianUnicode.GetPreamble()))
                content = DecodeBoundedPrefix(StrictShebangUtf16BigEndianEncoding, bytes[2..]);
            else
            {
                if (bytes.Contains((byte)0))
                    return FileProbeStatus.Unsupported;
                var preambleLength = bytes.StartsWith(Encoding.UTF8.GetPreamble()) ? 3 : 0;
                content = DecodeBoundedPrefix(StrictShebangUtf8Encoding, bytes[preambleLength..]);
            }

            return FileProbeStatus.Supported;
        }
        catch (FileNotFoundException)
        {
            return FileProbeStatus.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return FileProbeStatus.Missing;
        }
        catch (IOException)
        {
            return FileProbeStatus.ProbeFailed;
        }
        catch (UnauthorizedAccessException)
        {
            return FileProbeStatus.ProbeFailed;
        }
        catch (DecoderFallbackException)
        {
            return FileProbeStatus.Unsupported;
        }
    }

    private static string DecodeBoundedPrefix(Encoding encoding, ReadOnlySpan<byte> bytes)
    {
        var decoder = encoding.GetDecoder();
        var chars = new char[encoding.GetMaxCharCount(bytes.Length)];
        decoder.Convert(bytes, chars, flush: false, out _, out var charsUsed, out _);
        return new string(chars, 0, charsUsed);
    }

    private static (bool FirstFamily, bool SecondFamily) ProbeAmbiguousLanguageProjectMarkers(
        string filePath,
        string? projectRoot,
        bool isMExtension,
        Func<string, IEnumerable<string>>? enumerateFileSystemEntries)
    {
        string? directory;
        string? normalizedRoot;
        try
        {
            directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
            normalizedRoot = string.IsNullOrWhiteSpace(projectRoot)
                ? null
                : Path.GetFullPath(projectRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return default;
        }

        if (string.IsNullOrEmpty(directory))
            return default;

        var firstFamily = false;
        var secondFamily = false;
        for (var depth = 0; depth < 32 && !string.IsNullOrEmpty(directory); depth++)
        {
            try
            {
                var inspected = 0;
                var entries = enumerateFileSystemEntries?.Invoke(directory)
                    ?? Directory.EnumerateFileSystemEntries(directory);
                foreach (var entry in entries)
                {
                    if (++inspected > AmbiguousProjectMarkerEntryLimit)
                        break;

                    var name = Path.GetFileName(entry);
                    var markerFamilies = GetAmbiguousLanguageProjectMarkerFamilies(name, isMExtension);
                    firstFamily |= markerFamilies.FirstFamily;
                    secondFamily |= markerFamilies.SecondFamily;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
            {
                return default;
            }

            if (firstFamily || secondFamily || normalizedRoot == null
                || string.Equals(directory, normalizedRoot, StringComparison.Ordinal))
            {
                break;
            }

            var parent = Path.GetDirectoryName(directory);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, directory, StringComparison.Ordinal))
                break;
            directory = parent;
        }

        return (firstFamily, secondFamily);
    }

    internal static bool IsAmbiguousLanguageProjectMarkerPath(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        var mFamilies = GetAmbiguousLanguageProjectMarkerFamilies(name, isMExtension: true);
        var plFamilies = GetAmbiguousLanguageProjectMarkerFamilies(name, isMExtension: false);
        return mFamilies.FirstFamily || mFamilies.SecondFamily
            || plFamilies.FirstFamily || plFamilies.SecondFamily;
    }

    private static (bool FirstFamily, bool SecondFamily) GetAmbiguousLanguageProjectMarkerFamilies(
        string name,
        bool isMExtension)
    {
        if (isMExtension)
        {
            return (
                name.EndsWith(".xcodeproj", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".xcworkspace", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "project.pbxproj", StringComparison.OrdinalIgnoreCase),
                name.EndsWith(".prj", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".slx", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".mlx", StringComparison.OrdinalIgnoreCase));
        }

        return (
            string.Equals(name, "Makefile.PL", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Build.PL", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "cpanfile", StringComparison.OrdinalIgnoreCase),
            name.EndsWith(".pro", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".prolog", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "pack.pl", StringComparison.OrdinalIgnoreCase));
    }
}
