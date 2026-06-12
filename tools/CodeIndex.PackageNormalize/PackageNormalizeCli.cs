using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace CodeIndex.PackageNormalize;

public static class PackageNormalizeCli
{
    public static int Run(string[] args)
    {
        if (args.Any(arg => arg is "-h" or "--help"))
        {
            WriteUsage();
            return 0;
        }

        if (!PackageNormalizeOptions.TryParse(args, out var options, out var parseError))
        {
            Console.Error.WriteLine($"Error: {parseError}");
            WriteUsage();
            return 1;
        }

        var results = new List<PackageNormalizePackageResult>();
        var summary = new PackageNormalizeSummary();

        foreach (var packagePath in options.PackagePaths)
        {
            summary.Inspected++;
            try
            {
                if (options.DryRun)
                {
                    var inspection = PackageCorePropertiesNormalizer.InspectPackage(packagePath);
                    if (inspection.NeedsNormalization)
                    {
                        summary.Skipped++;
                        results.Add(new PackageNormalizePackageResult(packagePath, "would_normalize", null));
                        if (!options.Json)
                            Console.WriteLine($"Would normalize {packagePath}");
                    }
                    else
                    {
                        summary.Unchanged++;
                        results.Add(new PackageNormalizePackageResult(packagePath, "unchanged", null));
                        if (!options.Json)
                            Console.WriteLine($"Unchanged {packagePath}");
                    }
                }
                else
                {
                    PackageCorePropertiesNormalizer.NormalizePackage(packagePath);
                    summary.Normalized++;
                    results.Add(new PackageNormalizePackageResult(packagePath, "normalized", null));
                    if (!options.Json)
                        Console.WriteLine($"Normalized {packagePath}");
                }
            }
            catch (Exception ex)
            {
                summary.Failed++;
                results.Add(new PackageNormalizePackageResult(packagePath, "failed", ex.Message));
                if (!options.Json)
                    Console.Error.WriteLine($"Failed {packagePath}: {ex.Message}");

                if (!options.ContinueOnError)
                    break;
            }
        }

        if (options.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new PackageNormalizeJsonResult(
                    options.DryRun,
                    options.ContinueOnError,
                    summary.Inspected,
                    summary.Normalized,
                    summary.Unchanged,
                    summary.Failed,
                    summary.Skipped,
                    results),
                PackageNormalizeJsonContext.Default.PackageNormalizeJsonResult));
        }
        else if (options.Summary)
        {
            Console.WriteLine(
                $"Summary: inspected={summary.Inspected} normalized={summary.Normalized} unchanged={summary.Unchanged} failed={summary.Failed} skipped={summary.Skipped}");
        }

        return summary.Failed == 0 ? 0 : 1;
    }

    private static void WriteUsage()
    {
        Console.Error.WriteLine("Usage: dotnet run --project tools/CodeIndex.PackageNormalize -- [--dry-run|--check] [--summary] [--json] [--continue-on-error] <package.nupkg|package.snupkg> [...]");
    }
}

internal sealed class PackageNormalizeOptions
{
    private PackageNormalizeOptions(bool dryRun, bool summary, bool json, bool continueOnError, IReadOnlyList<string> packagePaths)
    {
        DryRun = dryRun;
        Summary = summary;
        Json = json;
        ContinueOnError = continueOnError;
        PackagePaths = packagePaths;
    }

    internal bool DryRun { get; }
    internal bool Summary { get; }
    internal bool Json { get; }
    internal bool ContinueOnError { get; }
    internal IReadOnlyList<string> PackagePaths { get; }

    internal static bool TryParse(string[] args, out PackageNormalizeOptions options, out string error)
    {
        var dryRun = false;
        var summary = false;
        var json = false;
        var continueOnError = false;
        var packagePaths = new List<string>();

        foreach (var arg in args)
        {
            switch (arg)
            {
                case "--dry-run":
                case "--check":
                    dryRun = true;
                    break;
                case "--summary":
                    summary = true;
                    break;
                case "--json":
                    json = true;
                    break;
                case "--continue-on-error":
                    continueOnError = true;
                    break;
                default:
                    if (arg.Length > 0 && arg[0] == '-')
                    {
                        options = null!;
                        error = $"unknown option: {arg}";
                        return false;
                    }

                    packagePaths.Add(arg);
                    break;
            }
        }

        if (packagePaths.Count == 0)
        {
            options = null!;
            error = "at least one package path is required.";
            return false;
        }

        options = new PackageNormalizeOptions(dryRun, summary, json, continueOnError, packagePaths);
        error = string.Empty;
        return true;
    }
}

