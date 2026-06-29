using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeIndex.Archives;

namespace CodeIndex.PackageNormalize;

public static class PackageNormalizeCli
{
    public static int Run(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        Console.CancelKeyPress += handler;
        try
        {
            return Run(args, Console.Out, Console.Error, cancellation.Token);
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    internal static int Run(string[] args, TextWriter stdout, TextWriter stderr)
        => Run(args, stdout, stderr, CancellationToken.None);

    internal static int Run(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        if (args.Any(arg => arg is "-h" or "--help"))
        {
            WriteUsage(stderr);
            return 0;
        }

        if (!PackageNormalizeOptions.TryParse(args, out var options, out var parseError))
        {
            stderr.WriteLine($"Error: {parseError}");
            WriteUsage(stderr);
            return 1;
        }

        var results = new List<PackageNormalizePackageResult>();
        var summary = new PackageNormalizeSummary();

        foreach (var packagePath in options.PackagePaths)
        {
            summary.Inspected++;
            var warnings = new List<string>();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (options.DryRun)
                {
                    var inspection = PackageCorePropertiesNormalizer.InspectPackage(
                        packagePath,
                        PackageNormalizeLimits.Default,
                        cancellationToken);
                    if (inspection.NeedsNormalization)
                    {
                        summary.Skipped++;
                        results.Add(new PackageNormalizePackageResult(packagePath, "would_normalize", null, warnings));
                        if (!options.Json)
                            stdout.WriteLine($"Would normalize {packagePath}");
                    }
                    else
                    {
                        summary.Unchanged++;
                        results.Add(new PackageNormalizePackageResult(packagePath, "unchanged", null, warnings));
                        if (!options.Json)
                            stdout.WriteLine($"Unchanged {packagePath}");
                    }
                }
                else
                {
                    PackageCorePropertiesNormalizer.NormalizePackage(
                        packagePath,
                        PackageNormalizeLimits.Default,
                        warnings,
                        cancellationToken);
                    summary.Normalized++;
                    results.Add(new PackageNormalizePackageResult(packagePath, "normalized", null, warnings));
                    if (!options.Json)
                    {
                        stdout.WriteLine($"Normalized {packagePath}");
                        WriteWarnings(stderr, warnings);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                const string error = "Package normalization was cancelled.";
                summary.Failed++;
                results.Add(new PackageNormalizePackageResult(packagePath, "failed", error, warnings));
                if (!options.Json)
                {
                    stderr.WriteLine($"Failed {PackageNormalizeDiagnostics.FormatPath(packagePath)}: {error}");
                    WriteWarnings(stderr, warnings);
                }

                break;
            }
            catch (Exception ex)
            {
                var error = PackageNormalizeDiagnostics.FormatException(packagePath, ex);
                summary.Failed++;
                results.Add(new PackageNormalizePackageResult(packagePath, "failed", error, warnings));
                if (!options.Json)
                {
                    stderr.WriteLine($"Failed {PackageNormalizeDiagnostics.FormatPath(packagePath)}: {error}");
                    WriteWarnings(stderr, warnings);
                }

                if (!options.ContinueOnError)
                    break;
            }
        }

        if (options.Json)
        {
            stdout.WriteLine(JsonSerializer.Serialize(
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
            stdout.WriteLine(
                $"Summary: inspected={summary.Inspected} normalized={summary.Normalized} unchanged={summary.Unchanged} failed={summary.Failed} skipped={summary.Skipped}");
        }

        return summary.Failed == 0 ? 0 : 1;
    }

    private static void WriteUsage(TextWriter error)
    {
        error.WriteLine("Usage: dotnet run --project tools/CodeIndex.PackageNormalize -- [--dry-run|--check] [--summary] [--json] [--continue-on-error] <package.nupkg|package.snupkg> [...]");
    }

    private static void WriteWarnings(TextWriter error, IReadOnlyList<string> warnings)
    {
        foreach (var warning in warnings)
            error.WriteLine($"Warning: {warning}");
    }
}

internal sealed class PackageNormalizeOptions
{
    internal const int MaxPackageArgumentCount = 1024;

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
                    if (packagePaths.Count > MaxPackageArgumentCount)
                    {
                        options = null!;
                        error = $"at most {MaxPackageArgumentCount} package paths are supported per run.";
                        return false;
                    }

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
    [property: System.Text.Json.Serialization.JsonPropertyName("error")] string? Error,
    [property: System.Text.Json.Serialization.JsonPropertyName("warnings")] IReadOnlyList<string> Warnings);

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

internal static class PackageNormalizeDiagnostics
{
    private const int MaxDiagnosticValueChars = 160;
    private const int MaxDiagnosticMessageChars = 512;
    private const string RedactedValue = "<redacted>";
    private const string RedactedPath = "<path>";
    private static readonly TimeSpan RedactionRegexTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly Regex SensitiveAssignmentPattern = new(
        @"(?<![\w.-])(?<name>(?:--?)?[\w.-]*(?:token|password|passwd|pwd|secret|auth|apikey|api-key|api_key|access-key|access_key|credential)[\w.-]*)(?<sep>=|:)(?<value>[^\s,;]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        RedactionRegexTimeout);
    private static readonly Regex WindowsAbsolutePathPattern = new(
        @"\b[A-Za-z]:[\\/][^\s""'<>|]+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        RedactionRegexTimeout);
    private static readonly Regex UnixAbsolutePathPattern = new(
        @"(?<![A-Za-z0-9+\-.]:)(?<!/)/[^\s""'<>]+(?:/[^\s""'<>]+)*",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        RedactionRegexTimeout);

    internal static string FormatException(string packagePath, Exception exception)
    {
        return exception switch
        {
            InvalidDataException => $"Package {FormatPath(packagePath)} is not a readable ZIP archive.",
            PackageNormalizeReplaceDurabilityException => FormatExceptionMessage(exception),
            IOException => $"Could not read or rewrite package {FormatPath(packagePath)}.",
            UnauthorizedAccessException => $"Could not access package {FormatPath(packagePath)}.",
            ArgumentException => FormatExceptionMessage(exception),
            InvalidOperationException => FormatExceptionMessage(exception),
            _ => $"Unexpected package normalization failure for {FormatPath(packagePath)}: {exception.GetType().Name}.",
        };
    }

    internal static string FormatPath(string path)
    {
        string display;
        try
        {
            display = Path.GetFileName(path);
        }
        catch (ArgumentException)
        {
            display = path;
        }

        if (string.IsNullOrEmpty(display))
            display = path;

        return Quote(FormatValue(display, MaxDiagnosticValueChars));
    }

    internal static string FormatEntryName(string entryName)
    {
        return Quote(FormatValue(entryName, MaxDiagnosticValueChars));
    }

    internal static string FormatMessage(string message)
    {
        return FormatValue(RedactMessage(message), MaxDiagnosticMessageChars);
    }

    private static string FormatExceptionMessage(Exception exception)
    {
        return FormatMessage(exception.Message);
    }

    internal static string FormatCleanupWarning(string tempPath, Exception exception)
    {
        return $"Could not delete temporary normalized package {FormatPath(tempPath)}: {exception.GetType().Name}.";
    }

    private static string Quote(string value)
    {
        return $"'{value}'";
    }

    private static string FormatValue(string value, int maxChars)
    {
        var builder = new StringBuilder(Math.Min(value.Length, maxChars));
        foreach (var ch in value)
        {
            if (builder.Length >= maxChars)
                break;

            builder.Append(IsSafeDiagnosticChar(ch) ? ch : '?');
        }

        if (value.Length > maxChars && builder.Length >= 3)
        {
            builder.Length -= 3;
            builder.Append("...");
        }

        return builder.ToString();
    }

    private static bool IsSafeDiagnosticChar(char ch)
    {
        return ch >= ' ' && ch != '\u007F';
    }

    private static string RedactMessage(string message)
    {
        try
        {
            var redacted = SensitiveAssignmentPattern.Replace(
                message,
                match => match.Groups["name"].Value + match.Groups["sep"].Value + RedactedValue);
            redacted = WindowsAbsolutePathPattern.Replace(redacted, RedactedPath);
            return UnixAbsolutePathPattern.Replace(redacted, RedactedPath);
        }
        catch (RegexMatchTimeoutException)
        {
            return RedactedValue;
        }
    }
}

internal sealed class PackageNormalizeReplaceDurabilityException : IOException
{
    internal PackageNormalizeReplaceDurabilityException(string message)
        : base(message)
    {
    }

    internal PackageNormalizeReplaceDurabilityException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

internal static class PackageNormalizeRewriteFile
{
    internal const int MaxTempFileNameChars = 120;
    private const int MaxTempStemChars = 48;

    internal static Action<string>? FlushParentDirectoryForTesting { get; set; }
    internal static Action<string>? TempFileCreatedForTesting { get; set; }

    internal static string BuildTempPath(string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        var fileName = Path.GetFileName(destinationPath);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(stem))
            stem = "package";
        if (stem.Length > MaxTempStemChars)
            stem = stem[..MaxTempStemChars];

        var tempFileName = $".cdidx-normalize-{stem}.{Guid.NewGuid():N}.tmp";
        if (tempFileName.Length > MaxTempFileNameChars)
            tempFileName = $".cdidx-normalize-{Guid.NewGuid():N}.tmp";

        return string.IsNullOrEmpty(directory)
            ? tempFileName
            : Path.Combine(directory, tempFileName);
    }

    internal static FileStream CreateTempFile(string path)
        => new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);

    internal static void NotifyTempFileCreated(string path)
        => TempFileCreatedForTesting?.Invoke(path);

    internal static void MoveReplacing(string sourcePath, string destinationPath)
    {
        File.Move(sourcePath, destinationPath, overwrite: true);
        FlushParentDirectoryAfterReplace(destinationPath);
    }

    internal static bool TryDeleteFile(
        string path,
        Action<Exception>? onCleanupFailure = null,
        Action<string>? deleteOverride = null)
    {
        try
        {
            if (!File.Exists(path))
                return false;

            if (deleteOverride != null)
                deleteOverride(path);
            else
                File.Delete(path);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onCleanupFailure?.Invoke(ex);
            return false;
        }
    }

    internal static void DeleteStaleLegacyTempFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;

            using (new FileStream(
                path,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.None,
                    Options = FileOptions.DeleteOnClose,
                }))
            {
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw BuildLegacyTempCleanupException(path, ex);
        }
    }

    private static InvalidOperationException BuildLegacyTempCleanupException(string path, Exception inner)
    {
        return new InvalidOperationException(
            $"Temporary normalized package {PackageNormalizeDiagnostics.FormatPath(path)} already exists but could not be locked and removed; aborting normalization to avoid racing another package normalizer.",
            inner);
    }

    private static void FlushParentDirectoryAfterReplace(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(directory))
            return;

        if (FlushParentDirectoryForTesting != null)
        {
            try
            {
                FlushParentDirectoryForTesting(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw BuildDirectoryFlushException(path, ex);
            }

            return;
        }

        if (OperatingSystem.IsWindows())
            return;

        var fd = UnixOpen(directory, flags: 0);
        if (fd < 0)
            throw BuildDirectoryFlushException(path, Marshal.GetLastPInvokeError());

        try
        {
            if (UnixFsync(fd) != 0)
                throw BuildDirectoryFlushException(path, Marshal.GetLastPInvokeError());
        }
        finally
        {
            _ = UnixClose(fd);
        }
    }

    private static PackageNormalizeReplaceDurabilityException BuildDirectoryFlushException(string path, int errno)
        => new($"Package replacement completed for {PackageNormalizeDiagnostics.FormatPath(path)}; the target package was already replaced, but the parent directory could not be flushed to disk (errno {errno}).");

    private static PackageNormalizeReplaceDurabilityException BuildDirectoryFlushException(string path, Exception inner)
        => new($"Package replacement completed for {PackageNormalizeDiagnostics.FormatPath(path)}; the target package was already replaced, but the parent directory could not be flushed to disk ({inner.GetType().Name}).", inner);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int UnixOpen(string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int UnixFsync(int fd);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int UnixClose(int fd);
}

public static class PackageCorePropertiesNormalizer
{
    public const string CanonicalCorePropertiesPath = "package/services/metadata/core-properties/core-properties.psmdcp";

    private const string LegacyTempSuffix = ".normalize-tmp";
    private const int SafeExternalAttributes = 0;
    private const int DosAttributeMask = 0xFF;
    private const int DosArchiveAttribute = 0x20;
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixRegularFileType = 0x8000;
    private const int UnixFifoFileType = 0x1000;
    private const int UnixCharacterDeviceFileType = 0x2000;
    private const int UnixDirectoryFileType = 0x4000;
    private const int UnixBlockDeviceFileType = 0x6000;
    private const int UnixSymlinkFileType = 0xA000;
    private const int UnixSocketFileType = 0xC000;

    private static readonly DateTimeOffset StableZipTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static void NormalizePackage(string packagePath)
    {
        NormalizePackage(packagePath, PackageNormalizeLimits.Default);
    }

    internal static void NormalizePackage(string packagePath, PackageNormalizeLimits limits)
    {
        NormalizePackage(packagePath, limits, warnings: null);
    }

    internal static void NormalizePackage(string packagePath, PackageNormalizeLimits limits, IList<string>? warnings)
    {
        NormalizePackage(packagePath, limits, warnings, CancellationToken.None);
    }

    internal static void NormalizePackage(
        string packagePath,
        PackageNormalizeLimits limits,
        IList<string>? warnings,
        CancellationToken cancellationToken)
    {
        NormalizePackage(packagePath, limits, warnings, File.Delete, cancellationToken);
    }

    internal static void NormalizePackage(
        string packagePath,
        PackageNormalizeLimits limits,
        IList<string>? warnings,
        Action<string> deleteFile)
    {
        NormalizePackage(packagePath, limits, warnings, deleteFile, CancellationToken.None);
    }

    internal static void NormalizePackage(
        string packagePath,
        PackageNormalizeLimits limits,
        IList<string>? warnings,
        Action<string> deleteFile,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentNullException.ThrowIfNull(deleteFile);
        limits.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(packagePath);
        var legacyTempPath = fullPath + LegacyTempSuffix;
        var tempPath = PackageNormalizeRewriteFile.BuildTempPath(fullPath);
        var tempCreated = false;
        var completed = false;

        try
        {
            PackageNormalizeRewriteFile.DeleteStaleLegacyTempFile(legacyTempPath);

            using (var sourceStream = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sourceArchive = new ZipArchive(sourceStream, ZipArchiveMode.Read, leaveOpen: false))
            {
                var originalCorePropertiesPath = ValidateSourceArchive(sourceArchive, packagePath, limits, cancellationToken);
                ValidateEntryNamesBeforeRewrite(sourceArchive, originalCorePropertiesPath, cancellationToken);

                using (var destinationStream = PackageNormalizeRewriteFile.CreateTempFile(tempPath))
                {
                    tempCreated = true;
                    PackageNormalizeRewriteFile.NotifyTempFileCreated(tempPath);
                    using (var destinationArchive = new ZipArchive(destinationStream, ZipArchiveMode.Create, leaveOpen: true))
                    {
                        var readBudget = new PackageNormalizeReadBudget(limits);
                        var usedNames = new HashSet<string>(StringComparer.Ordinal);

                        foreach (var sourceEntry in sourceArchive.Entries)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            var destinationName = sourceEntry.FullName == originalCorePropertiesPath
                                ? CanonicalCorePropertiesPath
                                : sourceEntry.FullName;

                            if (!usedNames.Add(destinationName))
                                throw new InvalidOperationException($"Duplicate ZIP entry after normalization: {PackageNormalizeDiagnostics.FormatEntryName(destinationName)}");

                            var destinationEntry = destinationArchive.CreateEntry(destinationName, CompressionLevel.Optimal);
                            destinationEntry.LastWriteTime = StableZipTimestamp;
                            destinationEntry.ExternalAttributes = SafeExternalAttributes;

                            using var rawSourceEntryStream = sourceEntry.Open();
                            using var sourceEntryStream = new BudgetedEntryReadStream(rawSourceEntryStream, sourceEntry, readBudget, cancellationToken);
                            using var destinationEntryStream = destinationEntry.Open();

                            if (NeedsXmlReferenceRewrite(sourceEntry.FullName))
                            {
                                using var writer = new StreamWriter(destinationEntryStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: false);
                                writer.Write(RewriteCorePropertiesReferences(ReadXmlEntryText(sourceEntry, sourceEntryStream, limits, cancellationToken), originalCorePropertiesPath));
                            }
                            else
                            {
                                CopyEntry(sourceEntryStream, destinationEntryStream, cancellationToken);
                            }
                        }
                    }

                    destinationStream.Flush(flushToDisk: true);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            PackageNormalizeRewriteFile.MoveReplacing(tempPath, fullPath);
            completed = true;
        }
        finally
        {
            if (!completed && tempCreated)
                TryDeleteFile(tempPath, warnings, deleteFile);
        }
    }

    internal static PackageNormalizeInspection InspectPackage(string packagePath)
    {
        return InspectPackage(packagePath, PackageNormalizeLimits.Default);
    }

    internal static PackageNormalizeInspection InspectPackage(string packagePath, PackageNormalizeLimits limits)
    {
        return InspectPackage(packagePath, limits, CancellationToken.None);
    }

    internal static PackageNormalizeInspection InspectPackage(
        string packagePath,
        PackageNormalizeLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        limits.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(packagePath);
        using var sourceStream = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sourceArchive = new ZipArchive(sourceStream, ZipArchiveMode.Read, leaveOpen: false);
        var originalCorePropertiesPath = ValidateSourceArchive(sourceArchive, packagePath, limits, cancellationToken);
        ValidateEntryNamesBeforeRewrite(sourceArchive, originalCorePropertiesPath, cancellationToken);
        var needsNormalization = originalCorePropertiesPath != CanonicalCorePropertiesPath
            || XmlReferencesNeedRewrite(sourceArchive, originalCorePropertiesPath, limits, cancellationToken);

        return new PackageNormalizeInspection(fullPath, needsNormalization, originalCorePropertiesPath);
    }

    private static string ValidateSourceArchive(
        ZipArchive sourceArchive,
        string packagePath,
        PackageNormalizeLimits limits,
        CancellationToken cancellationToken)
    {
        if (sourceArchive.Entries.Count > limits.MaxEntryCount)
            throw new InvalidOperationException($"Package {PackageNormalizeDiagnostics.FormatPath(packagePath)} has {sourceArchive.Entries.Count} ZIP entries, which exceeds the limit of {limits.MaxEntryCount}.");

        string? originalCorePropertiesPath = null;
        var corePropertiesEntryCount = 0;
        long totalUncompressedBytes = 0;

        foreach (var sourceEntry in sourceArchive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateExternalAttributes(sourceEntry);
            ValidateEntrySize(sourceEntry, limits);

            if (totalUncompressedBytes > limits.MaxTotalUncompressedBytes - sourceEntry.Length)
            {
                throw new InvalidOperationException(
                    $"ZIP entry {PackageNormalizeDiagnostics.FormatEntryName(sourceEntry.FullName)} makes package uncompressed size exceed the limit of {limits.MaxTotalUncompressedBytes} bytes.");
            }

            totalUncompressedBytes += sourceEntry.Length;

            if (!IsCorePropertiesPart(sourceEntry.FullName))
                continue;

            corePropertiesEntryCount++;
            originalCorePropertiesPath = sourceEntry.FullName;
        }

        if (corePropertiesEntryCount != 1)
            throw new InvalidOperationException($"Expected exactly one NuGet core-properties part in {PackageNormalizeDiagnostics.FormatPath(packagePath)}, found {corePropertiesEntryCount}.");

        return originalCorePropertiesPath!;
    }

    private static void ValidateEntryNamesBeforeRewrite(
        ZipArchive sourceArchive,
        string originalCorePropertiesPath,
        CancellationToken cancellationToken)
    {
        var normalizedDestinationNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var sourceEntry in sourceArchive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateZipEntryName(sourceEntry.FullName, "source");

            var destinationName = sourceEntry.FullName == originalCorePropertiesPath
                ? CanonicalCorePropertiesPath
                : sourceEntry.FullName;
            var normalizedDestinationName = ValidateZipEntryName(destinationName, "destination");

            if (!normalizedDestinationNames.Add(normalizedDestinationName))
            {
                throw new InvalidOperationException(
                    $"ZIP entry {PackageNormalizeDiagnostics.FormatEntryName(destinationName)} normalizes to duplicate destination name {PackageNormalizeDiagnostics.FormatEntryName(normalizedDestinationName)}.");
            }
        }
    }

    private static string ValidateZipEntryName(string entryName, string role)
    {
        if (ZipArchiveSafetyPolicy.TryNormalizeRelativeEntryName(entryName, out var normalizedName, out var failureReason))
            return normalizedName;

        var subject = entryName.Length == 0
            ? $"ZIP {role} entry name"
            : $"ZIP {role} entry {PackageNormalizeDiagnostics.FormatEntryName(entryName)}";
        throw new InvalidOperationException($"{subject} {failureReason}.");
    }

    private static void ValidateEntrySize(ZipArchiveEntry sourceEntry, PackageNormalizeLimits limits)
    {
        if (sourceEntry.Length > limits.MaxEntryUncompressedBytes)
        {
            throw new InvalidOperationException(
                $"ZIP entry {PackageNormalizeDiagnostics.FormatEntryName(sourceEntry.FullName)} is {sourceEntry.Length} bytes uncompressed, which exceeds the per-entry limit of {limits.MaxEntryUncompressedBytes} bytes.");
        }
    }

    private static void ValidateExternalAttributes(ZipArchiveEntry sourceEntry)
    {
        var externalAttributes = sourceEntry.ExternalAttributes;
        var unixMode = (externalAttributes >> 16) & 0xFFFF;
        var unixFileType = unixMode & UnixFileTypeMask;

        if (unixFileType != 0 && unixFileType != UnixRegularFileType)
        {
            throw new InvalidOperationException(
                $"ZIP entry {PackageNormalizeDiagnostics.FormatEntryName(sourceEntry.FullName)} uses unsafe POSIX file type {DescribeUnixFileType(unixFileType)} in external attributes.");
        }

        var dosAttributes = externalAttributes & DosAttributeMask;
        var unsafeDosAttributes = dosAttributes & ~DosArchiveAttribute;
        if (unsafeDosAttributes != 0)
        {
            throw new InvalidOperationException(
                $"ZIP entry {PackageNormalizeDiagnostics.FormatEntryName(sourceEntry.FullName)} uses unsafe DOS attributes 0x{unsafeDosAttributes:X2}.");
        }
    }

    private static string DescribeUnixFileType(int fileType)
    {
        return fileType switch
        {
            UnixFifoFileType => "fifo",
            UnixCharacterDeviceFileType => "character-device",
            UnixDirectoryFileType => "directory",
            UnixBlockDeviceFileType => "block-device",
            UnixSymlinkFileType => "symlink",
            UnixSocketFileType => "socket",
            _ => $"0x{fileType:X4}",
        };
    }

    private static string ReadXmlEntryText(
        ZipArchiveEntry sourceEntry,
        Stream sourceEntryStream,
        PackageNormalizeLimits limits,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(sourceEntryStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
        var buffer = new char[4096];
        var builder = new StringBuilder();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var charsRead = reader.Read(buffer, 0, buffer.Length);
            if (charsRead == 0)
                return builder.ToString();

            if (builder.Length > limits.MaxXmlTextChars - charsRead)
            {
                throw new InvalidOperationException(
                    $"XML ZIP entry {PackageNormalizeDiagnostics.FormatEntryName(sourceEntry.FullName)} exceeds the text limit of {limits.MaxXmlTextChars} characters.");
            }

            builder.Append(buffer, 0, charsRead);
        }
    }

    private static void CopyEntry(
        Stream sourceEntryStream,
        Stream destinationEntryStream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytesRead = sourceEntryStream.Read(buffer, 0, buffer.Length);
            if (bytesRead == 0)
                return;

            destinationEntryStream.Write(buffer, 0, bytesRead);
        }
    }

    private static void TryDeleteFile(string path, IList<string>? warnings, Action<string> deleteFile)
    {
        _ = PackageNormalizeRewriteFile.TryDeleteFile(
            path,
            ex => warnings?.Add(PackageNormalizeDiagnostics.FormatCleanupWarning(path, ex)),
            deleteFile);
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
                    $"ZIP entry {PackageNormalizeDiagnostics.FormatEntryName(sourceEntry.FullName)} exceeds the per-entry inflated size limit of {_limits.MaxEntryUncompressedBytes} bytes.");
            }

            if (_totalBytesRead > _limits.MaxTotalUncompressedBytes - bytesRead)
            {
                throw new InvalidOperationException(
                    $"ZIP entry {PackageNormalizeDiagnostics.FormatEntryName(sourceEntry.FullName)} makes actual inflated package size exceed the limit of {_limits.MaxTotalUncompressedBytes} bytes.");
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

        private readonly CancellationToken _cancellationToken;

        internal BudgetedEntryReadStream(
            Stream inner,
            ZipArchiveEntry sourceEntry,
            PackageNormalizeReadBudget readBudget,
            CancellationToken cancellationToken)
        {
            _inner = inner;
            _sourceEntry = sourceEntry;
            _readBudget = readBudget;
            _cancellationToken = cancellationToken;
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
            _cancellationToken.ThrowIfCancellationRequested();
            var bytesRead = _inner.Read(buffer, offset, count);
            TrackBytesRead(bytesRead);
            return bytesRead;
        }

        public override int Read(Span<byte> buffer)
        {
            _cancellationToken.ThrowIfCancellationRequested();
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
        PackageNormalizeLimits limits,
        CancellationToken cancellationToken)
    {
        var readBudget = new PackageNormalizeReadBudget(limits);
        foreach (var sourceEntry in sourceArchive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!NeedsXmlReferenceRewrite(sourceEntry.FullName))
                continue;

            using var rawSourceEntryStream = sourceEntry.Open();
            using var sourceEntryStream = new BudgetedEntryReadStream(rawSourceEntryStream, sourceEntry, readBudget, cancellationToken);
            var content = ReadXmlEntryText(sourceEntry, sourceEntryStream, limits, cancellationToken);
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
