using System.Text;
using CodeIndex.Database;
using CodeIndex.Indexer;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public partial class FileIndexerTests
{
    private static LoadedFileContent LoadFileContentForTest(byte[] bytes)
    {
        var tempDir = TestProjectHelper.CreateTempProject("codeindex_test");
        try
        {
            var path = Path.Combine(tempDir, "sample.cs");
            File.WriteAllBytes(path, bytes);

            var loader = new FileContentLoader(FileIndexer.DefaultMaxFileSizeBytes);
            return loader.Load(path, "sample.cs", "sample.cs", CancellationToken.None);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(tempDir);
        }
    }

    private static string RawSha256Hex(byte[] bytes)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    private static void WriteFileIndexerPatternConfig(string projectRoot, string fileName, string content)
    {
        var path = Path.Combine(projectRoot, ".cdidx", "patterns", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static int CountFiles(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM files";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static string ReadSingleChunkContent(string dbPath, string filePath)
    {
        using var db = new DbContext(dbPath);
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT c.content
            FROM chunks c
            JOIN files f ON f.id = c.file_id
            WHERE f.path = @path
            ORDER BY c.chunk_index
            """;
        cmd.Parameters.AddWithValue("@path", filePath);
        return Assert.IsType<string>(cmd.ExecuteScalar());
    }

    private static bool HasIndexedFile(string dbPath, string filePath)
    {
        using var db = new DbContext(dbPath);
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM files WHERE path = @path";
        cmd.Parameters.AddWithValue("@path", filePath);
        return cmd.ExecuteScalar() != null;
    }

    private static bool HasFileIssue(string dbPath, string filePath, string kind)
    {
        using var db = new DbContext(dbPath);
        using var cmd = db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT 1
            FROM file_issues i
            JOIN files f ON f.id = i.file_id
            WHERE f.path = @path
              AND i.kind = @kind
            """;
        cmd.Parameters.AddWithValue("@path", filePath);
        cmd.Parameters.AddWithValue("@kind", kind);
        return cmd.ExecuteScalar() != null;
    }

    private sealed class CancelAfterFirstReadStream(byte[] data, CancellationTokenSource cancellation) : Stream
    {
        private int offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position
        {
            get => offset;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int bufferOffset, int count)
        {
            if (offset >= data.Length)
                return 0;

            var read = Math.Min(count, data.Length - offset);
            Array.Copy(data, offset, buffer, bufferOffset, read);
            offset += read;
            cancellation.Cancel();
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long seekOffset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int bufferOffset, int count) => throw new NotSupportedException();
    }
}
