using System.IO.Pipes;
using System.Text;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

public class CancellableInstallerOutputStreamTests
{
    [Fact]
    public async Task WindowsSynchronousPipe_CancelsWithoutClosingWriterAndPreservesEncoding_Issue5320()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var writer = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        using var client = new AnonymousPipeClientStream(PipeDirection.In, writer.ClientSafePipeHandle);
        using var originalReader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        using var reader = CancellableInstallerOutputStream.CreateReader(originalReader);
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("日本語🙂")).ToArray();
        writer.Write(bytes);
        var text = new char[5];
        Assert.Equal(5, await reader.ReadBlockAsync(text).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("日本語🙂", new string(text));

        using var cancellation = new CancellationTokenSource();
        var pendingRead = reader.ReadAsync(new char[1], cancellation.Token).AsTask();
        Assert.False(pendingRead.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await pendingRead.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(writer.CanWrite);

        writer.Write(Encoding.Unicode.GetBytes("完了"));
        writer.Dispose();
        Assert.Equal("完了", await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
