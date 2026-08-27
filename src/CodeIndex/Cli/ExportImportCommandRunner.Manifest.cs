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
    private static ExportManifest BuildManifest(
        SqliteConnection connection,
        string appVersion,
        ArchiveExportScopeResult scope,
        bool pathRedactionRequested,
        string[] pathRedactionOmittedCategories,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userVersion = ReadSqliteUserVersion(connection);
        var projectRoot = ReadMetaString(connection, DbContext.IndexedProjectRootMetaKey);
        var indexedHead = ReadMetaString(connection, DbContext.IndexedHeadShaMetaKey);
        var unknownExtensionDiagnosticsCurrent =
            ReadMetaInt(connection, DbContext.UnknownExtensionDiagnosticsVersionMetaKey)
            == DbContext.UnknownExtensionDiagnosticsVersion;
        var unknownExtensionFiles = unknownExtensionDiagnosticsCurrent
            ? ReadUnknownExtensionFileSample(connection)
            : default;
        var indexCompleteness = ReadMetaString(connection, DbContext.IndexCompletenessMetaKey);
        var indexIncompleteReasons = ReadArchiveIncompleteReasons(connection);
        cancellationToken.ThrowIfCancellationRequested();
        return new ExportManifest(
            "1",
            appVersion,
            userVersion,
            projectRoot,
            indexedHead,
            string.Empty,
            FileCount: ReadTableCount(connection, "files", cancellationToken),
            ChunkCount: ReadTableCount(connection, "chunks", cancellationToken),
            SymbolCount: ReadTableCount(connection, "symbols", cancellationToken),
            ReferenceCount: ReadTableCount(connection, "symbol_references", cancellationToken),
            GraphReady: (userVersion & DbContext.GraphReadyFlag) != 0,
            IssuesReady: (userVersion & DbContext.IssuesReadyFlag) != 0,
            FoldReady: (userVersion & DbContext.FoldReadyFlag) != 0,
            IndexWriterVersion: ReadMetaString(connection, DbContext.CdidxWriterVersionMetaKey),
            IndexedHeadBranch: ReadMetaString(connection, DbContext.IndexedHeadBranchMetaKey),
            IndexedHeadTimestamp: ReadMetaString(connection, DbContext.IndexedHeadTimestampMetaKey),
            CodeIndexMetaSchemaVersion: ReadMetaInt(connection, DbContext.CodeIndexMetaSchemaVersionMetaKey),
            CSharpSymbolNameContractVersion: ReadMetaInt(connection, DbContext.CSharpSymbolNameContractVersionMetaKey),
            SqlGraphContractVersion: ReadMetaInt(connection, DbContext.SqlGraphContractVersionMetaKey),
            HotspotFamilyVersion: ReadMetaInt(connection, DbContext.HotspotFamilyVersionMetaKey),
            UnknownExtensionFileCount: unknownExtensionDiagnosticsCurrent
                ? ReadMetaLong(connection, DbContext.UnknownExtensionFileCountMetaKey)
                : null,
            UnknownExtensionFiles: unknownExtensionFiles.Files,
            UnknownExtensionFilesTruncated: unknownExtensionDiagnosticsCurrent
                ? ReadMetaBool(connection, DbContext.UnknownExtensionFilesTruncatedMetaKey)
                : null,
            UnknownExtensionFilePathLimit: unknownExtensionDiagnosticsCurrent
                ? ReadMetaInt(connection, DbContext.UnknownExtensionFilePathLimitMetaKey)
                : null,
            UnknownExtensionFileSampleCount: unknownExtensionFiles.Count,
            UnknownExtensionFileSampleLimit: unknownExtensionFiles.Limit,
            UnknownExtensionFileSampleTruncated: unknownExtensionFiles.Truncated,
            IndexComplete: indexCompleteness == null
                ? null
                : string.Equals(indexCompleteness, "complete", StringComparison.Ordinal),
            IndexIncompleteReasons: indexIncompleteReasons,
            Scope: scope,
            PathRedactionRequested: pathRedactionRequested,
            PathRedactionComplete: pathRedactionRequested,
            PathRedactionOmittedCategories: pathRedactionOmittedCategories);
    }

    private static void AddTextEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
        entry.LastWriteTime = DeterministicZipTimestamp;
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    internal static (long SizeBytes, string Sha256)? WriteExportArchiveFile(
        string outputPath,
        string snapshotPath,
        ExportManifest manifest,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken,
        bool overwrite = true,
        bool includeArtifactMetadata = true,
        Action? beforePublishForTesting = null)
    {
        var fullOutputPath = Path.GetFullPath(outputPath);
        void WriteContents(Stream stream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            AddTextEntry(archive, ManifestEntryName, JsonSerializer.Serialize(manifest, jsonOptions));
            var dbEntry = archive.CreateEntry(DatabaseEntryName, CompressionLevel.SmallestSize);
            dbEntry.LastWriteTime = DeterministicZipTimestamp;
            using var source = BoundedFile.OpenReadTrustedArchiveSource(snapshotPath);
            using var target = dbEntry.Open();
            CopyToExactLength(source, target, source.Length, DatabaseEntryName, cancellationToken);
        }

        (long SizeBytes, string Sha256)? artifact = null;
        AtomicFileWriter.WriteWithPrePublishValidation(
            fullOutputPath,
            WriteContents,
            AtomicFileWriter.WriteProfile.Sensitive,
            overwrite,
            tempPath =>
            {
                VerifyPrivateArchiveMode(tempPath);
                if (includeArtifactMetadata)
                    artifact = ReadArchiveArtifactMetadata(tempPath, cancellationToken);
                beforePublishForTesting?.Invoke();
            });

        return artifact;
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

    private static string ComputeSha256(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = BoundedFile.OpenReadForHash(path);
        return Sha256StreamHasher.ComputeHex(stream, cancellationToken);
    }

    private static (long SizeBytes, string Sha256) ReadArchiveArtifactMetadata(
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = BoundedFile.OpenReadForHash(path);
        var sizeBytes = stream.Length;
        var sha256 = Sha256StreamHasher.ComputeHex(stream, cancellationToken);
        return (sizeBytes, sha256);
    }

    private static void VerifyPrivateArchiveMode(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        DataDirectorySecurity.ApplyPrivateFileMode(path);
        var mode = File.GetUnixFileMode(path) & DataDirectorySecurity.PermissionBits;
        if (mode != DataDirectorySecurity.PrivateFileMode)
            throw new UnauthorizedAccessException("export archive permissions could not be restricted to the current user");
    }

}
