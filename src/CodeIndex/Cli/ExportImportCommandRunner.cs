using System.IO.Compression;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeIndex.Database;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Cli;

internal static class ExportImportCommandRunner
{
    private const string ManifestEntryName = "manifest.json";
    private const string DatabaseEntryName = "codeindex.db";
    internal const int MaxImportManifestBytes = 64 * 1024;
    internal const int MaxImportManifestJsonDepth = 16;
    internal const long MaxImportDatabaseBytes = 8L * 1024 * 1024 * 1024;
    private const int ImportCopyBufferSize = 81920;
    private static readonly DateTimeOffset DeterministicZipTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static int RunExport(string[] args, JsonSerializerOptions jsonOptions, string appVersion)
    {
        if (args.Length > 0 && args[0] == "ctags")
            return RunExportCtags(args[1..]);

        return RunExportArchive(args, jsonOptions, appVersion);
    }

    public static int RunImport(string[] args, JsonSerializerOptions jsonOptions)
    {
        string? archivePath = null;
        string? dbPath = null;
        var wantsJson = false;
        var prunePaths = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--json")
            {
                wantsJson = true;
                continue;
            }
            if (arg == "--prune-paths")
            {
                prunePaths = true;
                continue;
            }

            if (TryReadValueOption(args, ref i, "--db", arg, out var dbValue, out var dbError))
            {
                if (dbError != null)
                    return WriteError(dbError, "use `cdidx import <archive> --db <path>`.", "cdidx import <archive> [--db <path>] [--json]");
                dbPath = dbValue;
                continue;
            }

            if (arg.StartsWith("-", StringComparison.Ordinal))
                return WriteError($"unknown import option `{arg}`.", "use `cdidx import <archive> [--db <path>]`.", "cdidx import <archive> [--db <path>] [--prune-paths] [--json]");

            if (archivePath != null)
                return WriteError($"import accepts exactly one archive path, got extra `{arg}`.", "remove the extra argument.", "cdidx import <archive> [--db <path>] [--json]");
            archivePath = arg;
        }

        if (string.IsNullOrWhiteSpace(archivePath))
            return WriteError("import requires an archive path.", "pass an archive produced by `cdidx export <archive>`.", "cdidx import <archive> [--db <path>] [--prune-paths] [--json]");

        dbPath ??= DbPathResolver.ResolveForQuery(Environment.CurrentDirectory, explicitDbPath: null, explicitDataDir: null).DbPath;
        var fullDbPath = Path.GetFullPath(DbPathResolver.NormalizeDbPath(dbPath));
        var dbDirectory = Path.GetDirectoryName(fullDbPath);
        if (string.IsNullOrWhiteSpace(dbDirectory))
            return WriteError($"could not resolve destination DB directory for `{dbPath}`.", "pass an explicit `--db <path>`.", "cdidx import <archive> [--db <path>] [--json]");

        var tempPath = Path.Combine(dbDirectory, $".codeindex-import-{Guid.NewGuid():N}.db");
        try
        {
            Directory.CreateDirectory(dbDirectory);
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                var manifestEntry = archive.GetEntry(ManifestEntryName);
                if (manifestEntry == null)
                    return WriteError("archive is missing manifest.json.", "use an archive produced by `cdidx export <archive>`.", "cdidx import <archive> [--db <path>] [--json]");
                if (!TryReadManifest(manifestEntry, jsonOptions, out var manifest, out var manifestError))
                    return WriteError($"archive manifest is invalid: {manifestError}.", "use an archive produced by `cdidx export <archive>`.", "cdidx import <archive> [--db <path>] [--json]");
                if (!TryValidateManifestHeader(manifest, out var manifestHeaderError))
                    return WriteError($"archive manifest is invalid: {manifestHeaderError}.", "re-export from a compatible CodeIndex database.", "cdidx import <archive> [--db <path>] [--json]");

                var dbEntry = archive.GetEntry(DatabaseEntryName);
                if (dbEntry == null)
                    return WriteError("archive is missing codeindex.db.", "use an archive produced by `cdidx export <archive>`.", "cdidx import <archive> [--db <path>] [--json]");
                if (!TryValidateDatabaseEntrySize(dbEntry.Length, dbEntry.CompressedLength, out var sizeValidationMessage))
                    return WriteError(sizeValidationMessage, "re-export a smaller CodeIndex database or rebuild a smaller index.", "cdidx import <archive> [--db <path>] [--prune-paths] [--json]");

                ExtractDatabaseEntryToFile(dbEntry, tempPath);

                if (!TryValidateImportedManifest(manifest, tempPath, out var manifestValidationMessage))
                    return WriteError($"archive manifest mismatch: {manifestValidationMessage}.", "re-export from a compatible CodeIndex database.", "cdidx import <archive> [--db <path>] [--prune-paths] [--json]");
            }

