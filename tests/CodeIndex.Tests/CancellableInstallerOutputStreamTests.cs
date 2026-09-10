using System.IO.Pipes;
using System.Text;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

public class CancellableInstallerOutputStreamTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnonymousPipe_DrainBoundaryPreservesEncodingAndReportsCompleteness_Issue5320(bool cancel)
    {
        using var writer = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        using var client = new AnonymousPipeClientStream(PipeDirection.In, writer.ClientSafePipeHandle);
        using var originalReader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        using var cancellation = new CancellationTokenSource();
        using var reader = CancellableInstallerOutputStream.CreateReader(originalReader, cancellation.Token, out var stream);
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("日本語🙂")).ToArray();
        writer.Write(bytes);
        var text = new char[5];
        Assert.Equal(5, await reader.ReadBlockAsync(text).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("日本語🙂", new string(text));

        var pendingRead = reader.ReadAsync(new char[1]).AsTask();
        Assert.False(pendingRead.IsCompleted);
        if (cancel)
            cancellation.Cancel();
        else
            writer.Dispose();
        Assert.Equal(0, await pendingRead.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(cancel, stream.Incomplete);
        if (cancel)
            Assert.True(writer.CanWrite);
    }
}
