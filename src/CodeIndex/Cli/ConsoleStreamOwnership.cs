namespace CodeIndex.Cli;

/// <summary>
/// Serializes process-global <see cref="Console.Out"/> and <see cref="Console.Error"/>
/// replacement for the full lifetime of each capture.
/// </summary>
internal static class ConsoleStreamOwnership
{
    internal static object Gate { get; } = new();

    // Monitor ownership is intentionally reentrant because CLI scopes nest. The returned
    // scope must be disposed on the thread that entered it.
    internal static IDisposable Enter()
    {
        System.Threading.Monitor.Enter(Gate);
        return new OwnershipScope();
    }

    internal static void Restore(TextWriter originalOut, TextWriter originalError)
    {
        Console.SetError(originalError);
        Console.SetOut(originalOut);
        VerifyRestored(Console.Error, originalError, nameof(Console.Error));
        VerifyRestored(Console.Out, originalOut, nameof(Console.Out));
    }

    internal static void RestoreOut(TextWriter originalOut)
    {
        Console.SetOut(originalOut);
        VerifyRestored(Console.Out, originalOut, nameof(Console.Out));
    }

    internal static void RestoreError(TextWriter originalError)
    {
        Console.SetError(originalError);
        VerifyRestored(Console.Error, originalError, nameof(Console.Error));
    }

    private static void VerifyRestored(object actual, object expected, string streamName)
    {
        if (!ReferenceEquals(actual, expected))
        {
            throw new InvalidOperationException(
                $"{streamName} was not restored to the writer captured by the current owner.");
        }
    }

    private sealed class OwnershipScope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            System.Threading.Monitor.Exit(Gate);
        }
    }
}
