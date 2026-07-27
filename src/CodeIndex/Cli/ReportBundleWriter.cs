using System.Formats.Tar;
using System.IO.Compression;

namespace CodeIndex.Cli;

internal static class ReportBundleWriter
{
    internal static void Write(
        string outputPath,
        ReportBundle bundle,
        bool overwrite,
        Action? beforeWriteEntries = null)
    {
        var fullOutputPath = Path.GetFullPath(outputPath);
        var dir = Path.GetDirectoryName(fullOutputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        void WriteContents(Stream stream)
        {
            using var gz = new GZipStream(stream, CompressionLevel.Optimal, leaveOpen: true);
            using var tar = new TarWriter(gz, TarEntryFormat.Pax, leaveOpen: true);
            beforeWriteEntries?.Invoke();

            foreach (var (name, bytes) in bundle.Files)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(bytes, writable: false),
                    Mode = ReportCommandRunner.BundleFileMode,
                    ModificationTime = ReportCommandRunner.BundleEntryModificationTime,
                };
                tar.WriteEntry(entry);
            }
        }

        if (overwrite)
        {
            AtomicFileWriter.WritePreservingExistingOnFailure(
                fullOutputPath,
                WriteContents,
                AtomicFileWriter.WriteProfile.Sensitive);
            return;
        }

        AtomicFileWriter.Write(
            fullOutputPath,
            WriteContents,
            AtomicFileWriter.WriteProfile.Sensitive,
            overwrite: false);
    }
}