            if (!DbContext.TryValidateExistingCodeIndexDb(tempPath, out var validationMessage, out _))
                return WriteError($"archive database is invalid: {validationMessage}.", "re-export from a compatible CodeIndex database.", "cdidx import <archive> [--db <path>] [--prune-paths] [--json]");
            SqliteConnection.ClearAllPools();

            if (prunePaths)
            {
                RewriteImportedProjectRoot(tempPath, Environment.CurrentDirectory);
                SqliteConnection.ClearAllPools();
            }

            ReplaceImportedDatabase(tempPath, fullDbPath);
            if (wantsJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(new ImportResult("1", fullDbPath, prunePaths), jsonOptions));
            }
            else
            {
                Console.WriteLine($"Imported CodeIndex database to {fullDbPath}");
            }
            return CommandExitCodes.Success;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or SqliteException)
        {
            return WriteError($"import failed ({CommandErrorWriter.FormatSanitizedException(ex)}).", "check the archive path and destination database permissions.", "cdidx import <archive> [--db <path>] [--prune-paths] [--json]");
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            try { DeleteSqliteSidecars(tempPath); } catch { }
        }
    }

    private static int RunExportArchive(string[] args, JsonSerializerOptions jsonOptions, string appVersion)
    {
        string? outputPath = null;
        string? dbPath = null;
        var wantsJson = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--json")
            {
                wantsJson = true;
                continue;
            }

            if (TryReadValueOption(args, ref i, "--db", arg, out var dbValue, out var dbError))
            {
                if (dbError != null)
                    return WriteError(dbError, "use `cdidx export <archive> --db <path>`.", "cdidx export <archive> [--db <path>] [--json]");
                dbPath = dbValue;
                continue;
            }

            if (arg.StartsWith("-", StringComparison.Ordinal))
                return WriteError($"unknown export option `{arg}`.", "use `cdidx export <archive> [--db <path>]` or `cdidx export ctags`.", "cdidx export <archive> [--db <path>] [--json]");

            if (outputPath != null)
                return WriteError($"export accepts exactly one archive path, got extra `{arg}`.", "remove the extra argument.", "cdidx export <archive> [--db <path>] [--json]");
            outputPath = arg;
        }

        if (string.IsNullOrWhiteSpace(outputPath))
            return WriteError("export requires an output archive path.", "pass a destination such as `codeindex.cdidx.zip`, or use `cdidx export ctags`.", "cdidx export <archive> [--db <path>] [--json]");

        dbPath ??= DbPathResolver.ResolveForQuery(Environment.CurrentDirectory, explicitDbPath: null, explicitDataDir: null).DbPath;
        var normalizedDbPath = DbPathResolver.NormalizeDbPath(dbPath);
        if (!DbContext.TryValidateExistingCodeIndexDb(normalizedDbPath, out var validationMessage, out _))
            return WriteError(validationMessage, "run `cdidx index <projectPath>` first or pass `--db <path>`.", "cdidx export <archive> [--db <path>] [--json]");

        var fullSourceDbPath = Path.GetFullPath(normalizedDbPath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        if (IsDatabaseOrSqliteSidecarPath(fullOutputPath, fullSourceDbPath))
        {
            return WriteError("export archive path must not be the source database or a SQLite sidecar.", "choose a separate archive path, for example `codeindex.cdidx.zip`.", "cdidx export <archive> [--db <path>] [--json]");
        }

        var snapshotPath = Path.Combine(Path.GetTempPath(), $"codeindex-export-{Guid.NewGuid():N}.db");
        try
        {
            var outputDirectory = Path.GetDirectoryName(fullOutputPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
                Directory.CreateDirectory(outputDirectory);

            CreateDatabaseSnapshot(normalizedDbPath, snapshotPath);
            ExportManifest manifest;
            using (var snapshotConnection = new SqliteConnection(CreateUnpooledConnectionString(snapshotPath)))
            {
                snapshotConnection.Open();
                manifest = BuildManifest(snapshotConnection, appVersion);
            }
            SqliteConnection.ClearAllPools();
            manifest = manifest with { DatabaseSha256 = ComputeSha256(snapshotPath) };
            WriteExportArchiveFile(fullOutputPath, snapshotPath, manifest, jsonOptions);

            if (wantsJson)
                Console.WriteLine(JsonSerializer.Serialize(new ExportArchiveResult("1", fullOutputPath, fullSourceDbPath), jsonOptions));
            else
                Console.WriteLine($"Exported CodeIndex archive to {fullOutputPath}");
            return CommandExitCodes.Success;
        }
        catch (Exception ex)
        {
            return WriteError($"export failed ({CommandErrorWriter.FormatSanitizedException(ex)}).", "check the database and output archive paths.", "cdidx export <archive> [--db <path>] [--json]");
        }
        finally
        {
            try { if (File.Exists(snapshotPath)) File.Delete(snapshotPath); } catch { }
            try { DeleteSqliteSidecars(snapshotPath); } catch { }
        }
    }

    private static int RunExportCtags(string[] args)
    {
        var outputPath = "tags";
        string? dbPath = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (TryReadValueOption(args, ref i, "--output", arg, out var outputValue, out var outputError))
            {
                if (outputError != null)
                    return WriteError(outputError, "use `cdidx export ctags --output tags`.", "cdidx export ctags [--output <path>] [--db <path>]");
                outputPath = outputValue!;
                continue;
            }

            if (TryReadValueOption(args, ref i, "--db", arg, out var dbValue, out var dbError))
            {
                if (dbError != null)
                    return WriteError(dbError, "use `cdidx export ctags --db <path>`.", "cdidx export ctags [--output <path>] [--db <path>]");
                dbPath = dbValue;
                continue;
            }

            return WriteError($"unknown ctags export option `{arg}`.", "use `--output <path>` or `--db <path>`.", "cdidx export ctags [--output <path>] [--db <path>]");
        }

        dbPath ??= DbPathResolver.ResolveForQuery(Environment.CurrentDirectory, explicitDbPath: null, explicitDataDir: null).DbPath;
        var normalizedDbPath = DbPathResolver.NormalizeDbPath(dbPath);
        var fullSourceDbPath = Path.GetFullPath(normalizedDbPath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        if (IsDatabaseOrSqliteSidecarPath(fullOutputPath, fullSourceDbPath))
        {
            return WriteError("ctags output path must not be the source database or a SQLite sidecar.", "choose a separate tags path, for example `tags`.", "cdidx export ctags [--output <path>] [--db <path>]");
        }

        if (!DbContext.TryValidateExistingCodeIndexDb(normalizedDbPath, out var validationMessage, out _))
            return WriteError(validationMessage, "run `cdidx index <projectPath>` first or pass `--db <path>`.", "cdidx export ctags [--output <path>] [--db <path>]");

        try
        {
            using var db = new DbContext(normalizedDbPath);
            db.TryMigrateForRead();
            var outputDirectory = Path.GetDirectoryName(fullOutputPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
                Directory.CreateDirectory(outputDirectory);

            WriteCtagsFile(fullOutputPath, writer =>
            {
                writer.WriteLine("!_TAG_FILE_FORMAT\t2\t/extended format/");
                writer.WriteLine("!_TAG_FILE_SORTED\t1\t/0=unsorted, 1=sorted, 2=foldcase/");

                using var cmd = db.Connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT s.name, f.path, COALESCE(s.start_line, s.line, 1), s.kind
                    FROM symbols s
                    JOIN files f ON s.file_id = f.id
                    WHERE s.name IS NOT NULL AND s.name != ''
                    ORDER BY s.name COLLATE NOCASE, f.path, COALESCE(s.start_line, s.line, 1)";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var name = SanitizeCtagsField(reader.GetString(0));
                    var path = SanitizeCtagsField(reader.GetString(1));
                    var line = Math.Max(1, reader.GetInt32(2));
                    var kind = SanitizeCtagsField(reader.GetString(3));
                    writer.WriteLine($"{name}\t{path}\t{line};\"\tkind:{kind}\tline:{line}");
                }
            });

            Console.WriteLine($"Exported ctags to {fullOutputPath}");
            return CommandExitCodes.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return WriteError($"ctags export failed ({CommandErrorWriter.FormatSanitizedException(ex)}).", "check the database and output paths.", "cdidx export ctags [--output <path>] [--db <path>]");
        }
    }

    private static ExportManifest BuildManifest(SqliteConnection connection, string appVersion)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        var userVersion = Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        cmd.CommandText = "SELECT value FROM codeindex_meta WHERE key = 'indexed_project_root' LIMIT 1";
        var projectRoot = cmd.ExecuteScalar() as string;
        cmd.CommandText = "SELECT value FROM codeindex_meta WHERE key = 'indexed_head_sha' LIMIT 1";
        var indexedHead = cmd.ExecuteScalar() as string;
        return new ExportManifest("1", appVersion, userVersion, projectRoot, indexedHead, string.Empty);
    }

    private static void AddTextEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
        entry.LastWriteTime = DeterministicZipTimestamp;
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    internal static void WriteExportArchiveFile(string outputPath, string snapshotPath, ExportManifest manifest, JsonSerializerOptions jsonOptions)
    {
        var fullOutputPath = Path.GetFullPath(outputPath);
        AtomicFileWriter.Write(
            fullOutputPath,
            stream =>
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
                AddTextEntry(archive, ManifestEntryName, JsonSerializer.Serialize(manifest, jsonOptions));
                var dbEntry = archive.CreateEntry(DatabaseEntryName, CompressionLevel.SmallestSize);
                dbEntry.LastWriteTime = DeterministicZipTimestamp;
                using var source = File.OpenRead(snapshotPath);
                using var target = dbEntry.Open();
                source.CopyTo(target);
            });
    }

    internal static void WriteCtagsFile(string outputPath, Action<TextWriter> writeContents)
    {
        ArgumentNullException.ThrowIfNull(writeContents);

        var fullOutputPath = Path.GetFullPath(outputPath);
        AtomicFileWriter.Write(
            fullOutputPath,
            stream =>
            {
                using var writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 1024,
                    leaveOpen: true);
                writeContents(writer);
            });
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool TryReadManifest(ZipArchiveEntry manifestEntry, JsonSerializerOptions jsonOptions, out ExportManifest manifest, out string message)
    {
        if (!TryValidateManifestEntrySize(manifestEntry, out message))
        {
            manifest = null!;
            return false;
        }

        try
        {
            using var stream = manifestEntry.Open();
            using var manifestBytes = new MemoryStream((int)Math.Min(Math.Max(manifestEntry.Length, 0), MaxImportManifestBytes));
            CopyToWithLimit(stream, manifestBytes, MaxImportManifestBytes, ManifestEntryName);
            manifestBytes.Position = 0;
            var parsedManifest = JsonSerializer.Deserialize<ExportManifest>(manifestBytes, CreateImportManifestJsonOptions(jsonOptions));
            if (parsedManifest == null)
            {
                manifest = null!;
                message = "manifest.json did not contain an object";
                return false;
            }

            manifest = parsedManifest;
            message = string.Empty;
            return true;
        }
        catch (InvalidDataException ex)
        {
            manifest = null!;
            message = ex.Message;
            return false;
        }
        catch (JsonException ex)
        {
            manifest = null!;
            message = IsJsonDepthLimitException(ex)
                ? $"manifest.json exceeds the JSON depth limit of {MaxImportManifestJsonDepth}"
                : "manifest.json is not valid export manifest JSON";
            return false;
        }
        catch (NotSupportedException)
        {
            manifest = null!;
            message = "manifest.json contains unsupported export manifest JSON";
            return false;
        }
    }

    private static bool TryValidateManifestEntrySize(ZipArchiveEntry manifestEntry, out string message)
    {
        if (manifestEntry.Length < 0 || manifestEntry.CompressedLength < 0)
        {
            message = "archive manifest.json size metadata is invalid";
            return false;
        }

        if (manifestEntry.Length > MaxImportManifestBytes)
        {
            message = $"archive manifest.json is too large: {ConsoleUi.FormatBytes(manifestEntry.Length)} uncompressed exceeds the import limit of {ConsoleUi.FormatBytes(MaxImportManifestBytes)}";
            return false;
        }

        if (manifestEntry.CompressedLength > MaxImportManifestBytes)
        {
            message = $"archive manifest.json is too large: {ConsoleUi.FormatBytes(manifestEntry.CompressedLength)} compressed exceeds the import limit of {ConsoleUi.FormatBytes(MaxImportManifestBytes)}";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static JsonSerializerOptions CreateImportManifestJsonOptions(JsonSerializerOptions jsonOptions)
        => new(jsonOptions) { MaxDepth = MaxImportManifestJsonDepth };

    private static bool IsJsonDepthLimitException(JsonException ex)
        => ex.Message.Contains("depth", StringComparison.OrdinalIgnoreCase);

    private static bool TryValidateManifestHeader(ExportManifest manifest, out string message)
    {
        if (!string.Equals(manifest.FormatVersion, "1", StringComparison.Ordinal))
        {
            message = $"unsupported format_version `{manifest.FormatVersion}`";
            return false;
        }

        if (manifest.UserVersion < 0 || (manifest.UserVersion & ~DbContext.CurrentSchemaVersion) != 0)
        {
            message = $"unsupported user_version `{manifest.UserVersion}`";
            return false;
        }

        if (!IsSha256Hex(manifest.DatabaseSha256))
        {
            message = "database_sha256 is missing or invalid";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static bool TryValidateImportedManifest(ExportManifest manifest, string dbPath, out string message)
    {
        var actualSha256 = ComputeSha256(dbPath);
        if (!string.Equals(manifest.DatabaseSha256, actualSha256, StringComparison.OrdinalIgnoreCase))
        {
            message = "database_sha256 does not match codeindex.db";
            return false;
        }

        var actualUserVersion = ReadSqliteUserVersion(dbPath);
        if (actualUserVersion != manifest.UserVersion)
        {
            message = $"manifest user_version `{manifest.UserVersion}` does not match codeindex.db user_version `{actualUserVersion}`";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static int ReadSqliteUserVersion(string dbPath)
    {
        using var connection = new SqliteConnection(CreateUnpooledConnectionString(dbPath));
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static bool IsSha256Hex(string? value)
    {
        if (value == null || value.Length != 64)
            return false;

        foreach (var ch in value)
        {
            if (!char.IsAsciiHexDigit(ch))
                return false;
        }

        return true;
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

        message = string.Empty;
        return true;
    }

    private static void ExtractDatabaseEntryToFile(ZipArchiveEntry dbEntry, string destinationPath)
    {
        using var source = dbEntry.Open();
        using var target = File.Open(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        CopyToWithLimit(source, target, MaxImportDatabaseBytes);
    }

    internal static long CopyToWithLimit(Stream source, Stream target, long maxBytes)
        => CopyToWithLimit(source, target, maxBytes, DatabaseEntryName);

    private static long CopyToWithLimit(Stream source, Stream target, long maxBytes, string entryName)
    {
        var buffer = new byte[ImportCopyBufferSize];
        long totalBytes = 0;
        int bytesRead;
        while ((bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (totalBytes > maxBytes - bytesRead)
                throw new InvalidDataException($"archive {entryName} exceeds the import limit of {ConsoleUi.FormatBytes(maxBytes)}.");

            target.Write(buffer, 0, bytesRead);
            totalBytes += bytesRead;
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
        cmd.Parameters.AddWithValue("@projectRoot", Path.GetFullPath(projectRoot));
        cmd.ExecuteNonQuery();
    }

    internal static void CreateDatabaseSnapshot(string sourceDbPath, string snapshotPath)
    {
        using var source = new SqliteConnection(CreateUnpooledConnectionString(sourceDbPath));
        using var destination = new SqliteConnection(CreateUnpooledConnectionString(snapshotPath));
        source.Open();
        destination.Open();
        DataDirectorySecurity.ApplyPrivateFileMode(snapshotPath);
        source.BackupDatabase(destination);
        DataDirectorySecurity.ApplyPrivateFileMode(snapshotPath);
    }

    private static string CreateUnpooledConnectionString(string dbPath)
        => new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ConnectionString;

    internal static void ReplaceImportedDatabase(string tempPath, string fullDbPath)
    {
        File.Move(tempPath, fullDbPath, overwrite: true);
        DataDirectorySecurity.ApplyPrivateFileMode(fullDbPath);
        DeleteSqliteSidecars(fullDbPath);
    }

    private static void DeleteSqliteSidecars(string dbPath)
    {
        TryDeleteFile(dbPath + "-wal");
        TryDeleteFile(dbPath + "-shm");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;

            if (DeleteSqliteSidecarForTesting != null)
                DeleteSqliteSidecarForTesting(path);
            else
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
        }
    }

    internal static Action<string>? DeleteSqliteSidecarForTesting { get; set; }

    private static bool IsSamePath(string left, string right)
        => string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsDatabaseOrSqliteSidecarPath(string path, string dbPath)
        => IsSamePath(path, dbPath)
            || IsSamePath(path, dbPath + "-wal")
            || IsSamePath(path, dbPath + "-shm");

    private static string SanitizeCtagsField(string value)
        => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    private static bool TryReadValueOption(string[] args, ref int index, string optionName, string arg, out string? value, out string? error)
    {
        value = null;
        error = null;
        if (arg == optionName)
        {
            if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
            {
                error = $"{optionName} requires a non-empty value.";
                return true;
            }
            value = args[++index];
            return true;
        }

        var prefix = optionName + "=";
        if (arg.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = arg[prefix.Length..];
            if (string.IsNullOrWhiteSpace(value))
                error = $"{optionName} requires a non-empty value.";
            return true;
        }

        return false;
    }

    private static int WriteError(string message, string hint, string usage)
        => CommandErrorWriter.Write(message, CommandExitCodes.UsageError, hint, usage);

    internal sealed record ExportManifest(string FormatVersion, string CdidxVersion, int UserVersion, string? ProjectRoot, string? IndexedHeadSha, string DatabaseSha256);
    internal sealed record ExportArchiveResult(string ApiVersion, string ArchivePath, string DbPath);
    internal sealed record ImportResult(string ApiVersion, string DbPath, bool PrunedPaths);
}
