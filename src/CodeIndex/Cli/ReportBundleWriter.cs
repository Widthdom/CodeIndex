using System.Formats.Tar;
using System.IO.Compression;

namespace CodeIndex.Cli;

internal static class ReportBundleWriter
{
    internal static void Write(string outputPath, ReportBundle bundle, Action? beforeWriteEntries = null)
    {
        var fullOutputPath = Path.GetFullPath(outputPath);
        var dir = Path.GetDirectoryName(fullOutputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        AtomicFileWriter.Write(
            fullOutputPath,
            stream =>
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
            },
            ApplyBundleFileMode);
    }

    private static void ApplyBundleFileMode(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, ReportCommandRunner.BundleFileMode);
    }
}
