using System.Text;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    internal const int AmbiguousLanguageProbeByteLimit = 64 * 1024;
    internal const int AmbiguousProjectMarkerEntryLimit = 256;
    internal const int AmbiguousProjectMarkerAncestorLimit = 32;

    internal sealed record AmbiguousLanguageCandidate(
        string Language,
        string DisplayName,
        string ContentPattern,
        IReadOnlyList<string> ProjectMarkerPatterns)
    {
        internal Regex ContentMarker { get; } = new(
            ContentPattern,
            RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);

        internal bool MatchesProjectMarker(string name)
            => ProjectMarkerPatterns.Any(pattern =>
                pattern.StartsWith('*')
                    ? name.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)
                    : string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase));
    }

    internal sealed record AmbiguousLanguageDescriptor(
        string Extension,
        string BucketLanguage,
        IReadOnlyList<AmbiguousLanguageCandidate> Candidates);

    private static readonly AmbiguousLanguageDescriptor[] AmbiguousLanguageDescriptors =
    [
        new(
            ".m",
            "ambiguous_m",
            [
                new(
                    "objc",
                    "Objective-C",
                    @"^\s*(?:#\s*import\b|@(?:interface|implementation|protocol)\b)",
                    ["*.xcodeproj", "*.xcworkspace", "project.pbxproj"]),
                new(
                    "matlab",
                    "MATLAB",
                    @"^\s*(?:function\b|classdef\b)",
                    ["*.prj", "*.slx", "*.mlx"]),
            ]),
        new(
            ".pl",
            "ambiguous_pl",
            [
                new(
                    "perl",
                    "Perl",
                    @"^\s*(?:use\s+(?:strict|warnings)\s*;|package\s+[A-Za-z_]\w*(?:::\w+)*\s*;|(?:my|our|state)\s+[$@%][A-Za-z_]\w*|sub\s+[A-Za-z_]\w*)",
                    ["Makefile.PL", "Build.PL", "cpanfile"]),
                new(
                    "prolog",
                    "Prolog",
                    @"^\s*(?::-\s*(?:module|use_module|dynamic|multifile|discontiguous|initialization)\b|\?-\s*|[a-z][A-Za-z0-9_]*(?:\s*\([^\r\n.]*\))?\s*(?::-|-->))",
                    ["*.pro", "*.prolog", "pack.pl"]),
            ]),
    ];

    internal static bool TryGetAmbiguousLanguageDescriptor(
        string extension,
        out AmbiguousLanguageDescriptor descriptor)
    {
        descriptor = AmbiguousLanguageDescriptors.FirstOrDefault(candidate =>
            string.Equals(candidate.Extension, extension, StringComparison.OrdinalIgnoreCase))!;
        return descriptor is not null;
    }

    private static LanguageDetectionResult TryDetectAmbiguousExtensionLanguage(
        string filePath,
        string extension,
        string? content,
        string? projectRoot,
        FileProbeStatus? knownIndexability,
        Func<string, FileStream>? openReadForIndexContent,
        Func<string, IEnumerable<string>>? enumerateFileSystemEntries)
    {
        if (!TryGetAmbiguousLanguageDescriptor(extension, out var descriptor))
            throw new ArgumentOutOfRangeException(nameof(extension), extension, "Expected an ambiguous language extension.");

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
                {
                    return new LanguageDetectionResult(
                        FileProbeStatus.Supported,
                        descriptor.BucketLanguage,
                        AmbiguousFallbackDetectionSource,
                        LanguageDetectionConfidence.Low);
                }
                return new LanguageDetectionResult(readResult, null);
            }
        }

        var firstCandidate = descriptor.Candidates[0];
        var secondCandidate = descriptor.Candidates[1];
        var firstContentMarker = firstCandidate.ContentMarker.IsMatch(content);
        var secondContentMarker = secondCandidate.ContentMarker.IsMatch(content);

        if (firstContentMarker != secondContentMarker)
        {
            return new LanguageDetectionResult(
                FileProbeStatus.Supported,
                firstContentMarker ? firstCandidate.Language : secondCandidate.Language,
                AmbiguousContentDetectionSource,
                LanguageDetectionConfidence.High);
        }

        if (firstContentMarker)
        {
            return new LanguageDetectionResult(
                FileProbeStatus.Supported,
                descriptor.BucketLanguage,
                AmbiguousFallbackDetectionSource,
                LanguageDetectionConfidence.Low);
        }

        var (firstProjectMarker, secondProjectMarker) = ProbeAmbiguousLanguageProjectMarkers(
            filePath,
            projectRoot,
            descriptor,
            enumerateFileSystemEntries);
        if (firstProjectMarker != secondProjectMarker)
        {
            return new LanguageDetectionResult(
                FileProbeStatus.Supported,
                firstProjectMarker ? firstCandidate.Language : secondCandidate.Language,
                AmbiguousProjectDetectionSource,
                LanguageDetectionConfidence.Medium);
        }

        return new LanguageDetectionResult(
            FileProbeStatus.Supported,
            descriptor.BucketLanguage,
            AmbiguousFallbackDetectionSource,
            LanguageDetectionConfidence.Low);
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
        AmbiguousLanguageDescriptor descriptor,
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
        for (var depth = 0; depth < AmbiguousProjectMarkerAncestorLimit && !string.IsNullOrEmpty(directory); depth++)
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
                    firstFamily |= descriptor.Candidates[0].MatchesProjectMarker(name);
                    secondFamily |= descriptor.Candidates[1].MatchesProjectMarker(name);
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
        return AmbiguousLanguageDescriptors.Any(descriptor =>
            descriptor.Candidates.Any(candidate => candidate.MatchesProjectMarker(name)));
    }
}
