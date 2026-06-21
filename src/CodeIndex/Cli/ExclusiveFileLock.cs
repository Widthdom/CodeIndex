using System.Text;
using CodeIndex.Indexer;

namespace CodeIndex.Cli;

internal static class ExclusiveFileLock
{
    internal static FileStream Open(string lockPath)
    {
        var stream = new FileStream(
            LongPath.EnsureWindowsPrefix(lockPath),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        try
        {
            DataDirectorySecurity.ApplyPrivateFileMode(lockPath);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal static void WriteHolderInfo(string infoPath, string contents, Encoding? encoding = null)
        => DataDirectorySecurity.WritePrivateText(infoPath, contents, encoding);

    internal static bool TryReadHolderInfoText(string infoPath, int maxInfoBytes, out string? text)
    {
        text = null;
        var ioInfoPath = LongPath.EnsureWindowsPrefix(infoPath);
        if (!File.Exists(ioInfoPath))
            return false;

        text = DataDirectorySecurity.ReadTextWithinLimit(ioInfoPath, maxInfoBytes, FileShare.ReadWrite);
        return !string.IsNullOrWhiteSpace(text);
    }

    internal static void TryDeleteCleanupTarget(
        string path,
        string component,
        string target,
        Action<string> deleteFile,
        Action<LockCleanupDiagnostic>? cleanupDiagnosticSink)
    {
        try
        {
            deleteFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            var diagnostic = LockCleanupDiagnostic.Create(component, target, ex);
            GlobalToolLog.Error(diagnostic.ToLogMessage());
            cleanupDiagnosticSink?.Invoke(diagnostic);
        }
    }
}