internal sealed record PackageNormalizePackageResult(
    [property: System.Text.Json.Serialization.JsonPropertyName("path")] string Path,
    [property: System.Text.Json.Serialization.JsonPropertyName("status")] string Status,
    [property: System.Text.Json.Serialization.JsonPropertyName("error")] string? Error);

internal sealed record PackageNormalizeJsonResult(
    [property: System.Text.Json.Serialization.JsonPropertyName("dry_run")] bool DryRun,
    [property: System.Text.Json.Serialization.JsonPropertyName("continue_on_error")] bool ContinueOnError,
    [property: System.Text.Json.Serialization.JsonPropertyName("inspected")] int Inspected,
    [property: System.Text.Json.Serialization.JsonPropertyName("normalized")] int Normalized,
    [property: System.Text.Json.Serialization.JsonPropertyName("unchanged")] int Unchanged,
    [property: System.Text.Json.Serialization.JsonPropertyName("failed")] int Failed,
    [property: System.Text.Json.Serialization.JsonPropertyName("skipped")] int Skipped,
    [property: System.Text.Json.Serialization.JsonPropertyName("packages")] IReadOnlyList<PackageNormalizePackageResult> Packages);

[System.Text.Json.Serialization.JsonSerializable(typeof(PackageNormalizeJsonResult))]
internal sealed partial class PackageNormalizeJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

public static class PackageCorePropertiesNormalizer
{
    public const string CanonicalCorePropertiesPath = "package/services/metadata/core-properties/core-properties.psmdcp";

    private static readonly DateTimeOffset StableZipTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static void NormalizePackage(string packagePath)
    {
        NormalizePackage(packagePath, PackageNormalizeLimits.Default);
    }

    internal static void NormalizePackage(string packagePath, PackageNormalizeLimits limits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        limits.Validate();

        var fullPath = Path.GetFullPath(packagePath);
        var tempPath = fullPath + ".normalize-tmp";
        var completed = false;

        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);

