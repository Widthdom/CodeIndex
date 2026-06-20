namespace CodeIndex.Tests;

public class BoundedLineReaderTests
{
    [Fact]
    public async Task ReadLineAsync_BuffersRemainderAfterChunkRead_Issue3836()
    {
        using var reader = new StringReader("first line\nsecond line\n");

        var first = await BoundedLineReader.ReadLineAsync(reader, 100, 100, CancellationToken.None);
        var second = await BoundedLineReader.ReadLineAsync(reader, 100, 100, CancellationToken.None);
        var end = await BoundedLineReader.ReadLineAsync(reader, 100, 100, CancellationToken.None);

        Assert.Equal("first line", first);
        Assert.Equal("second line", second);
        Assert.Null(end);
    }

    [Fact]
    public void WorkerOutputBuffer_KeepsBoundedTail_Issue3836()
    {
        var buffer = new WorkerOutputBuffer(maxCharacters: 24, maxLines: 2, maxLineCharacters: 12);

        buffer.AppendLine("first-sensitive-line");
        buffer.AppendLine("second-line");
        buffer.AppendLine("third-line");

        var captured = buffer.GetCapturedText();

        Assert.Contains("worker stderr truncated", captured, StringComparison.Ordinal);
        Assert.DoesNotContain("first-sensitive", captured, StringComparison.Ordinal);
        Assert.Contains("third-line", captured, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatWorkerExit_IncludesSanitizedBoundedStderrTail_Issue3836()
    {
        var message = SafeDiagnosticFormatter.FormatWorkerExit(
            "worker_protocol_error",
            7,
            "worker exited before returning a response",
            "/private/secret/project/file.cs: raw stderr detail");

        Assert.Contains("worker exited with code 7", message, StringComparison.Ordinal);
        Assert.Contains("stderr_tail=", message, StringComparison.Ordinal);
        Assert.Contains("<path>", message, StringComparison.Ordinal);
        Assert.DoesNotContain("/private/secret", message, StringComparison.Ordinal);
    }
}
