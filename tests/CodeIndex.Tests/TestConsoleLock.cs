namespace CodeIndex.Tests;

[CollectionDefinition("Console sensitive", DisableParallelization = true)]
public sealed class ConsoleSensitiveCollection
{
}

internal static class TestConsoleLock
{
    internal static readonly object Gate = new();
}

internal sealed class ConsoleCapture : IDisposable
{
    private readonly TextWriter? originalOut;
    private readonly TextWriter? originalError;
    private readonly TextReader? originalIn;
    private readonly bool restoreOut;
    private readonly bool restoreError;
    private readonly bool restoreIn;
    private bool disposed;

    private ConsoleCapture(bool captureOut, bool captureError, TextWriter? outWriter = null, TextWriter? errorWriter = null, TextReader? inputReader = null)
    {
        restoreOut = captureOut;
        restoreError = captureError;
        restoreIn = inputReader is not null;
        if (captureOut)
            Out = outWriter ?? new StringWriter();
        if (captureError)
            Error = errorWriter ?? new StringWriter();

        System.Threading.Monitor.Enter(TestConsoleLock.Gate);
        try
        {
            if (captureOut)
            {
                originalOut = Console.Out;
                Console.SetOut(Out!);
            }

            if (captureError)
            {
                originalError = Console.Error;
                Console.SetError(Error!);
            }

            if (inputReader is not null)
            {
                originalIn = Console.In;
                Console.SetIn(inputReader);
            }
        }
        catch
        {
            Restore();
            System.Threading.Monitor.Exit(TestConsoleLock.Gate);
            throw;
        }
    }

    internal TextWriter? Out { get; }

    internal TextWriter? Error { get; }

    internal static ConsoleCapture Start(bool captureOut = false, bool captureError = false) => new(captureOut, captureError);

    internal static ConsoleCapture Start(TextWriter? output, TextWriter? error)
        => new(output is not null, error is not null, output, error);

    internal static ConsoleCapture StartWithInput(TextReader input, bool captureOut = false, bool captureError = false)
        => new(captureOut, captureError, inputReader: input);

    internal static string CaptureError(Action action)
    {
        using var capture = Start(captureError: true);
        action();
        return capture.Error!.ToString()!;
    }

    internal static (int ExitCode, string Stdout, string Stderr) Capture(Func<int> action)
    {
        using var capture = Start(captureOut: true, captureError: true);
        return (action(), capture.Out!.ToString()!, capture.Error!.ToString()!);
    }

    public void Dispose()
    {
        if (disposed)
            return;

        Restore();
        disposed = true;
        System.Threading.Monitor.Exit(TestConsoleLock.Gate);
    }

    private void Restore()
    {
        if (restoreIn && originalIn is not null)
            Console.SetIn(originalIn);
        if (restoreError && originalError is not null)
            Console.SetError(originalError);
        if (restoreOut && originalOut is not null)
            Console.SetOut(originalOut);
    }
}