            using (var sourceStream = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sourceArchive = new ZipArchive(sourceStream, ZipArchiveMode.Read, leaveOpen: false))
            {
                var originalCorePropertiesPath = ValidateSourceArchive(sourceArchive, packagePath, limits);
                ValidateEntryNamesBeforeRewrite(sourceArchive, originalCorePropertiesPath);

                using var destinationStream = File.Open(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                using var destinationArchive = new ZipArchive(destinationStream, ZipArchiveMode.Create, leaveOpen: false);
                var readBudget = new PackageNormalizeReadBudget(limits);
                var usedNames = new HashSet<string>(StringComparer.Ordinal);

                foreach (var sourceEntry in sourceArchive.Entries)
                {
                    var destinationName = sourceEntry.FullName == originalCorePropertiesPath
                        ? CanonicalCorePropertiesPath
                        : sourceEntry.FullName;

                    if (!usedNames.Add(destinationName))
                        throw new InvalidOperationException($"Duplicate ZIP entry after normalization: {destinationName}");

                    var destinationEntry = destinationArchive.CreateEntry(destinationName, CompressionLevel.Optimal);
                    destinationEntry.LastWriteTime = StableZipTimestamp;
                    destinationEntry.ExternalAttributes = sourceEntry.ExternalAttributes;

                    using var rawSourceEntryStream = sourceEntry.Open();
                    using var sourceEntryStream = new BudgetedEntryReadStream(rawSourceEntryStream, sourceEntry, readBudget);
                    using var destinationEntryStream = destinationEntry.Open();

                    if (NeedsXmlReferenceRewrite(sourceEntry.FullName))
                    {
                        using var writer = new StreamWriter(destinationEntryStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: false);
                        writer.Write(RewriteCorePropertiesReferences(ReadXmlEntryText(sourceEntry, sourceEntryStream, limits), originalCorePropertiesPath));
                    }
                    else
                    {
                        CopyEntry(sourceEntryStream, destinationEntryStream);
                    }
                }
            }

            File.Move(tempPath, fullPath, overwrite: true);
            completed = true;
        }
        finally
        {
            if (!completed)
                TryDeleteFile(tempPath);
        }
    }

    internal static PackageNormalizeInspection InspectPackage(string packagePath)
    {
        return InspectPackage(packagePath, PackageNormalizeLimits.Default);
    }

    internal static PackageNormalizeInspection InspectPackage(string packagePath, PackageNormalizeLimits limits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        limits.Validate();

        var fullPath = Path.GetFullPath(packagePath);
        using var sourceStream = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sourceArchive = new ZipArchive(sourceStream, ZipArchiveMode.Read, leaveOpen: false);
        var originalCorePropertiesPath = ValidateSourceArchive(sourceArchive, packagePath, limits);
        ValidateEntryNamesBeforeRewrite(sourceArchive, originalCorePropertiesPath);
        var needsNormalization = originalCorePropertiesPath != CanonicalCorePropertiesPath
            || XmlReferencesNeedRewrite(sourceArchive, originalCorePropertiesPath, limits);

        return new PackageNormalizeInspection(fullPath, needsNormalization, originalCorePropertiesPath);
    }

    private static string ValidateSourceArchive(ZipArchive sourceArchive, string packagePath, PackageNormalizeLimits limits)
    {
        if (sourceArchive.Entries.Count > limits.MaxEntryCount)
            throw new InvalidOperationException($"Package {packagePath} has {sourceArchive.Entries.Count} ZIP entries, which exceeds the limit of {limits.MaxEntryCount}.");

        string? originalCorePropertiesPath = null;
        var corePropertiesEntryCount = 0;
        long totalUncompressedBytes = 0;

        foreach (var sourceEntry in sourceArchive.Entries)
        {
            ValidateEntrySize(sourceEntry, limits);

            if (totalUncompressedBytes > limits.MaxTotalUncompressedBytes - sourceEntry.Length)
            {
                throw new InvalidOperationException(
                    $"ZIP entry {sourceEntry.FullName} makes package uncompressed size exceed the limit of {limits.MaxTotalUncompressedBytes} bytes.");
            }

            totalUncompressedBytes += sourceEntry.Length;

            if (!IsCorePropertiesPart(sourceEntry.FullName))
                continue;

            corePropertiesEntryCount++;
            originalCorePropertiesPath = sourceEntry.FullName;
        }

        if (corePropertiesEntryCount != 1)
            throw new InvalidOperationException($"Expected exactly one NuGet core-properties part in {packagePath}, found {corePropertiesEntryCount}.");

        return originalCorePropertiesPath!;
    }

    private static void ValidateEntryNamesBeforeRewrite(ZipArchive sourceArchive, string originalCorePropertiesPath)
    {
        var normalizedDestinationNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var sourceEntry in sourceArchive.Entries)
        {
            ValidateZipEntryName(sourceEntry.FullName, "source");

            var destinationName = sourceEntry.FullName == originalCorePropertiesPath
                ? CanonicalCorePropertiesPath
                : sourceEntry.FullName;
            var normalizedDestinationName = ValidateZipEntryName(destinationName, "destination");

            if (!normalizedDestinationNames.Add(normalizedDestinationName))
            {
                throw new InvalidOperationException(
                    $"ZIP entry {destinationName} normalizes to duplicate destination name {normalizedDestinationName}.");
            }
        }
    }

    private static string ValidateZipEntryName(string entryName, string role)
    {
        if (entryName.Length == 0)
            throw new InvalidOperationException($"ZIP {role} entry name must not be empty.");

        if (entryName.Contains('\\'))
            throw new InvalidOperationException($"ZIP {role} entry {entryName} must use '/' separators, not backslashes.");

        if (entryName.Contains('\0'))
            throw new InvalidOperationException($"ZIP {role} entry {entryName} must not contain NUL characters.");

        if (entryName[0] == '/' || StartsWithWindowsDrivePrefix(entryName))
            throw new InvalidOperationException($"ZIP {role} entry {entryName} must be a relative path.");

        var segments = entryName.Split('/');
        var normalizedSegments = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
                throw new InvalidOperationException($"ZIP {role} entry {entryName} must not contain empty path segments.");

            if (segment == "..")
                throw new InvalidOperationException($"ZIP {role} entry {entryName} must not contain parent-directory segments.");

            if (segment == ".")
                continue;

            normalizedSegments.Add(segment);
        }

        if (normalizedSegments.Count == 0)
            throw new InvalidOperationException($"ZIP {role} entry {entryName} must not normalize to an empty path.");

        var normalizedName = string.Join('/', normalizedSegments);
        if (normalizedName[0] == '/' || StartsWithWindowsDrivePrefix(normalizedName))
            throw new InvalidOperationException($"ZIP {role} entry {entryName} must be a relative path.");

        return normalizedName;
    }

    private static bool StartsWithWindowsDrivePrefix(string entryName)
    {
        return entryName.Length >= 2
            && entryName[1] == ':'
            && ((entryName[0] >= 'A' && entryName[0] <= 'Z') || (entryName[0] >= 'a' && entryName[0] <= 'z'));
    }

    private static void ValidateEntrySize(ZipArchiveEntry sourceEntry, PackageNormalizeLimits limits)
    {
        if (sourceEntry.Length > limits.MaxEntryUncompressedBytes)
        {
            throw new InvalidOperationException(
                $"ZIP entry {sourceEntry.FullName} is {sourceEntry.Length} bytes uncompressed, which exceeds the per-entry limit of {limits.MaxEntryUncompressedBytes} bytes.");
        }
    }

    private static string ReadXmlEntryText(ZipArchiveEntry sourceEntry, Stream sourceEntryStream, PackageNormalizeLimits limits)
    {
        using var reader = new StreamReader(sourceEntryStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
        var buffer = new char[4096];
        var builder = new StringBuilder();

        while (true)
        {
            var charsRead = reader.Read(buffer, 0, buffer.Length);
            if (charsRead == 0)
                return builder.ToString();

            if (builder.Length > limits.MaxXmlTextChars - charsRead)
            {
                throw new InvalidOperationException(
                    $"XML ZIP entry {sourceEntry.FullName} exceeds the text limit of {limits.MaxXmlTextChars} characters.");
            }

            builder.Append(buffer, 0, charsRead);
        }
    }

    private static void CopyEntry(Stream sourceEntryStream, Stream destinationEntryStream)
    {
        var buffer = new byte[81920];

        while (true)
        {
            var bytesRead = sourceEntryStream.Read(buffer, 0, buffer.Length);
            if (bytesRead == 0)
                return;

            destinationEntryStream.Write(buffer, 0, bytesRead);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class PackageNormalizeReadBudget
    {
        private readonly PackageNormalizeLimits _limits;
        private long _totalBytesRead;

        internal PackageNormalizeReadBudget(PackageNormalizeLimits limits)
        {
            _limits = limits;
        }

        internal void AddBytes(ZipArchiveEntry sourceEntry, long entryBytesRead, int bytesRead)
        {
            if (bytesRead <= 0)
                return;

            if (entryBytesRead > _limits.MaxEntryUncompressedBytes - bytesRead)
            {
                throw new InvalidOperationException(
                    $"ZIP entry {sourceEntry.FullName} exceeds the per-entry inflated size limit of {_limits.MaxEntryUncompressedBytes} bytes.");
            }

            if (_totalBytesRead > _limits.MaxTotalUncompressedBytes - bytesRead)
            {
                throw new InvalidOperationException(
                    $"ZIP entry {sourceEntry.FullName} makes actual inflated package size exceed the limit of {_limits.MaxTotalUncompressedBytes} bytes.");
            }

            _totalBytesRead += bytesRead;
        }
    }

    private sealed class BudgetedEntryReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly ZipArchiveEntry _sourceEntry;
        private readonly PackageNormalizeReadBudget _readBudget;
        private long _entryBytesRead;

        internal BudgetedEntryReadStream(Stream inner, ZipArchiveEntry sourceEntry, PackageNormalizeReadBudget readBudget)
        {
            _inner = inner;
            _sourceEntry = sourceEntry;
            _readBudget = readBudget;
        }

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var bytesRead = _inner.Read(buffer, offset, count);
            TrackBytesRead(bytesRead);
            return bytesRead;
        }

        public override int Read(Span<byte> buffer)
        {
            var bytesRead = _inner.Read(buffer);
            TrackBytesRead(bytesRead);
            return bytesRead;
        }

        public override void Flush()
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();

            base.Dispose(disposing);
        }

        private void TrackBytesRead(int bytesRead)
        {
            _readBudget.AddBytes(_sourceEntry, _entryBytesRead, bytesRead);
            _entryBytesRead += bytesRead;
        }
    }

    private static bool IsCorePropertiesPart(string entryName)
    {
        return entryName.StartsWith("package/services/metadata/core-properties/", StringComparison.Ordinal)
            && entryName.EndsWith(".psmdcp", StringComparison.Ordinal);
    }

    private static bool NeedsXmlReferenceRewrite(string entryName)
    {
        return entryName.Equals("[Content_Types].xml", StringComparison.Ordinal)
            || entryName.EndsWith(".rels", StringComparison.Ordinal);
    }

    private static bool XmlReferencesNeedRewrite(
        ZipArchive sourceArchive,
        string originalCorePropertiesPath,
        PackageNormalizeLimits limits)
    {
        var readBudget = new PackageNormalizeReadBudget(limits);
        foreach (var sourceEntry in sourceArchive.Entries)
        {
            if (!NeedsXmlReferenceRewrite(sourceEntry.FullName))
                continue;

            using var rawSourceEntryStream = sourceEntry.Open();
            using var sourceEntryStream = new BudgetedEntryReadStream(rawSourceEntryStream, sourceEntry, readBudget);
            var content = ReadXmlEntryText(sourceEntry, sourceEntryStream, limits);
            if (RewriteCorePropertiesReferences(content, originalCorePropertiesPath) != content)
                return true;
        }

        return false;
    }

    private static string RewriteCorePropertiesReferences(string content, string originalCorePropertiesPath)
    {
        var canonical = CanonicalCorePropertiesPath;
        return content
            .Replace(originalCorePropertiesPath, canonical, StringComparison.Ordinal)
            .Replace("/" + originalCorePropertiesPath, "/" + canonical, StringComparison.Ordinal);
    }
}

