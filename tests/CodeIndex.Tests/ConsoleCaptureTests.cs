namespace CodeIndex.Tests;

[Collection("Console sensitive")]
public class ConsoleCaptureTests
{
    [Fact]
    public void CaptureError_RestoresConsoleError_WhenActionThrows()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalError = Console.Error;

            var ex = Assert.Throws<InvalidOperationException>(() =>
                ConsoleCapture.CaptureError(() => throw new InvalidOperationException("boom")));

            Assert.Equal("boom", ex.Message);
            Assert.Same(originalError, Console.Error);
        }
    }

    [Fact]
    public void Capture_RestoresConsoleStreams_WhenActionThrows()
    {
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            var originalError = Console.Error;

            var ex = Assert.Throws<InvalidOperationException>(() =>
                ConsoleCapture.Capture(() => throw new InvalidOperationException("boom")));

            Assert.Equal("boom", ex.Message);
            Assert.Same(originalOut, Console.Out);
            Assert.Same(originalError, Console.Error);
        }
    }

    [Fact]
    public void Dispose_DoesNotCloseCapturedWriters()
    {
        lock (TestConsoleLock.Gate)
        {
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            using (var capture = ConsoleCapture.Start(stdout, stderr))
            {
                Console.Write("out");
                Console.Error.Write("err");
            }

            stdout.Write(" after");
            stderr.Write(" after");

            Assert.Equal("out after", stdout.ToString());
            Assert.Equal("err after", stderr.ToString());
        }
    }
}
