using System.IO.Compression;
using System.Text;

namespace CodeIndex.PackageNormalize;

public static class PackageNormalizeCli
{
    public static int Run(string[] args)
    {
        if (args.Length == 0 || args.Any(arg => arg is "-h" or "--help"))
        {
            Console.Error.WriteLine("Usage: dotnet run --project tools/CodeIndex.PackageNormalize -- <package.nupkg|package.snupkg> [...]");
            return args.Length == 0 ? 1 : 0;
        }

        foreach (var packagePath in args)
        {
            PackageCorePropertiesNormalizer.NormalizePackage(packagePath);
            Console.WriteLine($"Normalized {packagePath}");
        }

        return 0;
    }
}

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
        if (File.Exists(tempPath))
            File.Delete(tempPath);

        using (var sourceStream = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var sourceArchive = new ZipArchive(sourceStream, ZipArchiveMode.Read, leaveOpen: false))
        {
            var originalCorePropertiesPath = ValidateSourceArchive(sourceArchive, packagePath, limits);

            using var destinationStream = File.Open(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            using var destinationArchive = new ZipArchive(destinationStream, ZipArchiveMode.Create, leaveOpen: false);
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

                using var sourceEntryStream = sourceEntry.Open();
                using var destinationEntryStream = destinationEntry.Open();

                if (NeedsXmlReferenceRewrite(sourceEntry.FullName))
                {
                    using var writer = new StreamWriter(destinationEntryStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: false);
                    writer.Write(RewriteCorePropertiesReferences(ReadXmlEntryText(sourceEntry, sourceEntryStream, limits), originalCorePropertiesPath));
                }
                else
                {
                    CopyEntryWithLimit(sourceEntry, sourceEntryStream, destinationEntryStream, limits);
                }
            }
        }

        File.Move(tempPath, fullPath, overwrite: true);
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

    private static void CopyEntryWithLimit(
        ZipArchiveEntry sourceEntry,
        Stream sourceEntryStream,
        Stream destinationEntryStream,
        PackageNormalizeLimits limits)
    {
        var buffer = new byte[81920];
        long bytesCopied = 0;

        while (true)
        {
            var bytesRead = sourceEntryStream.Read(buffer, 0, buffer.Length);
            if (bytesRead == 0)
                return;

            if (bytesCopied > limits.MaxEntryUncompressedBytes - bytesRead)
            {
                throw new InvalidOperationException(
                    $"ZIP entry {sourceEntry.FullName} exceeds the per-entry copy limit of {limits.MaxEntryUncompressedBytes} bytes.");
            }

            bytesCopied += bytesRead;
            destinationEntryStream.Write(buffer, 0, bytesRead);
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

    private static string RewriteCorePropertiesReferences(string content, string originalCorePropertiesPath)
    {
        var canonical = CanonicalCorePropertiesPath;
        return content
            .Replace(originalCorePropertiesPath, canonical, StringComparison.Ordinal)
            .Replace("/" + originalCorePropertiesPath, "/" + canonical, StringComparison.Ordinal);
    }
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
