using System.IO.Compression;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeIndex.Archives;
using CodeIndex.Database;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

internal static partial class ExportImportCommandRunner
{
    private static bool TryValidateImportArchiveEntries(
        ZipArchive archive,
        out ZipArchiveEntry manifestEntry,
        out ZipArchiveEntry? databaseEntry,
        out string phase,
        out string errorCode,
        out string message)
    {
        manifestEntry = null!;
        databaseEntry = null!;
        phase = PhaseOpenArchive;
        errorCode = string.Empty;
        message = string.Empty;

        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (!ZipArchiveSafetyPolicy.TryNormalizeRelativeEntryName(entry.FullName, out var normalizedEntryName, out var entryNameFailureReason))
            {
                errorCode = "import_archive_unsafe_entry_name";
                message = $"archive contains unsafe entry {ConsoleUi.FormatBoundedValue(entry.FullName)}: ZIP entry name {entryNameFailureReason}; expected only {FormatExpectedImportArchiveEntryNames()}.";
                return false;
            }

            if (!string.Equals(normalizedEntryName, entry.FullName, StringComparison.Ordinal))
            {
                errorCode = "import_archive_noncanonical_entry_name";
                message = $"archive contains non-canonical entry {ConsoleUi.FormatBoundedValue(entry.FullName)} that normalizes to {ConsoleUi.FormatBoundedValue(normalizedEntryName)}; expected only {FormatExpectedImportArchiveEntryNames()}.";
                return false;
            }

            if (!IsExpectedImportArchiveEntryName(entry.FullName))
            {
                errorCode = "import_archive_unexpected_entry";
                message = $"archive contains unexpected entry {ConsoleUi.FormatBoundedValue(entry.FullName)}; expected only {FormatExpectedImportArchiveEntryNames()}.";
                return false;
            }

            if (!ZipArchiveSafetyPolicy.TryAddUniqueEntryName(entries, entry.FullName, entry))
            {
                phase = GetImportArchiveEntryPhase(entry.FullName);
                errorCode = "import_archive_duplicate_entry";
                message = $"archive contains duplicate entry {ConsoleUi.FormatBoundedValue(entry.FullName)}.";
                return false;
            }
        }

        if (!entries.TryGetValue(ManifestEntryName, out var foundManifestEntry))
        {
            phase = PhaseManifest;
            errorCode = "import_manifest_missing";
            message = $"archive is missing {ManifestEntryName}.";
            return false;
        }