internal readonly record struct PackageNormalizeInspection(
    string PackagePath,
    bool NeedsNormalization,
    string OriginalCorePropertiesPath);

internal sealed class PackageNormalizeSummary
{
    internal int Inspected { get; set; }
    internal int Normalized { get; set; }
    internal int Unchanged { get; set; }
    internal int Failed { get; set; }
    internal int Skipped { get; set; }
}

internal readonly record struct PackageNormalizeLimits(
    int MaxEntryCount,
    long MaxEntryUncompressedBytes,
    long MaxTotalUncompressedBytes,
    int MaxXmlTextChars)
{
    internal static PackageNormalizeLimits Default { get; } = new(
        MaxEntryCount: 4096,
        MaxEntryUncompressedBytes: 128L * 1024 * 1024,
        MaxTotalUncompressedBytes: 512L * 1024 * 1024,
        MaxXmlTextChars: 16 * 1024 * 1024);

    internal void Validate()
    {
        if (MaxEntryCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxEntryCount), MaxEntryCount, "ZIP entry count limit must be positive.");

        if (MaxEntryUncompressedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxEntryUncompressedBytes), MaxEntryUncompressedBytes, "ZIP entry size limit must be positive.");

        if (MaxTotalUncompressedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxTotalUncompressedBytes), MaxTotalUncompressedBytes, "ZIP total size limit must be positive.");

        if (MaxXmlTextChars <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxXmlTextChars), MaxXmlTextChars, "ZIP XML text limit must be positive.");
    }
}
