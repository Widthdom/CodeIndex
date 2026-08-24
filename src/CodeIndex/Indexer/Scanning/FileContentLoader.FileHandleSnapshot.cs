namespace CodeIndex.Indexer;

internal sealed partial class FileContentLoader
{
    private FileIndexer.FileHandleSnapshot CaptureFileHandleSnapshot(FileStream stream)
    {
        if (!FileIndexer.TryGetFileHandleSnapshot(
                stream.SafeFileHandle,
                out var snapshot))
        {
            var modifiedUtc = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
            var length = stream.Length;
            FileIndexer.FileIdentity? identity =
                FileIndexer.TryGetFileIdentity(stream.SafeFileHandle, out var capturedIdentity)
                    ? capturedIdentity
                    : null;
            snapshot = new FileIndexer.FileHandleSnapshot(
                length,
                modifiedUtc,
                identity);
        }

        _fileHandleSnapshotCapturedForTesting?.Invoke();
        return snapshot;
    }
}
