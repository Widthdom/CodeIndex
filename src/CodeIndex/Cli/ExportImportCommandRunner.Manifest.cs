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
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var userVersion = ReadSqliteUserVersion(connection);
        var projectRoot = ReadMetaString(connection, DbContext.IndexedProjectRootMetaKey);
        var indexedHead = ReadMetaString(connection, DbContext.IndexedHeadShaMetaKey);
        var unknownExtensionFiles = ReadUnknownExtensionFileSample(connection);
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
            UnknownExtensionFileCount: ReadMetaLong(connection, DbContext.UnknownExtensionFileCountMetaKey),
            UnknownExtensionFiles: unknownExtensionFiles.Files,
            UnknownExtensionFilesTruncated: ReadMetaBool(connection, DbContext.UnknownExtensionFilesTruncatedMetaKey),
            UnknownExtensionFilePathLimit: ReadMetaInt(connection, DbContext.UnknownExtensionFilePathLimitMetaKey),
            UnknownExtensionFileSampleCount: unknownExtensionFiles.Count,
            UnknownExtensionFileSampleLimit: unknownExtensionFiles.Limit,
            UnknownExtensionFileSampleTruncated: unknownExtensionFiles.Truncated,
            Scope: scope);
    }

    private static void AddTextEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
        entry.LastWriteTime = DeterministicZipTimestamp;
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    internal static void WriteExportArchiveFile(string outputPath, string snapshotPath, ExportManifest manifest, JsonSerializerOptions jsonOptions, CancellationToken cancellationToken)
    {
        var fullOutputPath = Path.GetFullPath(outputPath);
        AtomicFileWriter.Write(
            fullOutputPath,
            stream =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
                AddTextEntry(archive, ManifestEntryName, JsonSerializer.Serialize(manifest, jsonOptions));
                var dbEntry = archive.CreateEntry(DatabaseEntryName, CompressionLevel.SmallestSize);
                dbEntry.LastWriteTime = DeterministicZipTimestamp;
                using var source = BoundedFile.OpenReadTrustedArchiveSource(snapshotPath);
                using var target = dbEntry.Open();
                CopyToExactLength(source, target, source.Length, DatabaseEntryName, cancellationToken);
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

    private static string ComputeSha256(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = BoundedFile.OpenReadForHash(path);
        return Sha256StreamHasher.ComputeHex(stream, cancellationToken);
    }

}