        manifestEntry = foundManifestEntry;
        entries.TryGetValue(DatabaseEntryName, out databaseEntry);
        return true;
    }

    private static bool IsExpectedImportArchiveEntryName(string name)
        => Array.Exists(ExpectedImportArchiveEntryNames, expected => string.Equals(expected, name, StringComparison.Ordinal));

    private static string GetImportArchiveEntryPhase(string name)
        => string.Equals(name, ManifestEntryName, StringComparison.Ordinal)
            ? PhaseManifest
            : string.Equals(name, DatabaseEntryName, StringComparison.Ordinal)
                ? PhaseDatabaseEntry
                : PhaseOpenArchive;

    private static string FormatExpectedImportArchiveEntryNames()
        => string.Join(", ", ExpectedImportArchiveEntryNames.Select(name => $"`{name}`"));

    internal static string FormatImportManifestReadException(Exception ex)
        => CommandErrorWriter.FormatSanitizedException(ex);

    private static bool TryReadManifest(ZipArchiveEntry manifestEntry, JsonSerializerOptions jsonOptions, out ExportManifest manifest, out string message, CancellationToken cancellationToken)
    {
        if (!ExportImportManifestCodec.TryValidateEntrySize(manifestEntry, out message))
        {
            manifest = null!;
            return false;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = manifestEntry.Open();
            using var manifestBytes = new MemoryStream((int)Math.Min(Math.Max(manifestEntry.Length, 0), MaxImportManifestBytes));
            CopyToWithLimit(stream, manifestBytes, MaxImportManifestBytes, ManifestEntryName, cancellationToken);
            manifestBytes.Position = 0;
            cancellationToken.ThrowIfCancellationRequested();
            return ExportImportManifestCodec.TryDeserialize(
                manifestBytes.GetBuffer().AsSpan(0, (int)manifestBytes.Length),
                jsonOptions,
                out manifest,
                out message);
        }
        catch (InvalidDataException ex)
        {
            manifest = null!;
            message = FormatImportManifestReadException(ex);
            return false;
        }
    }

    private static bool TryValidateImportedManifest(
        ExportManifest manifest,
        string dbPath,
        out string message,
        out string phase,
        CancellationToken cancellationToken = default)
    {
        phase = PhaseSha256;
        var actualSha256 = ComputeSha256(dbPath, cancellationToken);
        if (!string.Equals(manifest.DatabaseSha256, actualSha256, StringComparison.OrdinalIgnoreCase))
        {
            message = "database_sha256 does not match codeindex.db";
            return false;
        }

        phase = PhaseSqliteValidate;
        int actualUserVersion;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = new SqliteConnection(CreateUnpooledConnectionString(dbPath));
            connection.Open();
            actualUserVersion = ReadSqliteUserVersion(connection);
            if (!TryValidateManifestCount(manifest.FileCount, connection, "files", "file_count", out message, cancellationToken)
                || !TryValidateManifestCount(manifest.ChunkCount, connection, "chunks", "chunk_count", out message, cancellationToken)
                || !TryValidateManifestCount(manifest.SymbolCount, connection, "symbols", "symbol_count", out message, cancellationToken)
                || !TryValidateManifestCount(manifest.ReferenceCount, connection, "symbol_references", "reference_count", out message, cancellationToken))
            {
                return false;
            }
        }
        catch (SqliteException ex)
        {
            message = $"could not validate codeindex.db manifest metadata ({CommandErrorWriter.FormatSanitizedException(ex)})";
            return false;
        }

        if (actualUserVersion != manifest.UserVersion)
        {
            message = $"manifest user_version `{manifest.UserVersion}` does not match codeindex.db user_version `{actualUserVersion}`";
            return false;
        }

        phase = string.Empty;
        message = string.Empty;
        return true;
    }

    private static int ReadSqliteUserVersion(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static bool TryValidateManifestCount(long? expected, SqliteConnection connection, string tableName, string fieldName, out string message, CancellationToken cancellationToken)
    {
        if (expected == null)
        {
            message = string.Empty;
            return true;
        }

        var actual = ReadTableCount(connection, tableName, cancellationToken);
        if (actual != expected.Value)
        {
            message = $"manifest {fieldName} `{expected.Value}` does not match codeindex.db {tableName} count `{actual}`";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static long ReadTableCount(SqliteConnection connection, string tableName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = tableName switch
        {
            "files" => "SELECT COUNT(*) FROM files",
            "chunks" => "SELECT COUNT(*) FROM chunks",
            "symbols" => "SELECT COUNT(*) FROM symbols",
            "symbol_references" => "SELECT COUNT(*) FROM symbol_references",
            _ => throw new ArgumentOutOfRangeException(nameof(tableName), tableName, "Unsupported manifest count table."),
        };
        var count = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        cancellationToken.ThrowIfCancellationRequested();
        return count;
    }

    private static string? ReadMetaString(
        SqliteConnection connection,
        string key,
        SqliteTransaction? transaction = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT value FROM codeindex_meta WHERE key = @key LIMIT 1";
        SqliteCommandPolicy.Add(cmd, "@key", key);
        return cmd.ExecuteScalar() as string;
    }

    private static int? ReadMetaInt(SqliteConnection connection, string key)
    {
        var value = ReadMetaString(connection, key);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : null;
    }

    private static long? ReadMetaLong(SqliteConnection connection, string key)
    {
        var value = ReadMetaString(connection, key);
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : null;
    }

    private static bool? ReadMetaBool(SqliteConnection connection, string key)
    {
        var value = ReadMetaString(connection, key);
        return bool.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string[]? ReadArchiveIncompleteReasons(SqliteConnection connection)
    {
        var rawReasons = ReadMetaString(connection, DbContext.IndexIncompleteReasonsMetaKey);
        if (string.IsNullOrWhiteSpace(rawReasons)
            || Encoding.UTF8.GetByteCount(rawReasons) > MaxImportManifestBytes)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(
                rawReasons,
                new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            var reasons = new List<string>(MaxArchiveIncompleteReasons);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var totalChars = 0;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || reasons.Count >= MaxArchiveIncompleteReasons)
                    return null;
                var reason = item.GetString();
                if (string.IsNullOrWhiteSpace(reason)
                    || reason.Length > MaxArchiveIncompleteReasonChars)
                {
                    return null;
                }
                if (!seen.Add(reason))
                    continue;
                if (totalChars + reason.Length > MaxArchiveIncompleteReasonsTotalChars)
                    return null;
                reasons.Add(reason);
                totalChars += reason.Length;
            }
            return reasons.ToArray();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private readonly record struct UnknownExtensionFileSample(string[]? Files, int? Count, int? Limit, bool? Truncated);

    private static UnknownExtensionFileSample ReadUnknownExtensionFileSample(SqliteConnection connection)
    {
        var json = ReadMetaString(connection, DbContext.UnknownExtensionFilePathsMetaKey);
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaxImportManifestBytes)
            return new(null, null, null, null);

        try
        {
            var jsonBytes = Encoding.UTF8.GetBytes(json);
            var reader = new Utf8JsonReader(
                jsonBytes,
                new JsonReaderOptions { MaxDepth = ManifestUnknownExtensionJsonDepth });
            if (!reader.Read())
                return new(null, null, null, null);
            if (reader.TokenType == JsonTokenType.Null)
            {
                if (reader.Read())
                    return new(null, null, null, null);

                return new(null, 0, ManifestUnknownExtensionFileLimit, false);
            }
            if (reader.TokenType != JsonTokenType.StartArray)
                return new(null, null, null, null);

            var sample = new List<string>(ManifestUnknownExtensionFileLimit);
            var decodedItems = 0;
            var truncated = false;
            var completed = false;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    completed = true;
                    break;
                }
                if (reader.TokenType != JsonTokenType.String)
                    return new(null, null, null, null);

                decodedItems++;
                if (decodedItems > ManifestUnknownExtensionDecodedItemLimit)
                {
                    truncated = true;
                    break;
                }

                var path = reader.GetString();
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                if (sample.Count >= ManifestUnknownExtensionFileLimit)
                {
                    truncated = true;
                    break;
                }

                sample.Add(path.Length <= ManifestUnknownExtensionPathCharLimit
                    ? path
                    : path[..ManifestUnknownExtensionPathCharLimit]);
            }

            if (!completed && !truncated)
                return new(null, null, null, null);
            if (completed && reader.Read())
                return new(null, null, null, null);
            if (sample.Count == 0)
                return new(null, 0, ManifestUnknownExtensionFileLimit, false);

            return new(sample.ToArray(), sample.Count, ManifestUnknownExtensionFileLimit, truncated);
        }
        catch (JsonException)
        {
            return new(null, null, null, null);
        }
    }

    internal static bool TryValidateDatabaseEntrySize(long uncompressedLength, long compressedLength, out string message)
    {
        if (uncompressedLength < 0 || compressedLength < 0)
        {
            message = "archive codeindex.db size metadata is invalid";
            return false;
        }

        if (uncompressedLength > MaxImportDatabaseBytes)
        {
            message = $"archive codeindex.db is too large: {ConsoleUi.FormatBytes(uncompressedLength)} uncompressed exceeds the import limit of {ConsoleUi.FormatBytes(MaxImportDatabaseBytes)}";
            return false;
        }

        if (compressedLength > MaxImportDatabaseBytes)
        {
            message = $"archive codeindex.db is too large: {ConsoleUi.FormatBytes(compressedLength)} compressed exceeds the import limit of {ConsoleUi.FormatBytes(MaxImportDatabaseBytes)}";
            return false;
        }

        if (uncompressedLength > 0 && compressedLength == 0)
        {
            message = "archive codeindex.db compression metadata is invalid: non-empty entry has zero compressed bytes";
            return false;
        }

        if (compressedLength > 0 && uncompressedLength > compressedLength * MaxImportDatabaseCompressionRatio)
        {
            message = $"archive codeindex.db compression ratio exceeds the import limit of {MaxImportDatabaseCompressionRatio}:1";
            return false;
        }

        message = string.Empty;
        return true;
    }

    internal static void ExtractDatabaseEntryToFile(ZipArchiveEntry dbEntry, string destinationPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var source = dbEntry.Open();
        using var target = DataDirectorySecurity.OpenPrivateFileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        EnsureImportStagingFilesPrivate(destinationPath);
        CopyToWithLimit(source, target, MaxImportDatabaseBytes, cancellationToken);
        EnsureImportStagingFilesPrivate(destinationPath);
    }

    internal static void EnsureImportStagingFilesPrivate(string databasePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
            {
                if (!File.Exists(path))
                    continue;

                DataDirectorySecurity.ApplyPrivateFileMode(path);
                var mode = File.GetUnixFileMode(path) & DataDirectorySecurity.PermissionBits;
                if (mode != DataDirectorySecurity.PrivateFileMode)
                {
                    throw new UnauthorizedAccessException(
                        "import staging database permissions could not be restricted to the current user");
                }
            }
        }

        ImportStagingFilesHardenedForTesting?.Invoke(databasePath);
    }

    internal static Action<string>? ImportStagingFilesHardenedForTesting { get; set; }

    internal static long CopyToWithLimit(
        Stream source,
        Stream target,
        long maxBytes,
        CancellationToken cancellationToken = default)
        => CopyToWithLimit(source, target, maxBytes, DatabaseEntryName, cancellationToken);

    internal static long CopyToExactLength(
        Stream source,
        Stream target,
        long expectedBytes,
        string entryName,
        CancellationToken cancellationToken = default)
    {
        if (expectedBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedBytes), expectedBytes, "Expected byte length must be non-negative.");

        var buffer = new byte[ImportCopyBufferSize];
        long totalBytes = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytesRead = source.Read(buffer, 0, buffer.Length);
            if (bytesRead == 0)
                break;

            if (totalBytes > expectedBytes - bytesRead)
                throw new InvalidDataException($"archive {entryName} source grew beyond the expected snapshot length of {ConsoleUi.FormatBytes(expectedBytes)}.");

            target.Write(buffer, 0, bytesRead);
            totalBytes += bytesRead;
        }

        if (totalBytes != expectedBytes)
            throw new EndOfStreamException($"archive {entryName} source ended after {ConsoleUi.FormatBytes(totalBytes)}; expected {ConsoleUi.FormatBytes(expectedBytes)}.");

        return totalBytes;
    }

    internal static long CopyToWithLimit(
        Stream source,
        Stream target,
        long maxBytes,
        CancellationToken cancellationToken,
        IProgress<long>? progress = null)
        => CopyToWithLimit(source, target, maxBytes, DatabaseEntryName, cancellationToken, progress);

    private static long CopyToWithLimit(
        Stream source,
        Stream target,
        long maxBytes,
        string entryName,
        CancellationToken cancellationToken = default,
        IProgress<long>? progress = null)
    {
        var buffer = new byte[ImportCopyBufferSize];
        long totalBytes = 0;
        int bytesRead;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bytesRead = source.Read(buffer, 0, buffer.Length);
            if (bytesRead == 0)
                break;

            if (totalBytes > maxBytes - bytesRead)
                throw new InvalidDataException($"archive {entryName} exceeds the import limit of {ConsoleUi.FormatBytes(maxBytes)}.");

            target.Write(buffer, 0, bytesRead);
            totalBytes += bytesRead;
            progress?.Report(totalBytes);
        }

        return totalBytes;
    }

    private static void RewriteImportedProjectRoot(string dbPath, string projectRoot)
    {
        using var connection = new SqliteConnection(CreateUnpooledConnectionString(dbPath));
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO codeindex_meta(key, value)
            VALUES ('indexed_project_root', @projectRoot)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        SqliteCommandPolicy.Add(cmd, "@projectRoot", Path.GetFullPath(projectRoot));
        cmd.ExecuteNonQuery();
    }

    internal static string ResolveImportTargetProjectRoot(string fullDbPath)
    {
        var normalizedDbPath = Path.GetFullPath(fullDbPath);
        var dbDirectory = Path.GetDirectoryName(normalizedDbPath);
        if (!string.IsNullOrWhiteSpace(dbDirectory)
            && string.Equals(Path.GetFileName(normalizedDbPath), "codeindex.db", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(dbDirectory), ".cdidx", StringComparison.OrdinalIgnoreCase))
        {
            var siblingRoot = Path.GetDirectoryName(dbDirectory);
            if (!string.IsNullOrWhiteSpace(siblingRoot))
                return Path.GetFullPath(siblingRoot);
        }

        return Path.GetFullPath(Environment.CurrentDirectory);
    }

    private static string FormatImportSuccessMessage(string prefix, bool prunePaths, string importTargetProjectRoot)
        => prunePaths
            ? $"{prefix}; pruned paths to project root {importTargetProjectRoot}"
            : prefix;

}
